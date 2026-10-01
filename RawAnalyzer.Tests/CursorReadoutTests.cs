using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// カーソル位置の画素値の表示文(オーバーレイとステータスバー。MainWindow の OnCursorPixelChanged と、
/// 送り・差し替え・Bayer の変更の後に位置を保ったまま読み直す RefreshCursorReadout)。
/// </summary>
public class CursorReadoutTests
{
    private static RawImage TwoFrameImage(BayerPattern bayer = BayerPattern.Rggb)
    {
        // 4×2・12bit・2フレーム。(1, 1) はフレーム0で 100、フレーム1で 900
        var codes = new ushort[4 * 2 * 2];
        codes[(0 * 2 + 1) * 4 + 1] = 100;
        codes[(1 * 2 + 1) * 4 + 1] = 900;
        return TestImages.FromCodes(codes, new RawFormat
        {
            Width = 4, Height = 2, BitDepth = 12, Bayer = bayer, FrameCount = 2,
        });
    }

    [Fact]
    public void ReadsTheDisplayedFrame()
    {
        // フレーム送り・再生の後は、カーソルを動かさなくても送った先のフレームの値を出す
        // (以前はマウス移動・画素カーソルの移動でしか作り直さず、フレーム0の値を出し続けた)
        using RawImage image = TwoFrameImage();

        CursorReadout.Text? frame0 = CursorReadout.Compose(image, image.Format, null, 1, 1, frame: 0);
        CursorReadout.Text? frame1 = CursorReadout.Compose(image, image.Format, null, 1, 1, frame: 1);

        Assert.NotNull(frame0);
        Assert.NotNull(frame1);
        Assert.Equal("(1, 1) raw=100", frame0.Value.Status);
        Assert.Equal("(1, 1) raw=900", frame1.Value.Status);
        Assert.Equal("(1, 1)  raw: 900 / 4095  B", frame1.Value.Overlay);
    }

    [Fact]
    public void ChannelFollowsTheCurrentPattern()
    {
        // 右パネルで Bayer を変えたら、同じ位置でも新しいパターンのチャネル名を出す
        using RawImage image = TwoFrameImage();
        RawFormat bggr = image.Format with { Bayer = BayerPattern.Bggr };
        RawFormat none = image.Format with { Bayer = BayerPattern.None };

        Assert.EndsWith("  R", CursorReadout.Compose(image, image.Format, null, 0, 0, 0)!.Value.Overlay);
        Assert.EndsWith("  B", CursorReadout.Compose(image, bggr, null, 0, 0, 0)!.Value.Overlay);
        Assert.EndsWith("  -", CursorReadout.Compose(image, none, null, 0, 0, 0)!.Value.Overlay);
    }

    [Fact]
    public void BitDepthOfTheFormatScalesTheCode()
    {
        // 送った先のビット深度(画像ファイルの連番・TIFF のページ)でコード値と最大値を表す
        using RawImage image = TwoFrameImage();
        RawFormat as16 = image.Format with { BitDepth = 16 };

        Assert.Equal(
            "(1, 1)  raw: 14400 / 65535  B",
            CursorReadout.Compose(image, as16, null, 1, 1, frame: 1)!.Value.Overlay);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(0, 2)]
    [InlineData(-1, 0)]
    public void OutsideTheImage_IsNull(int x, int y)
    {
        // 寸法の小さい画像へ送った・差し替えた後は、前の位置を読めないので表示を消す
        using RawImage image = TwoFrameImage();

        Assert.Null(CursorReadout.Compose(image, image.Format, null, x, y, 0));
    }

    [Fact]
    public void FrameOutsideTheImage_IsNull()
    {
        using RawImage image = TwoFrameImage();

        Assert.Null(CursorReadout.Compose(image, image.Format, null, 1, 1, frame: 2));
    }

    [Fact]
    public void DisposedImage_IsNull()
    {
        // 差し替えの途中で破棄された画像を読んでも例外にしない
        RawImage image = TwoFrameImage();
        image.Dispose();

        Assert.Null(CursorReadout.Compose(image, image.Format, null, 1, 1, 0));
    }

    [Fact]
    public void ColorImage_ShowsRgbAndYCbCr()
    {
        // カラー画像(デコード済みの RGB)は RGB と YCbCr を出す
        using RawImage luminance = TestImages.FromCodes(new ushort[4], 2, 2, bitDepth: 8);
        var rgb = new ushort[2 * 2 * 3];
        int at = (1 * 2 + 0) * 3;
        rgb[at] = 255 << 8;
        rgb[at + 1] = 0;
        rgb[at + 2] = 0;
        ColorImage color = ColorImage.FromInterleaved(2, 2, 8, rgb);

        CursorReadout.Text? text = CursorReadout.Compose(luminance, luminance.Format, color, 0, 1, 0);

        Assert.NotNull(text);
        (int y, int cb, int cr) = ColorConvert.RgbToYCbCr(255, 0, 0, 255);
        Assert.Equal($"(0, 1) RGB=(255, 0, 0) YCbCr=({y}, {cb}, {cr})", text.Value.Status);
        Assert.Equal($"(0, 1)  RGB: 255 0 0  YCbCr: {y} {cb} {cr}", text.Value.Overlay);
    }
}
