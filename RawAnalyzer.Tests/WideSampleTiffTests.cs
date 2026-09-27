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
/// 確かめる。16bit実数(半精度)のグレーもWICは生のビット列をGray16で返すが、RGBは
/// 0〜1へ切り詰めてガンマを掛けた整数(Rgb48)にしてしまうため、非圧縮なら自前で読む。
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

    private static byte[] HalfSamples(params float[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(i * 2), (Half)values[i]);
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

    [Fact]
    public void Float16Rgb_KeepsSampleValues()
    {
        // WICは半精度RGBを0〜1へ切り詰めてガンマを掛けたRgb48にするため、
        // 生のビット列として読み直すと (65535,65436,0) のような値になっていた
        var page = TiffBuilder.GrayPage(
            1, 1, 16, HalfSamples(0.25f, 0.5f, 0.75f), photometric: 2, sampleFormat: 3, samplesPerPixel: 3);

        DecodedImage decoded = Load(page);
        using RawImage owned = decoded.Luminance;

        Assert.NotNull(decoded.Color);
        decoded.Color!.GetPixel(0, 0, out ushort r, out ushort g, out ushort b);
        Assert.Equal(16384, r);
        Assert.Equal(32768, g);
        Assert.Equal(49151, b);
        Assert.Equal("16bit実数 0〜1 → 16bit", decoded.ValueNote);
    }

    [Theory]
    [InlineData(3, false, false)]
    [InlineData(4, false, false)]
    [InlineData(3, true, false)]
    [InlineData(4, true, false)]
    [InlineData(3, false, true)]
    public void Float16Rgb_ScalesLikeFloat32Rgb(int samplesPerPixel, bool planar, bool tiled)
    {
        // 1を超える値と負値を含む3×2画像。同じ値の32bit実数RGB(WICが生のビット列を返す経路)と
        // 同じコード・同じ換算になること。アルファ(4サンプル目)は値域に入れない
        const int width = 3;
        const int height = 2;
        float[] rgb =
        {
            2f, -0.5f, 100f, 0f, 1f, 50f, 0.25f, 0.75f, -2f,
            8f, 16f, 32f, 3f, 5f, 7f, 0.125f, 64f, -1f,
        };
        var samples = new float[width * height * samplesPerPixel];
        for (int i = 0; i < width * height; i++)
        {
            for (int c = 0; c < samplesPerPixel; c++)
            {
                samples[(i * samplesPerPixel) + c] = c < 3 ? rgb[(i * 3) + c] : 1000f;
            }
        }

        TiffBuilder.Page half = tiled
            ? TiledRgbPage(width, height, samplesPerPixel, HalfSamples(samples), 2, 2)
            : planar
                ? PlanarRgbPage(width, height, samplesPerPixel, samples)
                : TiffBuilder.GrayPage(width, height, 16, HalfSamples(samples),
                    photometric: 2, sampleFormat: 3, samplesPerPixel: samplesPerPixel);
        if (samplesPerPixel == 4)
        {
            half.Tags[338] = (3, new long[] { 2 }); // ExtraSamples = unassociated alpha
        }

        var single = TiffBuilder.GrayPage(
            width, height, 32, FloatSamples(rgb), photometric: 2, sampleFormat: 3, samplesPerPixel: 3);

        DecodedImage expected = Load(single);
        using RawImage expectedOwned = expected.Luminance;
        DecodedImage actual = Load(half);
        using RawImage actualOwned = actual.Luminance;

        Assert.NotNull(actual.Color);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                expected.Color!.GetPixel(x, y, out ushort er, out ushort eg, out ushort eb);
                actual.Color!.GetPixel(x, y, out ushort ar, out ushort ag, out ushort ab);
                Assert.Equal((er, eg, eb), (ar, ag, ab));
                Assert.Equal(expected.Luminance.GetPixel(x, y), actual.Luminance.GetPixel(x, y));
            }
        }

        Assert.Equal("32bit値 -2〜100 → 16bit (1code≈0.00156)", expected.ValueNote);
        Assert.Equal("16bit値 -2〜100 → 16bit (1code≈0.00156)", actual.ValueNote);
    }

    [Fact]
    public void Float16Rgb_Compressed_IsRejectedWithReason()
    {
        // 圧縮ページは自前では復号できず、WICの返す値は切り詰め・ガンマ変換済みで元に戻せない
        var page = TiffBuilder.GrayPage(
            1, 1, 16, HalfSamples(0.25f, 0.5f, 0.75f), photometric: 2, sampleFormat: 3, samplesPerPixel: 3);
        Deflate(page);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains("半精度", ex.Message);
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(4, 2)] // 非関連アルファ(WICはBgra32)
    [InlineData(4, 1)] // 関連アルファ(WICはPbgra32)
    public void SignedInt8Rgb_KeepsChannelOrder(int samplesPerPixel, int extraSamples)
    {
        // WICは符号あり8bitのRGBをB,G,Rの順(Bgr24/Bgra32/Pbgra32)で返す。
        // 並べ替えずにRGBとして読むと赤と青が入れ替わる
        sbyte[] values = { 10, 20, 30, 40 };
        byte[] samples = Array.ConvertAll(values[..samplesPerPixel], v => (byte)v);
        var page = TiffBuilder.GrayPage(
            1, 1, 8, samples, photometric: 2, sampleFormat: 2, samplesPerPixel: samplesPerPixel);
        if (extraSamples != 0)
        {
            page.Tags[338] = (3, new long[] { extraSamples });
        }

        DecodedImage decoded = Load(page);
        using RawImage owned = decoded.Luminance;

        Assert.NotNull(decoded.Color);
        decoded.Color!.GetPixel(0, 0, out ushort r, out ushort g, out ushort b);
        Assert.Equal(21845, r);
        Assert.Equal(43690, g);
        Assert.Equal(65535, b);
        Assert.Equal("8bit値 0〜30 → 16bit (1code≈0.000458)", decoded.ValueNote);
    }

    /// <summary>PlanarConfiguration=2(成分ごとに1ストリップ)の半精度RGB(A)ページ。</summary>
    private static TiffBuilder.Page PlanarRgbPage(int width, int height, int samplesPerPixel, float[] interleaved)
    {
        var page = TiffBuilder.GrayPage(width, height, 16, HalfSamples(interleaved),
            photometric: 2, sampleFormat: 3, samplesPerPixel: samplesPerPixel);
        page.Blocks.Clear();
        int pixels = width * height;
        for (int c = 0; c < samplesPerPixel; c++)
        {
            var plane = new float[pixels];
            for (int i = 0; i < pixels; i++)
            {
                plane[i] = interleaved[(i * samplesPerPixel) + c];
            }

            page.Blocks.Add(HalfSamples(plane));
        }

        page.Tags[284] = (3, new long[] { 2 });
        return page;
    }

    /// <summary>チャンキーの半精度RGB(A)をタイルにしたページ(1画素ぶんのバイト列をタイルへ並べ、タグをRGBへ直す)。</summary>
    private static TiffBuilder.Page TiledRgbPage(
        int width, int height, int samplesPerPixel, byte[] samples, int tileWidth, int tileHeight)
    {
        var page = TiffBuilder.TiledGrayPage(width, height, 16 * samplesPerPixel, samples, tileWidth, tileHeight);
        page.Tags[258] = (3, Enumerable.Repeat(16L, samplesPerPixel).ToArray());
        page.Tags[262] = (3, new long[] { 2 });
        page.Tags[277] = (3, new long[] { samplesPerPixel });
        page.Tags[339] = (3, Enumerable.Repeat(3L, samplesPerPixel).ToArray());
        return page;
    }

    /// <summary>各ストリップ/タイルをzlibで圧縮し、Compression=8(Deflate)にする。</summary>
    private static void Deflate(TiffBuilder.Page page)
    {
        for (int i = 0; i < page.Blocks.Count; i++)
        {
            using var output = new MemoryStream();
            using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(page.Blocks[i]);
            }

            page.Blocks[i] = output.ToArray();
        }

        page.Tags[259] = (3, new long[] { 8 });
    }

    /// <summary>単一ページのリトルエンディアンTIFFを一時ファイルに書き、本番経路で読み込む。</summary>
    private static DecodedImage Load(TiffBuilder.Page page)
    {
        using var file = TempTiff.Write(new TiffBuilder().Build(page));
        return ImageFileLoader.Load(file.Path);
    }
}
