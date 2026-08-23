using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// Codexレビュー(2026-08-23)で指摘された不具合の回帰テスト。
/// </summary>
public class CodexFixTests
{
    // ---- #1 カラー画像を輝度化せず保存する ----

    private static ColorImage MakeColorImage(int width, int height)
    {
        var interleaved = new ushort[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            interleaved[i * 3] = (ushort)(1000 + i);       // R
            interleaved[(i * 3) + 1] = (ushort)(20000 + i); // G
            interleaved[(i * 3) + 2] = (ushort)(60000 - i); // B
        }

        return ColorImage.FromInterleaved(width, height, 16, interleaved);
    }

    [Fact]
    public void RenderColorRgb48_KeepsPerChannelValues()
    {
        // 16bit保存でチャネルを潰さないこと(輝度化すると色が失われる)
        ColorImage color = MakeColorImage(4, 3);

        ushort[] rgb48 = ImageExport.RenderColorRgb48(color);

        Assert.Equal(4 * 3 * 3, rgb48.Length);
        color.GetPixel(2, 1, out ushort r, out ushort g, out ushort b);
        int index = ((1 * 4) + 2) * 3;
        Assert.Equal(r, rgb48[index]);
        Assert.Equal(g, rgb48[index + 1]);
        Assert.Equal(b, rgb48[index + 2]);
        Assert.NotEqual(rgb48[index], rgb48[index + 1]);
    }

    [Fact]
    public void RenderColorRgb24_WritesIntoProvidedBuffer()
    {
        // 動画書き出しでフレームごとに確保しないための経路
        ColorImage color = MakeColorImage(4, 3);
        var lut = DisplayLut.Create(new DisplayParameters());

        byte[] allocated = ImageExport.RenderColorRgb24(color, lut);
        var reused = new byte[allocated.Length];
        ImageExport.RenderColorRgb24(color, lut, reused);

        Assert.Equal(allocated, reused);
    }

    [Fact]
    public void RenderColorRgb24_RejectsTooSmallBuffer()
    {
        ColorImage color = MakeColorImage(4, 3);
        var lut = DisplayLut.Create(new DisplayParameters());

        Assert.Throws<ArgumentException>(
            () => ImageExport.RenderColorRgb24(color, lut, new byte[8]));
    }

    [Fact]
    public void DevelopRgb24_WritesIntoProvidedBuffer()
    {
        var format = new RawFormat
        {
            Width = 8,
            Height = 6,
            BitDepth = 16,
            Bayer = BayerPattern.Rggb,
        };
        var codes = new ushort[format.Width * format.Height];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)((i * 5003) % 65536);
        }

        using RawImage image = RawImage.FromPixels(format, codes);
        var luts = DevelopLuts.Create(new DevelopParameters(Gamma: 1.0));

        byte[] allocated = ImageExport.DevelopRgb24(image, 0, BayerPattern.Rggb, luts);
        var reused = new byte[allocated.Length];
        ImageExport.DevelopRgb24(image, 0, BayerPattern.Rggb, luts, null, default, reused);

        Assert.Equal(allocated, reused);
    }

    // ---- #9 NaN / Infinity を通さない ----

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("abc")]
    public void TryParsePositive_RejectsNonFiniteAndNonPositive(string text)
    {
        Assert.False(NumericInput.TryParsePositive(text, out _));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void TryParseFinite_RejectsNonFinite(string text)
    {
        Assert.False(NumericInput.TryParseFinite(text, out _));
    }

    [Fact]
    public void TryParseFinite_AcceptsNegativeMatrixCoefficients()
    {
        // カラーマトリクスは負の係数を取り得るので、有限なら通す
        Assert.True(NumericInput.TryParseFinite("-0.25", out double value));
        Assert.Equal(-0.25, value, 10);
    }

    [Fact]
    public void RawFormat_Validate_RejectsNonFiniteExposureRatio()
    {
        var format = new RawFormat
        {
            Width = 4,
            Height = 4,
            BitDepth = 12,
            ExposureRatio = double.NaN,
        };

        Assert.Throws<ArgumentException>(format.Validate);
    }

    [Fact]
    public void DevelopLuts_Create_RejectsNonFiniteParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DevelopLuts.Create(new DevelopParameters(Gamma: double.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DevelopLuts.Create(new DevelopParameters(Gain: double.PositiveInfinity)));
    }

    [Fact]
    public void DevelopLuts_Create_RejectsNonFiniteMatrix()
    {
        var matrix = new ColorMatrix(1, 0, 0, 0, double.NaN, 0, 0, 0, 1);

        Assert.Throws<ArgumentException>(
            () => DevelopLuts.Create(new DevelopParameters(Gamma: 1.0, Matrix: matrix)));
    }

    [Fact]
    public void DefectPixelDetector_RejectsNonFiniteSigma()
    {
        var format = new RawFormat { Width = 4, Height = 4, BitDepth = 12 };
        using RawImage image = RawImage.FromPixels(format, new ushort[16]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => DefectPixelDetector.Detect(image, sigmaFactor: double.NaN));
    }

    // ---- #8 ネットワーク判定 ----

    [Fact]
    public void IsNetworkPath_LocalTempIsNotNetwork()
    {
        Assert.False(RawLoader.IsNetworkPath(Path.GetTempPath()));
    }
}
