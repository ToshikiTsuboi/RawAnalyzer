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
        var page = TiffBuilder.GrayPage(Width, Height, 32, FloatSamples(Ramp(1f)), sampleFormat: 3);

        DecodedImage decoded = Load(page);
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
        var page = TiffBuilder.GrayPage(Width, Height, 32,
            Int32Samples(0, 250_000, 500_000, 750_000, 1_000_000, 0, 0, 0, 0, 0, 0, 0));
        page.Tags[339] = (3, new long[] { 1 }); // SampleFormat=符号なし整数 を明示する(省略時の既定と同じ)

        DecodedImage decoded = Load(page);
        using RawImage owned = decoded.Luminance;

        Assert.Equal(0, decoded.Luminance.GetPixel(0, 0));
        Assert.Equal(65535, decoded.Luminance.GetPixel(0, 1)); // 1,000,000 が最大
        Assert.InRange(decoded.Luminance.GetPixel(1, 0), 16000, 16800); // 250,000 ≈ 1/4
    }

    [Fact]
    public void SignedInt32Gray_KeepsNegativeTail()
    {
        var page = TiffBuilder.GrayPage(Width, Height, 32,
            Int32Samples(-1000, -500, 0, 500, 1000, 0, 0, 0, 0, 0, 0, 0), sampleFormat: 2);

        DecodedImage decoded = Load(page);
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

        var page = TiffBuilder.GrayPage(
            Width, Height, 32, FloatSamples(values), photometric: 2, sampleFormat: 3, samplesPerPixel: 3);

        DecodedImage decoded = Load(page);
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

        var page = TiffBuilder.GrayPage(Width, Height, 16, bytes, sampleFormat: 3);

        DecodedImage decoded = Load(page);
        using RawImage owned = decoded.Luminance;

        Assert.Equal(0, decoded.Luminance.GetPixel(0, 0));
        Assert.Equal(65535, decoded.Luminance.GetPixel(Width - 1, Height - 1));
        Assert.True(
            decoded.Luminance.GetPixel(1, 0) > decoded.Luminance.GetPixel(0, 0),
            "階調が単調に増えること");
        Assert.NotNull(decoded.ValueNote);
    }

    /// <summary>単一ページのリトルエンディアンTIFFを一時ファイルに書き、本番経路で読み込む。</summary>
    private static DecodedImage Load(TiffBuilder.Page page)
    {
        using var file = TempTiff.Write(new TiffBuilder().Build(page));
        return ImageFileLoader.Load(file.Path);
    }
}
