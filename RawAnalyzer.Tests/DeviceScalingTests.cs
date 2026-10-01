using RawAnalyzer.App.Rendering;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 表示倍率(Windows の表示スケール)とビューポートのズームの換算。DPI は自動テストで変えられないので、
/// 倍率を引数に取る換算をここで 100%・125%・150%・200% について確かめる。
/// </summary>
public class DeviceScalingTests
{
    [Theory]
    [InlineData(1.0, 240, 180)]
    [InlineData(1.25, 300, 225)]
    [InlineData(1.5, 360, 270)]
    [InlineData(2.0, 480, 360)]
    public void DrawsAtDeviceResolution(double scale, int deviceWidth, int deviceHeight)
    {
        // 以前は DIP の大きさ(240×180)の 96dpi のビットマップを描き、それが表示倍率ぶん最近傍で
        // 引き伸ばされていた(125% では元画像 1 画素が 1,1,1,2 デバイス画素と周期的に幅を変え、縞に見えた)
        Assert.Equal(deviceWidth, DeviceScaling.DevicePixels(240, scale));
        Assert.Equal(deviceHeight, DeviceScaling.DevicePixels(180, scale));

        // ビットマップは 96×倍率 dpi で作るので、DIP の大きさ(画素数×96/dpi)はビューポートと同じになり、
        // 描いた 1 画素が 1 デバイス画素に写る
        double dpi = DeviceScaling.BitmapDpi(scale);
        Assert.Equal(240, deviceWidth * 96 / dpi, 9);
        Assert.Equal(180, deviceHeight * 96 / dpi, 9);
    }

