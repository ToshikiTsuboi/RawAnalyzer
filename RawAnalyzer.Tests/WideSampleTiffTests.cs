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

    private static byte[] DoubleSamples(params float[] values)
    {
        var bytes = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), values[i]);
        }

        return bytes;
    }

    /// <summary>整数値の下位 bits ビットをリトルエンディアンで並べる(8/16/24/32/64bit)。</summary>
    private static byte[] IntegerSamples(int bits, params long[] values)
    {
        int bytesPer = bits / 8;
        var bytes = new byte[values.Length * bytesPer];
        for (int i = 0; i < values.Length; i++)
        {
            for (int k = 0; k < bytesPer; k++)
            {
                bytes[(i * bytesPer) + k] = (byte)(values[i] >> (8 * k));
            }
        }

        return bytes;
    }

    private static int[] Row(RawImage image)
    {
        return Enumerable.Range(0, image.Width).Select(x => (int)image.GetPixel(x, 0)).ToArray();
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

    /// <summary>値を指定のビット幅・サンプル形式のサンプル列にする(実数は16/32/64bit、整数は下位ビット)。</summary>
    private static byte[] TypedSamples(int bits, int sampleFormat, params float[] values)
    {
        if (sampleFormat != 3)
        {
            return IntegerSamples(bits, Array.ConvertAll(values, v => (long)v));
        }

        return bits switch { 16 => HalfSamples(values), 32 => FloatSamples(values), _ => DoubleSamples(values) };
    }

    /// <summary>
    /// 1行の多チャネルのグレー(チャンキー)。先頭チャネルは first、残りのチャネルはすべて rest。
    /// extraSamples が0以上なら、追加サンプルすべてにその値の ExtraSamples タグを付ける(-1 は付けない)。
    /// </summary>
    private static TiffBuilder.Page MultiChannelGrayPage(
        int bits, int sampleFormat, int samplesPerPixel, int extraSamples, float[] first, float rest,
        int photometric = 1)
    {
        var values = new float[first.Length * samplesPerPixel];
        for (int i = 0; i < first.Length; i++)
        {
            for (int c = 0; c < samplesPerPixel; c++)
            {
                values[(i * samplesPerPixel) + c] = c == 0 ? first[i] : rest;
            }
        }

        var page = TiffBuilder.GrayPage(first.Length, 1, bits, TypedSamples(bits, sampleFormat, values),
            photometric: photometric, sampleFormat: sampleFormat, samplesPerPixel: samplesPerPixel);
        if (extraSamples >= 0)
        {
            page.Tags[338] = (3, Enumerable.Repeat((long)extraSamples, samplesPerPixel - 1).ToArray());
        }

        return page;
    }

    /// <summary>
    /// 1行5画素の、色(グレーは1チャネル、RGBは3チャネル)+アルファ(+残りは7)の符号なし整数ページ。
    /// アルファは先頭4画素が100(16bitは1000)、最後の画素が0。追加サンプルすべてに extraSamples を付ける。
    /// </summary>
    private static TiffBuilder.Page AlphaPage(
        int bits, int photometric, int samplesPerPixel, long[] color, int colorChannels, int extraSamples,
        bool compressed)
    {
        const int pixels = 5;
        long alpha = bits == 8 ? 100 : 1000;
        var values = new long[pixels * samplesPerPixel];
        for (int i = 0; i < pixels; i++)
        {
            for (int c = 0; c < samplesPerPixel; c++)
            {
                values[(i * samplesPerPixel) + c] = c < colorChannels ? color[(i * colorChannels) + c]
                    : c > colorChannels ? 7
                    : i == pixels - 1 ? 0 : alpha;
            }
        }

        var page = TiffBuilder.GrayPage(pixels, 1, bits, IntegerSamples(bits, values),
            photometric: photometric, samplesPerPixel: samplesPerPixel);
        page.Tags[338] = (3, Enumerable.Repeat((long)extraSamples, samplesPerPixel - colorChannels).ToArray());
        if (compressed)
        {
            TiffBuilder.Deflate(page);
        }

        return page;
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
            ? TiledPage(width, height, 16, 3, samplesPerPixel, HalfSamples(samples), 2, 2)
            : planar
                ? PlanarPage(width, height, 16, 3, samplesPerPixel, HalfSamples(samples))
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
        TiffBuilder.Deflate(page);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains("半精度", ex.Message);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(3, true)]
    public void Float64Rgb_ScalesLikeFloat32Rgb(int samplesPerPixel, bool tiled)
    {
        // 以前は非圧縮なのに「64bit 実数の非圧縮(1)TIFFは未対応です(非圧縮であれば読めます)」という
        // 矛盾したエラーで開けなかった。自前で復号し、同じ値の32bit実数RGB(WIC経路)と同じコード・
        // 同じ換算になること。アルファ(4サンプル目の1000)は値域に入れない
        // (プレーン分離の読み方はビット幅によらず、SignedInt16Rgb_ScalesLikeSignedInt32Rgb が見る)
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

        byte[] chunky = DoubleSamples(samples);
        TiffBuilder.Page page = tiled
            ? TiledPage(width, height, 64, 3, samplesPerPixel, chunky, 2, 2)
            : TiffBuilder.GrayPage(width, height, 64, chunky,
                photometric: 2, sampleFormat: 3, samplesPerPixel: samplesPerPixel);
        if (samplesPerPixel == 4)
        {
            page.Tags[338] = (3, new long[] { 2 });
        }

        DecodedImage expected = Load(TiffBuilder.GrayPage(
            width, height, 32, FloatSamples(rgb), photometric: 2, sampleFormat: 3, samplesPerPixel: 3));
        using RawImage expectedOwned = expected.Luminance;
        DecodedImage actual = Load(page);
        using RawImage actualOwned = actual.Luminance;

        AssertSameColor(expected, actual);
        Assert.Equal("32bit値 -2〜100 → 16bit (1code≈0.00156)", expected.ValueNote);
        Assert.Equal("64bit値 -2〜100 → 16bit (1code≈0.00156)", actual.ValueNote);
    }

    [Theory]
    [InlineData(24, 1)]
    [InlineData(64, 1)]
    [InlineData(64, 2)]
    public void WideIntegerRgb_ScalesLikeInt32Rgb(int bits, int sampleFormat)
    {
        // 24bit・64bit整数のRGBもWICは復号できず、自前復号も呼ばれないため非圧縮でも開けなかった
        // (エラー文は64bit実数と同じく「非圧縮であれば読めます」の矛盾したもの)。
        // 同じ値の32bit整数RGB(WIC経路)と同じコード・換算になること
        long[] rgb = sampleFormat == 2
            ? new long[] { -1000, 0, 1000, 250, -250, 500 }
            : new long[] { 0, 500, 1000, 250, 750, 125 };
        DecodedImage expected = Load(TiffBuilder.GrayPage(
            2, 1, 32, IntegerSamples(32, rgb), photometric: 2, sampleFormat: sampleFormat, samplesPerPixel: 3));
        using RawImage expectedOwned = expected.Luminance;
        DecodedImage actual = Load(TiffBuilder.GrayPage(
            2, 1, bits, IntegerSamples(bits, rgb), photometric: 2, sampleFormat: sampleFormat, samplesPerPixel: 3));
        using RawImage actualOwned = actual.Luminance;

        AssertSameColor(expected, actual);
        Assert.StartsWith("32bit値 ", expected.ValueNote);
        Assert.Equal(expected.ValueNote!.Replace("32bit値", $"{bits}bit値"), actual.ValueNote);
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

    [Theory]
    [InlineData(3, false, false)]
    [InlineData(4, false, false)]
    [InlineData(3, true, false)]
    [InlineData(4, true, false)]
    [InlineData(3, false, true)]
    [InlineData(4, false, true)]
    public void SignedInt16Rgb_ScalesLikeSignedInt32Rgb(int samplesPerPixel, bool planar, bool tiled)
    {
        // WICは符号あり16bitのRGB/RGBAを復号できず、読込エラーになっていた。非圧縮なら自前で読み、
        // 同じ値の32bit符号ありRGB(WICが生のビット列を返す経路)と同じコード・同じ換算になること。
        // アルファ(4サンプル目、RGBの値域外の30000)は値域に入れない
        const int width = 3;
        const int height = 2;
        long[] rgb =
        {
            -1000, 0, 1000, 250, -250, 500, 7, -7, 0,
            -32, 64, -128, 999, -999, 1, 300, 200, 100,
        };
        var samples = new long[width * height * samplesPerPixel];
        for (int i = 0; i < width * height; i++)
        {
            for (int c = 0; c < samplesPerPixel; c++)
            {
                samples[(i * samplesPerPixel) + c] = c < 3 ? rgb[(i * 3) + c] : 30000;
            }
        }

        byte[] chunky = IntegerSamples(16, samples);
        TiffBuilder.Page page = tiled
            ? TiledPage(width, height, 16, 2, samplesPerPixel, chunky, 2, 2)
            : planar
                ? PlanarPage(width, height, 16, 2, samplesPerPixel, chunky)
                : TiffBuilder.GrayPage(width, height, 16, chunky,
                    photometric: 2, sampleFormat: 2, samplesPerPixel: samplesPerPixel);
        if (samplesPerPixel == 4)
        {
            page.Tags[338] = (3, new long[] { 2 }); // ExtraSamples = unassociated alpha
        }

        DecodedImage expected = Load(TiffBuilder.GrayPage(
            width, height, 32, IntegerSamples(32, rgb), photometric: 2, sampleFormat: 2, samplesPerPixel: 3));
        using RawImage expectedOwned = expected.Luminance;
        DecodedImage actual = Load(page);
        using RawImage actualOwned = actual.Luminance;

        AssertSameColor(expected, actual);
        actual.Color!.GetPixel(0, 0, out ushort r, out ushort g, out ushort b);
        Assert.Equal(0, r);       // -1000 が下端
        Assert.Equal(32768, g);   // 0 が中央
        Assert.Equal(65535, b);   // 1000 が上端
        Assert.Equal("32bit値 -1000〜1000 → 16bit (1code≈0.0305)", expected.ValueNote);
        Assert.Equal("16bit値 -1000〜1000 → 16bit (1code≈0.0305)", actual.ValueNote);
    }

    [Fact]
    public void SignedInt16Rgb_Compressed_IsRejectedWithReason()
    {
        // WICは符号あり16bitのRGBを復号できない(以前は「イメージが破損している可能性があります」という
        // WICの汎用の文言だった)。自前で読めない圧縮ページは、開く前に理由付きのエラーにする
        // (判定はサンプル数を見ない。RGBAは SignedInt16Rgb_ScalesLikeSignedInt32Rgb の非圧縮の行が同じ判定を通す)
        var page = TiffBuilder.GrayPage(1, 1, 16, IntegerSamples(16, -100, 0, 100),
            photometric: 2, sampleFormat: 2, samplesPerPixel: 3);
        TiffBuilder.Deflate(page);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains("16bit符号あり整数", ex.Message);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void FloatWhiteIsZero_IsInvertedAtAnyWidth(int bits)
    {
        // 以前は64bitは反転されず [0,32768,65535]、16bitはWICのビット反転で非数や別の値になっていた
        float[] values = { 0f, 0.5f, 1f };
        byte[] samples = bits switch { 16 => HalfSamples(values), 32 => FloatSamples(values), _ => DoubleSamples(values) };

        DecodedImage decoded = Load(TiffBuilder.GrayPage(3, 1, bits, samples, photometric: 0, sampleFormat: 3));
        using RawImage owned = decoded.Luminance;

        Assert.Equal(new[] { 65535, 32768, 0 }, Row(decoded.Luminance));
        Assert.Equal($"{bits}bit実数 0〜1 → 16bit", decoded.ValueNote);
    }

    [Fact]
    public void FloatWhiteIsZero_BeyondOneAndNegative_MatchesFloat32()
    {
        // 32bit実数のWhiteIsZeroはWICが 1−v で返す。自前復号(半精度)でも範囲外の値で同じ向き・同じ換算に
        // なること(64bitも自前復号の同じ反転を通る)
        float[] values = { 0f, 0.5f, 1f, 2f, -1f, 4f };

        DecodedImage expected = Load(TiffBuilder.GrayPage(6, 1, 32, FloatSamples(values), photometric: 0, sampleFormat: 3));
        using RawImage expectedOwned = expected.Luminance;
        DecodedImage actual = Load(TiffBuilder.GrayPage(6, 1, 16, HalfSamples(values), photometric: 0, sampleFormat: 3));
        using RawImage actualOwned = actual.Luminance;

        // 1−v = [1, 0.5, 0, −1, 2, −3] を −3〜2 で写す
        Assert.Equal(new[] { 52428, 45874, 39321, 26214, 65535, 0 }, Row(expected.Luminance));
        Assert.Equal(Row(expected.Luminance), Row(actual.Luminance));
        Assert.Equal("32bit値 -3〜2 → 16bit (1code≈7.63E-05)", expected.ValueNote);
        Assert.Equal("16bit値 -3〜2 → 16bit (1code≈7.63E-05)", actual.ValueNote);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void SignedWhiteIsZero_IsInvertedAtAnyWidth(int bits)
    {
        // 符号ありは全ビット反転(−1−v。WICが8/16bitで返す向き)にそろえる。
        // 以前は非圧縮の8/64bitが反転されず、32bitはWICが実数として 1−v して値が壊れていた
        DecodedImage decoded = Load(TiffBuilder.GrayPage(
            3, 1, bits, IntegerSamples(bits, -100, 0, 100), photometric: 0, sampleFormat: 2));
        using RawImage owned = decoded.Luminance;

        Assert.Equal(new[] { 65535, 32768, 0 }, Row(decoded.Luminance));
        Assert.Equal($"{bits}bit値 -101〜99 → 16bit (1code≈0.00305)", decoded.ValueNote);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    public void UnsignedWideWhiteIsZero_IsInverted(int bits)
    {
        // 32bit符号なしはWICが実数として 1−v して全画素が同じ値に潰れていた。24bitと同じく 最大値−v で反転する
        long max = (1L << bits) - 1;
        DecodedImage decoded = Load(TiffBuilder.GrayPage(
            3, 1, bits, IntegerSamples(bits, 0, 1L << (bits - 1), max), photometric: 0));
        using RawImage owned = decoded.Luminance;

        Assert.Equal(new[] { 65535, 32767, 0 }, Row(decoded.Luminance));
        Assert.StartsWith($"{bits}bit値 0〜", decoded.ValueNote);
    }

    [Theory]
    [InlineData(16, 3)]
    [InlineData(32, 1)]
    public void WhiteIsZero_CompressedHalfOrInt32_IsRejectedWithReason(int bits, int sampleFormat)
    {
        // WICはこれらのWhiteIsZeroをビット反転・実数の 1−v で壊して返す。自前で読めない圧縮ページは開かない
        byte[] samples = sampleFormat == 3 ? HalfSamples(0f, 0.5f, 1f) : IntegerSamples(bits, 1, 100, 1000);
        var page = TiffBuilder.GrayPage(3, 1, bits, samples, photometric: 0, sampleFormat: sampleFormat);
        TiffBuilder.Deflate(page);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains("WhiteIsZero", ex.Message);
    }

    [Theory]
    [InlineData(16, 3)] // WICはRgba64
    [InlineData(16, 2)]
    [InlineData(8, 2)] // WICはBgra32
    public void GrayWithAlpha_WideSamples_OpensFirstChannelAsGray(int bits, int sampleFormat)
    {
        // WICは16/8bitのグレー+アルファをRGBA(R=G=B=グレー)に展開して返すため、元タグの2サンプル分の
        // 画素幅と合わず読込エラーになっていた。先頭チャネルだけをグレーとして開き、
        // アルファ(100)は値域に入れない(非圧縮の32bitは自前復号へ回り、
        // MultiChannelGray_WicUnreadable_OpensFirstChannelAsGray の32bitの行が見る)
        float[] grayAndAlpha = { 0f, 100f, 10f, 100f, 20f, 100f };
        byte[] samples = sampleFormat != 3
            ? IntegerSamples(bits, Array.ConvertAll(grayAndAlpha, v => (long)v))
            : bits == 16 ? HalfSamples(grayAndAlpha) : FloatSamples(grayAndAlpha);
        var page = TiffBuilder.GrayPage(3, 1, bits, samples, sampleFormat: sampleFormat, samplesPerPixel: 2);
        page.Tags[338] = (3, new long[] { 2 }); // ExtraSamples = unassociated alpha

        DecodedImage decoded = Load(page);
        using RawImage owned = decoded.Luminance;

        Assert.Null(decoded.Color);
        Assert.Equal(16, decoded.Luminance.Format.BitDepth);
        Assert.Equal(new[] { 0, 32768, 65535 }, Row(decoded.Luminance));
        Assert.Equal($"{bits}bit値 0〜20 → 16bit (1code≈0.000305)", decoded.ValueNote);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(2)]
    public void MultiChannelGray16_WideSamples_OpensFirstChannelAsGray(int sampleFormat)
    {
        // 3サンプルのグレー(ExtraSamples=未指定)をWICは先頭チャネルだけのGray16で返す
        float[] values = { 0f, 7f, 7f, 10f, 7f, 7f, 20f, 7f, 7f };
        byte[] samples = sampleFormat == 3
            ? HalfSamples(values)
            : IntegerSamples(16, Array.ConvertAll(values, v => (long)v));
        var page = TiffBuilder.GrayPage(3, 1, 16, samples, sampleFormat: sampleFormat, samplesPerPixel: 3);
        page.Tags[338] = (3, new long[] { 0, 0 });

        DecodedImage decoded = Load(page);
        using RawImage owned = decoded.Luminance;

        Assert.Null(decoded.Color);
        Assert.Equal(new[] { 0, 32768, 65535 }, Row(decoded.Luminance));
        Assert.Equal("16bit値 0〜20 → 16bit (1code≈0.000305)", decoded.ValueNote);
    }

    [Theory]
    [InlineData(32, 3, 3, -1)]
    [InlineData(32, 3, 5, 2)]
    [InlineData(32, 1, 3, 2)]
    [InlineData(32, 2, 4, 0)]
    [InlineData(32, 3, 2, 0)] // 2サンプルでも追加サンプルが未指定(ExtraSamples=0)だとWICは復号できない
    [InlineData(64, 3, 2, 2)] // 64/24bitの多チャネルは以前「非圧縮であれば読めます」の矛盾したエラーだった
    [InlineData(64, 2, 3, 0)]
    [InlineData(64, 1, 3, -1)]
    [InlineData(24, 1, 2, 2)]
    public void MultiChannelGray_WicUnreadable_OpensFirstChannelAsGray(
        int bits, int sampleFormat, int samplesPerPixel, int extraSamples)
    {
        // WICは32bitの3サンプル以上のグレー(2サンプルでも追加サンプルが未指定のもの)を復号できず、
        // 24/64bitの多チャネルも読めない。非圧縮なら自前で先頭チャネルだけを読み、1サンプルのページと
        // 同じ値域換算にする(残りのチャネルの100は値域に入れない)
        float[] first = { 0f, 10f, 20f };
        DecodedImage single = Load(TiffBuilder.GrayPage(
            3, 1, bits, TypedSamples(bits, sampleFormat, first), sampleFormat: sampleFormat));
        using RawImage singleOwned = single.Luminance;
        DecodedImage decoded = Load(MultiChannelGrayPage(bits, sampleFormat, samplesPerPixel, extraSamples, first, 100f));
        using RawImage owned = decoded.Luminance;

        Assert.Null(decoded.Color);
        Assert.Equal(16, decoded.Luminance.Format.BitDepth);
        Assert.Equal(new[] { 0, 32768, 65535 }, Row(decoded.Luminance));
        Assert.Equal($"{bits}bit値 0〜20 → 16bit (1code≈0.000305)", decoded.ValueNote);
        Assert.Equal(Row(single.Luminance), Row(decoded.Luminance));
        Assert.Equal(single.ValueNote, decoded.ValueNote);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MultiChannelGray32_PlanarOrTiled_OpensFirstChannelAsGray(bool planar)
    {
        // プレーン分離(planar)は先頭プレーンだけ、タイル(!planar)はタイルごとに先頭チャネルを読む
        const int width = 3;
        const int height = 2;
        float[] first = { 0f, 4f, 8f, 12f, 16f, 20f };
        var chunky = new float[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            chunky[i * 3] = first[i];
            chunky[(i * 3) + 1] = 100f;
            chunky[(i * 3) + 2] = -100f;
        }

        byte[] bytes = FloatSamples(chunky);
        TiffBuilder.Page page = planar
            ? PlanarPage(width, height, 32, 3, 3, bytes, photometric: 1)
            : TiledPage(width, height, 32, 3, 3, bytes, 2, 2, photometric: 1);
        page.Tags[338] = (3, new long[] { 0, 0 });

        DecodedImage decoded = Load(page);
        using RawImage owned = decoded.Luminance;

        Assert.Null(decoded.Color);
        Assert.Equal(new[] { 0, 13107, 26214 }, Row(decoded.Luminance));
        Assert.Equal(65535, decoded.Luminance.GetPixel(width - 1, height - 1));
        Assert.Equal("32bit値 0〜20 → 16bit (1code≈0.000305)", decoded.ValueNote);
    }

    [Theory]
    [InlineData(32, 3, 3, 2)] // WICが復号できない
    [InlineData(32, 2, 2, 2)] // 以前は理由付きエラー(WICの反転が壊れる32bit整数)
    [InlineData(16, 3, 2, 2)] // 以前は理由付きエラー(WICの反転が壊れる半精度)
    [InlineData(64, 3, 3, 2)]
    public void MultiChannelGrayWhiteIsZero_MatchesSingleSample(
        int bits, int sampleFormat, int samplesPerPixel, int extraSamples)
    {
        // 多チャネルのグレーのWhiteIsZeroも、1サンプルのページと同じ規則(整数は全ビット反転、実数は 1−v)で
        // 反転してから値域を調べる
        float[] first = sampleFormat == 3 ? new[] { 0f, 0.5f, 1f } : new[] { 0f, 10f, 20f };
        DecodedImage single = Load(TiffBuilder.GrayPage(
            3, 1, bits, TypedSamples(bits, sampleFormat, first), photometric: 0, sampleFormat: sampleFormat));
        using RawImage singleOwned = single.Luminance;
        DecodedImage decoded = Load(MultiChannelGrayPage(
            bits, sampleFormat, samplesPerPixel, extraSamples, first, 7f, photometric: 0));
        using RawImage owned = decoded.Luminance;

        Assert.Null(decoded.Color);
        Assert.Equal(new[] { 65535, 32768, 0 }, Row(decoded.Luminance));
        Assert.Equal(Row(single.Luminance), Row(decoded.Luminance));
        Assert.Equal(single.ValueNote, decoded.ValueNote);
    }

    [Theory]
    [InlineData(1, -1, true)]  // WICはRGB(Bgr24)として返す。以前は先頭チャネルではなくBを読んでいた(黙って誤値)
    [InlineData(1, 2, false)]  // ExtraSamplesがあるとWICは復号できない
    [InlineData(0, -1, false)] // WICはRGBとして返し反転しない(8bit符号あり・多チャネルのWhiteIsZeroはこの行だけが見る)
    public void ThreeSampleGray8Signed_OpensFirstChannelAsGray(int photometric, int extraSamples, bool compressed)
    {
        var page = MultiChannelGrayPage(8, 2, 3, extraSamples, new[] { 0f, 10f, 20f }, 100f, photometric);
        if (compressed)
        {
            TiffBuilder.Deflate(page);
        }

        DecodedImage decoded = Load(page);
        using RawImage owned = decoded.Luminance;

        Assert.Null(decoded.Color);
        Assert.Equal(photometric == 1 ? new[] { 0, 32768, 65535 } : new[] { 65535, 32768, 0 }, Row(decoded.Luminance));
        Assert.Equal(photometric == 1 ? "8bit値 0〜20 → 16bit (1code≈0.000305)" : "8bit値 -21〜-1 → 16bit (1code≈0.000305)",
            decoded.ValueNote);
    }

    [Theory]
    [InlineData(1, 2, new[] { 0, 10 << 8, 20 << 8 })]                  // ExtraSamplesがあるとWICは復号できない
    [InlineData(0, -1, new[] { 255 << 8, 245 << 8, 235 << 8 })]         // WICはRGBとして返し反転しない
    [InlineData(1, -1, new[] { 0, 10 * 257, 20 * 257 })]                // WICが読めるもの(RGBのR)はWICのまま
    public void ThreeSampleGray8Unsigned_OpensFirstChannelAsGray(int photometric, int extraSamples, int[] expected)
    {
        // WICで読めない8bit×3サンプルのグレーは自前で先頭チャネルを読む。自前復号は8bitを
        // ほかの自前復号の8bit(CFA・BigTIFFなど)と同じく v<<8 で置き、WICで読めるものは従来どおり v×257
        DecodedImage decoded = Load(MultiChannelGrayPage(8, 1, 3, extraSamples, new[] { 0f, 10f, 20f }, 100f, photometric));
        using RawImage owned = decoded.Luminance;

        Assert.Null(decoded.Color);
        Assert.Equal(8, decoded.Luminance.Format.BitDepth);
        Assert.Equal(expected, Row(decoded.Luminance));
    }

    [Theory]
    [InlineData(16, 2, false)]
    [InlineData(16, 3, true)]
    [InlineData(8, 2, true)]
    public void FiveSampleGray_WideSamples_OpensFirstChannelAsGray(int bits, int sampleFormat, bool compressed)
    {
        // WICは5サンプルのグレーもRGBA(R=G=B=先頭チャネル)で返すが、読み出し側が「1画素あたり5サンプル」を
        // 拒否していた(8/16bitの符号なしは通常経路で開けていた)
        var page = MultiChannelGrayPage(bits, sampleFormat, 5, 2, new[] { 0f, 10f, 20f }, 100f);
        if (compressed)
        {
            TiffBuilder.Deflate(page);
        }

        DecodedImage decoded = Load(page);
        using RawImage owned = decoded.Luminance;

        Assert.Null(decoded.Color);
        Assert.Equal(new[] { 0, 32768, 65535 }, Row(decoded.Luminance));
        Assert.Equal($"{bits}bit値 0〜20 → 16bit (1code≈0.000305)", decoded.ValueNote);
    }

    [Theory]
    [InlineData(32, 3, 3, 2, 1)]
    [InlineData(32, 1, 4, -1, 1)]
    [InlineData(32, 3, 2, 0, 1)] // 2サンプルで追加サンプルが未指定
    [InlineData(8, 1, 3, 2, 1)]  // 8bit×3サンプル、ExtraSamples あり
    [InlineData(8, 1, 3, -1, 0)] // 8bit×3サンプルのWhiteIsZero(WICはRGBとして返し反転しない)
    public void MultiChannelGray_CompressedWicUnreadable_IsRejectedWithReason(
        int bits, int sampleFormat, int samplesPerPixel, int extraSamples, int photometric)
    {
        // 圧縮ページは自前では読めず、WICは復号に失敗するか黙って誤った値を返す。開く前に理由付きで拒否する
        var page = MultiChannelGrayPage(bits, sampleFormat, samplesPerPixel, extraSamples, new[] { 0f, 10f, 20f }, 100f, photometric);
        TiffBuilder.Deflate(page);
        using var file = TempTiff.Write(new TiffBuilder().Build(page));

        var ex = Assert.Throws<InvalidDataException>(() => ImageFileLoader.Load(file.Path));
        Assert.Contains($"{bits}bit×{samplesPerPixel}サンプルのグレー", ex.Message);
        Assert.Contains("非圧縮であれば読めます", ex.Message);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    public void GrayWithAlpha32_CompressedViaWic_MatchesUncompressed(int sampleFormat, int photometric)
    {
        // 非圧縮の32bitグレー+アルファは自前復号、圧縮はWIC(Rgba128Float)で読む。経路が違っても同じ結果
        float[] first = sampleFormat == 3 ? new[] { 0f, 0.5f, 1f } : new[] { 0f, 10f, 20f };
        var compressed = MultiChannelGrayPage(32, sampleFormat, 2, 2, first, 1f, photometric);
        TiffBuilder.Deflate(compressed);

        DecodedImage native = Load(MultiChannelGrayPage(32, sampleFormat, 2, 2, first, 1f, photometric));
        using RawImage nativeOwned = native.Luminance;
        DecodedImage wic = Load(compressed);
        using RawImage wicOwned = wic.Luminance;

        Assert.Equal(photometric == 1 ? new[] { 0, 32768, 65535 } : new[] { 65535, 32768, 0 }, Row(native.Luminance));
        Assert.Equal(Row(native.Luminance), Row(wic.Luminance));
        Assert.Equal(native.ValueNote, wic.ValueNote);
    }

    [Theory]
    [InlineData(8, 1, 2, false)]
    [InlineData(8, 1, 4, false)]
    [InlineData(8, 0, 2, false)] // WhiteIsZero: 以前は全画素65535
    [InlineData(8, 0, 4, true)]
    [InlineData(16, 1, 2, false)]
    [InlineData(16, 1, 3, true)]
    [InlineData(16, 0, 2, false)]
    [InlineData(16, 0, 4, true)]
    public void AssociatedAlphaGray_KeepsSampleValues(int bits, int photometric, int samplesPerPixel, bool compressed)
    {
        // 関連アルファ(ExtraSamples=1)のページをWICはPbgra32/Prgba64で返す。以前はこれをBgra32へ変換し、
        // アルファで割り戻された値(10→25)になり、16bitは8bitに落ち、WhiteIsZeroでは全画素65535だった。
        // 元のサンプル値のまま、非関連アルファ(ExtraSamples=2)の同じページと同じ結果になること。
        // 色がアルファより大きい画素・アルファ0の画素も元の値のまま(アルファは値に使わない)
        long max = (1L << bits) - 1;
        long[] gray = { 0, 10, 20, bits == 8 ? 200 : 60000, 50 };
        DecodedImage expected = Load(AlphaPage(bits, photometric, samplesPerPixel, gray, 1, extraSamples: 2, compressed));
        using RawImage expectedOwned = expected.Luminance;
        DecodedImage actual = Load(AlphaPage(bits, photometric, samplesPerPixel, gray, 1, extraSamples: 1, compressed));
        using RawImage actualOwned = actual.Luminance;

        // 8bitはWIC経路の規約どおり v×257、16bitはそのまま(WhiteIsZero は 最大値−v)
        int[] codes = Array.ConvertAll(gray, v =>
        {
            long sample = photometric == 0 ? max - v : v;
            return (int)(bits == 8 ? sample * 257 : sample);
        });
        Assert.Null(actual.Color);
        Assert.Equal(bits, actual.Luminance.Format.BitDepth);
        Assert.Equal(codes, Row(actual.Luminance));
        Assert.Equal(Row(expected.Luminance), Row(actual.Luminance));

        // 非関連アルファの側も、カラーにせず先頭チャネルを元のビット深度のグレーとして開く
        Assert.Null(expected.Color);
        Assert.Equal(bits, expected.Luminance.Format.BitDepth);
    }

    [Theory]
    [InlineData(8, 4, false)]
    [InlineData(8, 5, false)]
    [InlineData(16, 4, false)]
    [InlineData(16, 5, true)]
    public void AssociatedAlphaRgb_KeepsSampleValues(int bits, int samplesPerPixel, bool compressed)
    {
        // RGB+関連アルファも同じく、アルファで割り戻さず(16bitは16bitのまま)、非関連アルファの
        // 同じページと同じRGBになること
        long big = bits == 8 ? 200 : 60000;
        long[] rgb = { 0, 5, 15, 10, 6, 16, 20, 7, 17, big, 1, 2, 50, 60, 70 };
        DecodedImage expected = Load(AlphaPage(bits, 2, samplesPerPixel, rgb, 3, extraSamples: 2, compressed));
        using RawImage expectedOwned = expected.Luminance;
        DecodedImage actual = Load(AlphaPage(bits, 2, samplesPerPixel, rgb, 3, extraSamples: 1, compressed));
        using RawImage actualOwned = actual.Luminance;

        Assert.NotNull(actual.Color);
        Assert.Equal(bits, actual.Color!.BitDepth);
        actual.Color.GetPixel(1, 0, out ushort r, out ushort g, out ushort b);
        int scale = bits == 8 ? 257 : 1;
        Assert.Equal((10 * scale, 6 * scale, 16 * scale), ((int)r, (int)g, (int)b));
        AssertSameColor(expected, actual);
    }

    [Fact]
    public void AssociatedAlphaRgb16_Planar_KeepsSampleValues()
    {
        // プレーン分離の16bit RGB+関連アルファも、割り戻さず16bitのまま読む(プレーン分離の展開はWICの
        // 内部で行われ、圧縮の有無で本番の経路は変わらない)
        long[] rgba = { 0, 5, 15, 1000, 10, 6, 16, 1000, 60000, 1, 2, 1000, 50, 60, 70, 0 };
        var page = PlanarPage(4, 1, 16, 1, 4, IntegerSamples(16, rgba));
        page.Tags[338] = (3, new long[] { 1 });

        DecodedImage decoded = Load(page);
        using RawImage owned = decoded.Luminance;

        Assert.Equal(16, decoded.Color!.BitDepth);
        decoded.Color.GetPixel(2, 0, out ushort r, out ushort g, out ushort b);
        Assert.Equal((60000, 1, 2), ((int)r, (int)g, (int)b));
        decoded.Color.GetPixel(3, 0, out r, out g, out b);
        Assert.Equal((50, 60, 70), ((int)r, (int)g, (int)b)); // アルファ0でも色は元の値
    }

    /// <summary>
    /// PlanarConfiguration=2(成分ごとに1ストリップ)の多サンプルページ(既定はRGB(A))。chunky は1画素に
    /// samplesPerPixel サンプルを並べたバイト列(1サンプル bits/8 バイト)。
    /// </summary>
    private static TiffBuilder.Page PlanarPage(
        int width, int height, int bits, int sampleFormat, int samplesPerPixel, byte[] chunky, int photometric = 2)
    {
        var page = TiffBuilder.GrayPage(width, height, bits, chunky,
            photometric: photometric, sampleFormat: sampleFormat, samplesPerPixel: samplesPerPixel);
        page.Blocks.Clear();
        int bytesPer = bits / 8;
        int pixels = width * height;
        for (int c = 0; c < samplesPerPixel; c++)
        {
            var plane = new byte[pixels * bytesPer];
            for (int i = 0; i < pixels; i++)
            {
                chunky.AsSpan(((i * samplesPerPixel) + c) * bytesPer, bytesPer).CopyTo(plane.AsSpan(i * bytesPer));
            }

            page.Blocks.Add(plane);
        }

        page.Tags[284] = (3, new long[] { 2 });
        return page;
    }

    /// <summary>
    /// チャンキーの多サンプル(既定はRGB(A))をタイルにしたページ(1画素ぶんのバイト列をタイルへ並べ、
    /// タグを多サンプルへ直す)。
    /// </summary>
    private static TiffBuilder.Page TiledPage(
        int width, int height, int bits, int sampleFormat, int samplesPerPixel, byte[] chunky,
        int tileWidth, int tileHeight, int photometric = 2)
    {
        var page = TiffBuilder.TiledGrayPage(width, height, bits * samplesPerPixel, chunky, tileWidth, tileHeight);
        page.Tags[258] = (3, Enumerable.Repeat((long)bits, samplesPerPixel).ToArray());
        page.Tags[262] = (3, new long[] { photometric });
        page.Tags[277] = (3, new long[] { samplesPerPixel });
        page.Tags[339] = (3, Enumerable.Repeat((long)sampleFormat, samplesPerPixel).ToArray());
        return page;
    }

    /// <summary>2つの読込結果が同じRGBコード・同じ輝度であることを確かめる。</summary>
    private static void AssertSameColor(DecodedImage expected, DecodedImage actual)
    {
        Assert.NotNull(expected.Color);
        Assert.NotNull(actual.Color);
        Assert.Equal(expected.Luminance.Width, actual.Luminance.Width);
        Assert.Equal(expected.Luminance.Height, actual.Luminance.Height);
        for (int y = 0; y < expected.Luminance.Height; y++)
        {
            for (int x = 0; x < expected.Luminance.Width; x++)
            {
                expected.Color!.GetPixel(x, y, out ushort er, out ushort eg, out ushort eb);
                actual.Color!.GetPixel(x, y, out ushort ar, out ushort ag, out ushort ab);
                Assert.Equal((er, eg, eb), (ar, ag, ab));
                Assert.Equal(expected.Luminance.GetPixel(x, y), actual.Luminance.GetPixel(x, y));
            }
        }
    }

    /// <summary>単一ページのリトルエンディアンTIFFを一時ファイルに書き、本番経路で読み込む。</summary>
    private static DecodedImage Load(TiffBuilder.Page page)
    {
        using var file = TempTiff.Write(new TiffBuilder().Build(page));
        return ImageFileLoader.Load(file.Path);
    }
}
