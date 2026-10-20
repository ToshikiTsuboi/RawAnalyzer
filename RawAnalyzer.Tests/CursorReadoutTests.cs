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

    /// <summary>
    /// 行交互HDRの raw の、ファイル行 y の画素が持つセンサ行(HdrSplitter の解釈: 段 s のサブ行 k は
    /// ファイル行 (k/B)·P + s·B + k%B にあり、センサ行 k − s·offset を持つ)。
    /// </summary>
    private static int SensorRowOf(int fileRow, int lineBlock, int stages, int rowOffset)
    {
        int period = lineBlock * stages;
        int subRow = (fileRow / period * lineBlock) + (fileRow % lineBlock);
        int stage = fileRow % period / lineBlock;
        return subRow - (stage * rowOffset);
    }

    [Theory]
    [InlineData(0, 0, false)]   // 既定(Bayer ありでライン単位2)・オフセット0: 位相が一致する
    [InlineData(2, 2, false)]   // ライン単位・オフセットとも偶数
    [InlineData(1, 0, true)]    // ライン単位1(物理ライン1本ごとに露光を読むセンサ)
    [InlineData(3, 0, true)]
    [InlineData(2, 1, true)]    // 奇数の行オフセット
    [InlineData(2, -1, true)]
    [InlineData(1, 1, true)]
    public void LineInterleavedHdr_ChannelNameNeverContradictsTheSensorPhase(
        int lineBlock, int rowOffset, bool phaseMismatch)
    {
        // 検証 r2-render-rawview-hdr-phase-1。Raw 表示のチャネル名はファイル行の偶奇で決めていたが、
        // 行交互HDRのファイル行の偶奇がセンサ行の偶奇と一致するのは、ライン単位と行オフセットがともに偶数のときだけ。
        // ライン単位1では (0,1)(センサ行0の短秒の R)に R の値 52000 を表示しながら「Gb」と出していた。
        // 位相が一致しないときは名前を出さない(「-」。HDR分割ビューでは段の位相で正しく出る)
        const int width = 8;
        const int height = 32;
        const int stages = 2;
        var codes = new ushort[width * height];
        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16, Bayer = BayerPattern.Rggb,
            Hdr = HdrMode.LineInterleaved, HdrStages = stages, HdrLineBlock = lineBlock, HdrRowOffset = rowOffset,
        };
        int block = format.EffectiveHdrLineBlock;
        for (int y = 0; y < height; y++)
        {
            int sensorRow = SensorRowOf(y, block, stages, rowOffset);
            for (int x = 0; x < width; x++)
            {
                codes[(y * width) + x] = BayerHelper.GetChannel(BayerPattern.Rggb, x, sensorRow & 1) switch
                {
                    BayerChannel.R => 52000,
                    BayerChannel.Gr => 31000,
                    BayerChannel.Gb => 19000,
                    _ => 8000,
                };
            }
        }

        using RawImage image = TestImages.FromCodes(codes, format);
        for (int y = 0; y < height; y++)
        {
            int sensorRow = SensorRowOf(y, block, stages, rowOffset);
            for (int x = 0; x < width; x++)
            {
                string expected = BayerHelper.GetLabel(BayerHelper.GetChannel(BayerPattern.Rggb, x, sensorRow & 1));
                string overlay = CursorReadout.Compose(image, format, null, x, y, 0)!.Value.Overlay;
                string shown = overlay[(overlay.LastIndexOf(' ') + 1)..];

                Assert.Equal(phaseMismatch ? "-" : expected, shown);
            }
        }
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