    [Fact]
    public void DevicePixels_IsAtLeastOne()
    {
        Assert.Equal(1, DeviceScaling.DevicePixels(0.2, 1.0));
        Assert.Equal(1, DeviceScaling.DevicePixels(0, 2.0));
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.25, 0.8)]
    [InlineData(1.5, 2.0 / 3)]
    [InlineData(2.0, 0.5)]
    public void ActualSize_MapsOneImagePixelToOneDevicePixel(double scale, double expectedZoom)
    {
        // 等倍は元画像 1 画素 = 1 デバイス画素。ズームは DIP 基準なので 1/倍率 になる
        // (以前は 1 画素 = 1 DIP で、100% 以外では非整数倍の最近傍拡大になっていた)
        double zoom = DeviceScaling.ActualSizeZoom(scale);

        Assert.Equal(expectedZoom, zoom, 12);
        Assert.Equal(1.0, DeviceScaling.ToDeviceZoom(zoom, scale));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(1.27)] // 1/倍率×倍率 が 0.99999999999999989 になる倍率(カスタムの拡大率)
    [InlineData(122 / 96.0)]
    [InlineData(1.75)]
    [InlineData(2.25)]
    [InlineData(3.0)]
    public void DeviceZoom_IsExactAtIntegerRatios(double scale)
    {
        // DIP 基準のズームへ換算して戻した値が整数倍(または 1/整数倍)から丸め誤差だけずれると、
        // 最近傍の描画で元画像の列の対応が途中で 1 つずれ、1 列だけ 2 デバイス画素に写ることがある。
        // オーバーレイの閾値(32 倍)も 31.999… で外れる。丸め誤差の範囲はその値にそろえる
        foreach (double device in new[] { 1.0, 2.0, 4.0, 32.0, 0.5, 0.25, 0.125 })
        {
            double zoom = DeviceScaling.FromDeviceZoom(device, scale);
            Assert.Equal(device, DeviceScaling.ToDeviceZoom(zoom, scale));
        }

        // 整数倍から離れた値はそのまま
        Assert.Equal(1.1 * scale, DeviceScaling.ToDeviceZoom(1.1, scale), 12);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidScale_IsTreatedAsOne(double scale)
    {
        Assert.Equal(1.0, DeviceScaling.Normalize(scale));
        Assert.Equal(240, DeviceScaling.DevicePixels(240, scale));
        Assert.Equal(1.0, DeviceScaling.ActualSizeZoom(scale));
    }

    [Theory]
    // 倍率, DIP基準ズーム, 品質パスの縮小率, 操作中(速報)の縮小率
    [InlineData(1.0, 0.25, 4, 8)]
    [InlineData(1.25, 0.25, 2, 8)]
    [InlineData(1.5, 0.25, 2, 8)]
    [InlineData(2.0, 0.25, 2, 8)]
    [InlineData(1.0, 0.125, 8, 16)]
    [InlineData(1.25, 0.125, 4, 16)]
    [InlineData(1.5, 0.125, 4, 16)]
    [InlineData(2.0, 0.125, 4, 16)]
    [InlineData(1.0, 0.05, 16, 32)]
    [InlineData(1.25, 0.05, 16, 32)]
    [InlineData(1.5, 0.05, 8, 32)]
    [InlineData(2.0, 0.05, 8, 32)]
    [InlineData(2.0, 0.6, 1, 1)] // デバイス基準で等倍以上は縮小レベルを使わず、操作中も落とさない
    [InlineData(2.0, 0.5, 1, 1)] // 200% の等倍。操作中も元画像のまま(以前の速報なら DIP 基準の 2 から 1 段粗い 4)
    [InlineData(2.0, 0.2, 2, 8)] // 速報は従来どおり DIP 基準のレベル(4)から1段粗い
    public void SelectRenderFactor_QualityMatchesDevicePixels_FastReadsLikeBefore(
        double scale, double zoom, int quality, int fast)
    {
        // 品質パスはデバイス画素に見合う縮小レベル(1/デバイス基準ズーム を超えない最大の縮小率)で描き、
        // 全体表示もモニタの解像度で細かく見せる。読む画素数はデバイス画素数に比例して増えるので、
        // 操作中の速報は従来と同じ DIP 基準のレベルから1段粗いレベルにし、操作中の読み出し量を増やさない
        TilePyramid pyramid = CreatePyramid();

        Assert.Equal(quality, Select(pyramid, zoom, scale, fast: false));
        Assert.Equal(fast, Select(pyramid, zoom, scale, fast: true));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void SelectRenderFactor_NeverCoarserThanDevicePixels_FastNeverFiner(double scale)
    {
        TilePyramid pyramid = CreatePyramid();
        for (double zoom = 0.012; zoom < 2; zoom *= 1.25)
        {
            int quality = Select(pyramid, zoom, scale, fast: false);
            int fast = Select(pyramid, zoom, scale, fast: true);
            double devicePerImagePixel = zoom * scale;

            // 品質パスのレベルの1画素はデバイス1画素より大きくならない(粗いブロックに見えない)。
            // 必要以上に細かいレベル(デバイス1画素に2画素以上)は、その上のレベルがあれば読まない
            Assert.True(quality * devicePerImagePixel <= 1 + 1e-9 || quality == 1, $"zoom={zoom}");
            Assert.True(
                quality * 2 * devicePerImagePixel > 1 || pyramid.GetLevel(quality * 2) is null,
                $"zoom={zoom}");

            // 速報は品質パスより細かいレベルを読まない。デバイス基準で等倍以上なら元画像のまま、
            // それ以外は倍率によらず従来(倍率 1)と同じレベル
            Assert.True(fast >= quality, $"zoom={zoom}");
            Assert.Equal(quality == 1 ? 1 : Select(pyramid, zoom, 1.0, fast: true), fast);
        }
    }

    [Theory]
    [InlineData(1.0, 32.0)]
    [InlineData(1.25, 25.6)]
    [InlineData(1.5, 64.0 / 3)]
    [InlineData(2.0, 16.0)]
    [InlineData(1.27, 32 / 1.27)]
    public void RawOverlay_StartsAtDeviceZoom32(double scale, double overlayZoom)
    {
        // raw 値オーバーレイは元画像 1 画素が 32 デバイス画素以上のとき(倍率表示の 3200% 以上)に出す。
        // 「ここを拡大」などで画素値を見せるときもこのズームへ寄せる
        Assert.Equal(overlayZoom, DeviceScaling.RawOverlayZoom(scale), 12);
        Assert.True(DeviceScaling.ShowsRawOverlay(DeviceScaling.RawOverlayZoom(scale), scale));
        Assert.True(DeviceScaling.ShowsRawOverlay(overlayZoom * 2, scale));
        Assert.False(DeviceScaling.ShowsRawOverlay(overlayZoom * 0.99, scale));
    }

    [Fact]
    public void RawOverlayFontSize_IsUnchangedAtScaleOne_AndShrinksToFitSmallerCells()
    {
        // 倍率 1 で出るズーム(32 以上)では従来と同じ大きさ
        for (double zoom = 32; zoom <= 128; zoom += 0.5)
        {
            Assert.Equal(Math.Clamp(zoom / 4.5, 9, 15), DeviceScaling.RawOverlayFontSize(zoom), 12);
        }

        // 高DPIでは 32 デバイス画素のマスが 32 DIP より小さい(200% で 16 DIP)。従来の最小 9 DIP の文字では
        // 5 桁(Consolas で約 2.75 文字幅)がマスからはみ出すので、マスに収まる大きさまで小さくする
        foreach (double scale in new[] { 1.25, 1.5, 2.0, 2.5, 3.0 })
        {
            double zoom = DeviceScaling.RawOverlayZoom(scale);
            double fontSize = DeviceScaling.RawOverlayFontSize(zoom);
            Assert.True(fontSize * 2.75 <= zoom, $"scale={scale}");
            Assert.True(fontSize <= 9);
        }
    }

    [Theory]
    [InlineData(1.0, 1.5)]
    [InlineData(1.5, 1.0)]
    [InlineData(1.25, 2.0)]
    [InlineData(1.27, 1.5)]
    public void DpiChange_KeepsActualSizeAndIntegerMagnification(double oldScale, double newScale)
    {
        // 表示倍率が変わっても(別の倍率のモニタへ移したとき)、等倍・整数倍の拡大は元画像の 1 画素を
        // 同じ数のデバイス画素に写し続ける(DIP 基準のまま保つと非整数倍になり縞が出る)
        foreach (double device in new[] { 1.0, 2.0, 3.0, 32.0 })
        {
            double before = DeviceScaling.FromDeviceZoom(device, oldScale);
            double after = DeviceScaling.ZoomAfterScaleChange(before, oldScale, newScale);
            Assert.Equal(device, DeviceScaling.ToDeviceZoom(after, newScale));
        }

        // それ以外(全体表示・縮小表示など)は画面に占める大きさ(DIP 基準のズーム)を保つ
        foreach (double zoom in new[] { 0.25, 0.37, 1.1 / oldScale, 2.8125 })
        {
            Assert.Equal(zoom, DeviceScaling.ZoomAfterScaleChange(zoom, oldScale, newScale));
        }
    }

    private static int Select(TilePyramid pyramid, double zoom, double scale, bool fast)
    {
        return DeviceScaling.SelectRenderFactor(
            pyramid.SelectFactor, factor => pyramid.GetLevel(factor) is not null, zoom, scale, fast);
    }

    private static TilePyramid CreatePyramid()
    {
        // 128×128 では 1/64 まで全レベルが生成される(TilePyramidTests)
        ushort[] values = TestData.MakePattern(128 * 128, 16);
        using RawImage image = TestImages.FromCodes(values, 128, 128);
        TilePyramid pyramid = TilePyramid.Create(image);
        Assert.Equal(new[] { 2, 4, 8, 16, 32, 64 }, pyramid.Levels.Select(l => l.Factor));
        return pyramid;
    }
}
