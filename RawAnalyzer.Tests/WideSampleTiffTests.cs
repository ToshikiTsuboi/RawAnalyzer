using System.Buffers.Binary;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 32bitサンプルのTIFFを読み込む経路。
/// </summary>
/// <remarks>
/// WICは32bitのページをサンプル形式によらずGray32Floatとして返し、整数のビット列を
/// そのまま実数として渡してくる。ファイルのSampleFormatで解釈することを実ファイルで
/// 確かめる。16bit実数はWICが正しく変換するので従来経路のままであることも確認する。
/// </remarks>
public class WideSampleTiffTests
{
    private const int Width = 4;
    private const int Height = 3;

    /// <summary>単一ストリップ・非圧縮の最小TIFFを作る(リトルエンディアン)。</summary>
    private static byte[] BuildTiff(
        byte[] samples, int width, int height, int bitsPerSample, int samplesPerPixel,
        int sampleFormat)
    {
        const int TagCount = 10;
        const int IfdOffset = 8;
        int ifdSize = 2 + (TagCount * 12) + 4;
        int arraysOffset = IfdOffset + ifdSize;
        bool arrays = samplesPerPixel > 1;
        int arraysSize = arrays ? samplesPerPixel * 2 * 2 : 0; // BitsPerSample + SampleFormat
        int dataOffset = arraysOffset + arraysSize;
        var file = new byte[dataOffset + samples.Length];

        file[0] = (byte)'I';
        file[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), IfdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(IfdOffset), TagCount);

        int entry = IfdOffset + 2;
        void Write(ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(entry), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(entry + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 4), count);
            if (type == 3 && count == 1)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(entry + 8), (ushort)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 8), value);
            }

            entry += 12;
        }

        uint bitsValue = arrays ? (uint)arraysOffset : (uint)bitsPerSample;
        uint formatValue = arrays ? (uint)(arraysOffset + (samplesPerPixel * 2)) : (uint)sampleFormat;
        Write(256, 3, 1, (uint)width);
        Write(257, 3, 1, (uint)height);
        Write(258, 3, (uint)samplesPerPixel, bitsValue);
        Write(259, 3, 1, 1); // Compression = none
        Write(262, 3, 1, samplesPerPixel == 1 ? 1u : 2u); // BlackIsZero / RGB
        Write(273, 4, 1, (uint)dataOffset);
        Write(277, 3, 1, (uint)samplesPerPixel);
        Write(278, 4, 1, (uint)height);
        Write(279, 4, 1, (uint)samples.Length);
        Write(339, 3, (uint)samplesPerPixel, formatValue);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry), 0); // 次のIFDなし

        if (arrays)
        {
            for (int i = 0; i < samplesPerPixel; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    file.AsSpan(arraysOffset + (i * 2)), (ushort)bitsPerSample);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    file.AsSpan(arraysOffset + (samplesPerPixel * 2) + (i * 2)), (ushort)sampleFormat);
            }
        }

        samples.CopyTo(file, dataOffset);
        return file;
    }

    private static byte[] FloatSamples(params float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }

    private static byte[] Int32Samples(params int[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }

    private static DecodedImage LoadTiff(byte[] file)
    {
        string dir = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tif");
        File.WriteAllBytes(path, file);
        try
        {
            return ImageFileLoader.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static float[] Ramp(float maximum)
    {
        var values = new float[Width * Height];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = maximum * i / (values.Length - 1);
        }

        return values;
    }

    [Fact]
    public void Float32Gray_Normalized_FillsFullRange()
    {
        byte[] file = BuildTiff(FloatSamples(Ramp(1f)), Width, Height, 32, 1, 3);

        DecodedImage decoded = Load(file);
        using RawImage owned = decoded.Luminance;
        RawImage image = decoded.Luminance;

        Assert.Null(decoded.Color);
        Assert.Equal(16, image.Format.BitDepth);
        Assert.Equal(0, image.GetPixel(0, 0));
        Assert.Equal(65535, image.GetPixel(Width - 1, Height - 1));
        Assert.NotNull(decoded.ValueNote);
        Assert.Contains("0〜1", decoded.ValueNote);
    }

    [Fact]
    public void UnsignedInt32Gray_IsNotReadAsFloat()
    {
        // WICはこのページもGray32Floatとして返す。実数として読むと
        // 1,000,000 が 1.4e-39 になり、全画素が真っ暗に潰れる
        byte[] file = BuildTiff(
            Int32Samples(0, 250_000, 500_000, 750_000, 1_000_000, 0, 0, 0, 0, 0, 0, 0),
            Width, Height, 32, 1, 1);

        DecodedImage decoded = Load(file);
        using RawImage owned = decoded.Luminance;

        Assert.Equal(0, decoded.Luminance.GetPixel(0, 0));
        Assert.Equal(65535, decoded.Luminance.GetPixel(0, 1)); // 1,000,000 が最大
        Assert.InRange(decoded.Luminance.GetPixel(1, 0), 16000, 16800); // 250,000 ≈ 1/4
    }

    [Fact]
    public void SignedInt32Gray_KeepsNegativeTail()
    {
        byte[] file = BuildTiff(
            Int32Samples(-1000, -500, 0, 500, 1000, 0, 0, 0, 0, 0, 0, 0),
            Width, Height, 32, 1, 2);

        DecodedImage decoded = Load(file);
        using RawImage owned = decoded.Luminance;

        Assert.Equal(0, decoded.Luminance.GetPixel(0, 0));       // -1000 が下端
        Assert.Equal(65535, decoded.Luminance.GetPixel(0, 1));   // +1000 が上端
        Assert.InRange(decoded.Luminance.GetPixel(2, 0), 32000, 33500); // 0 は中央
    }

    [Fact]
    public void Float32Rgb_KeepsChannelRatios()
    {
        var values = new float[Width * Height * 3];
        for (int i = 0; i < Width * Height; i++)
        {
            values[(i * 3) + 0] = 1.0f;
            values[(i * 3) + 1] = 0.5f;
            values[(i * 3) + 2] = 0.0f;
        }

        byte[] file = BuildTiff(FloatSamples(values), Width, Height, 32, 3, 3);

        DecodedImage decoded = Load(file);
        using RawImage owned = decoded.Luminance;

        Assert.NotNull(decoded.Color);
        decoded.Color!.GetPixel(1, 1, out ushort r, out ushort g, out ushort b);
        Assert.Equal(65535, r);
        Assert.InRange(g, 32000, 33500);
        Assert.Equal(0, b);
    }

    [Fact]
    public void Float16Gray_IsNotReadAsInteger()
    {
        // WICは半精度もGray16として生ビットで返す。1.0は15360という整数に見える
        var bytes = new byte[Width * Height * 2];
        float[] ramp = Ramp(1f);
        for (int i = 0; i < ramp.Length; i++)
        {
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(i * 2), (Half)ramp[i]);
        }

        byte[] file = BuildTiff(bytes, Width, Height, 16, 1, 3);

        DecodedImage decoded = Load(file);
        using RawImage owned = decoded.Luminance;

        Assert.Equal(0, decoded.Luminance.GetPixel(0, 0));
        Assert.Equal(65535, decoded.Luminance.GetPixel(Width - 1, Height - 1));
        Assert.True(
            decoded.Luminance.GetPixel(1, 0) > decoded.Luminance.GetPixel(0, 0),
            "階調が単調に増えること");
        Assert.NotNull(decoded.ValueNote);
    }

    private static DecodedImage Load(byte[] file) => LoadTiff(file);
}
