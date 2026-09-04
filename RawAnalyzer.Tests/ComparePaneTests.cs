using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Compare;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class DisplaySettingsTests
{
    [Fact]
    public void CreateDefault_MapsFullRange()
    {
        DisplaySettings settings = DisplaySettings.CreateDefault(12);
        DisplayParameters parameters = settings.ToDisplayParameters(12);

        Assert.Equal(0, parameters.BlackPoint);
        Assert.Equal(65535, parameters.WhitePoint);
        Assert.Equal(1.0, parameters.Gain, 10);
        Assert.Equal(1.0, parameters.Gamma, 10);
        Assert.Equal(1.0, parameters.Contrast, 10);
    }

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

    [Fact]
    public void Percent_IsComparableAcrossBitDepths()
    {
        // ビット深度が違うカメラの「同じ条件」は%FSで一致する
        var on12bit = new DisplaySettings { BlackCode = 409.5, WhiteCode = 4095 };
        var on10bit = new DisplaySettings { BlackCode = 102.3, WhiteCode = 1023 };

        Assert.Equal(10.0, on12bit.BlackPercent(12), 6);
        Assert.Equal(10.0, on10bit.BlackPercent(10), 6);
        Assert.Equal(100.0, on12bit.WhitePercent(12), 6);
        Assert.Equal(100.0, on10bit.WhitePercent(10), 6);
    }
}

public class CompareViewLayoutTests
{
    [Theory]
    [InlineData(0, 1)]  // 画像なし: 中央の追加案内のみ
    [InlineData(1, 1)]  // 1枚は領域全体
    [InlineData(2, 2)]  // 2枚は左右半分ずつ
    [InlineData(3, 3)]  // 3枚は横一列
    [InlineData(4, 2)]  // 4枚は2×2
    [InlineData(5, 2)]  // 上限超えは来ないが2列のまま
    public void ColumnsFor_ThreeAcrossThenGrid(int elements, int expected)
    {
        Assert.Equal(expected, CompareView.ColumnsFor(elements));
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
    [InlineData(@"C:\a\b.png", false)]
    public void IsRawFile_ChecksExtension(string path, bool expected)
    {
        Assert.Equal(expected, ComparePane.IsRawFile(path));
    }

    [Fact]
    public async Task LoadAsync_Raw_RequiresFormat()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ComparePane.LoadAsync(@"C:\nowhere\x.raw", rawFormat: null));
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
            using ComparePane pane = await ComparePane.LoadAsync(path, format);

            Assert.Equal(BayerPattern.Rggb, pane.Format.Bayer);
            Assert.Equal(12, pane.Format.BitDepth);
            Assert.Null(pane.Color);
            Assert.Equal(codes[65] << 4, pane.Image.GetPixel(1, 1));
            Assert.Equal((1 << 12) - 1, pane.Display.WhiteCode);

            await pane.EnsureTilePyramidAsync();
            await pane.EnsureBayerPyramidAsync();
            Assert.NotNull(pane.Pyramid);
            Assert.NotNull(pane.BayerPyramid);

            // 同じフレームなら再生成しない
            TilePyramid? tile = pane.Pyramid;
            BayerPyramid? bayer = pane.BayerPyramid;
            await pane.EnsureTilePyramidAsync();
            await pane.EnsureBayerPyramidAsync();
            Assert.Same(tile, pane.Pyramid);
            Assert.Same(bayer, pane.BayerPyramid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_MultiFrameRaw_RebuildsPyramidForOtherFrame()
    {
        var format = new RawFormat
        {
            Width = 16, Height = 16, BitDepth = 16, FrameCount = 2,
            Bayer = BayerPattern.Rggb,
        };
        var codes = new ushort[16 * 16 * 2];
        string path = WriteRaw(codes, format);
        try
        {
            using ComparePane pane = await ComparePane.LoadAsync(path, format);
            await pane.EnsureBayerPyramidAsync(frame: 0);
            BayerPyramid? first = pane.BayerPyramid;

            await pane.EnsureBayerPyramidAsync(frame: 1);

            Assert.NotSame(first, pane.BayerPyramid);
            Assert.Equal(1, pane.BayerPyramidFrame);
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

            // Bayerなしなので何もしない(例外にならない)
            await pane.EnsureBayerPyramidAsync();
            Assert.Null(pane.BayerPyramid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Dispose_ReleasesImageAndRejectsFurtherUse()
    {
        var format = new RawFormat { Width = 8, Height = 8, BitDepth = 16 };
        string path = WriteRaw(new ushort[64], format);
        try
        {
            ComparePane pane = await ComparePane.LoadAsync(path, format);
            pane.Dispose();

            Assert.Throws<ObjectDisposedException>(() => pane.Image.GetPixel(0, 0));
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => pane.EnsureTilePyramidAsync());

            // 二重Disposeは安全
            pane.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
