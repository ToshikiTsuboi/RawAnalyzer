using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Compare;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class DisplaySettingsTests
{
    [Fact]
    public void ToDisplayParameters_UsesLevelCodeConvention()
    {
        // メインビューのApplyLevelCodesと同じ規約:
        // 黒は下端 code<<shift、白は区間上端 (code<<shift)|(2^shift-1)
        var settings = new DisplaySettings { BlackCode = 137, WhiteCode = 4000 };
        DisplayParameters parameters = settings.ToDisplayParameters(12);

        Assert.Equal(137 << 4, parameters.BlackPoint);
        Assert.Equal((4000 << 4) | 15, parameters.WhitePoint);
    }

    [Fact]
    public void GainDb_ConvertsToLinear()
    {
        Assert.Equal(2.0, new DisplaySettings { GainDb = 6.0206 }.GainLinear, 4);
        Assert.Equal(0.5, new DisplaySettings { GainDb = -6.0206 }.GainLinear, 4);
        Assert.Equal(1000.0, new DisplaySettings { GainDb = 60 }.GainLinear, 6);
    }
}

public class ComparePaneTests
{
    private static string WriteRaw(ushort[] codes, RawFormat format)
    {
        // WriteTempFileは.binを作る(.binはraw拡張子なのでそのまま使える)
        return TestData.WriteTempFile(TestData.EncodeRawFile(codes, format));
    }

    [Theory]
    [InlineData(@"C:\a\b.raw", true)]
    [InlineData(@"C:\a\b.BIN", true)]
    [InlineData(@"C:\a\b.tif", false)]
    public void IsRawFile_ChecksExtension(string path, bool expected)
    {
        Assert.Equal(expected, ComparePane.IsRawFile(path));
    }

    [Fact]
    public async Task LoadAsync_Raw_LoadsWithPyramids()
    {
        var format = new RawFormat
        {
            Width = 64, Height = 64, BitDepth = 12, Bayer = BayerPattern.Rggb,
        };
        var codes = new ushort[64 * 64];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(i % 4096);
        }

        string path = WriteRaw(codes, format);
        try
        {
            // rawはフォーマット未指定では開けない
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => ComparePane.LoadAsync(path, rawFormat: null));

            using ComparePane pane = await ComparePane.LoadAsync(path, format);

            Assert.Equal(BayerPattern.Rggb, pane.Format.Bayer);
            Assert.Equal(12, pane.Format.BitDepth);
            Assert.Null(pane.Color);
            Assert.Equal(codes[65] << 4, pane.Image.GetPixel(1, 1));
            Assert.Equal((1 << 12) - 1, pane.Display.WhiteCode);

            await pane.EnsureTilePyramidAsync();
            Assert.NotNull(pane.Pyramid);

            // 同じフレームなら再生成しない
            TilePyramid? tile = pane.Pyramid;
            await pane.EnsureTilePyramidAsync();
            Assert.Same(tile, pane.Pyramid);

            // Disposeは画像も破棄する(MMFハンドル漏れ防止)。二重Disposeは安全
            pane.Dispose();
            Assert.Throws<ObjectDisposedException>(() => pane.Image.GetPixel(0, 0));
            pane.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_ColorPng_KeepsColorImage()
    {
        // 8x8のカラーPNGを作って読み込む
        string path = Path.Combine(
            Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N") + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < 64; i++)
        {
            pixels[i * 4] = 30;             // B
            pixels[i * 4 + 1] = 200;        // G
            pixels[i * 4 + 2] = 120;        // R
            pixels[i * 4 + 3] = 255;
        }

        BitmapSource source = BitmapSource.Create(
            8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var stream = new FileStream(path, FileMode.Create))
        {
            encoder.Save(stream);
        }

        try
        {
            using ComparePane pane = await ComparePane.LoadAsync(path, rawFormat: null);

            Assert.NotNull(pane.Color);
            Assert.Equal(8, pane.Image.Width);
            Assert.Equal(BayerPattern.None, pane.Format.Bayer);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
