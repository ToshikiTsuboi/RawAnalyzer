using RawAnalyzer.App.Compare;
using Xunit;

namespace RawAnalyzer.Tests;

public class CompareSyncTests
{
    [Fact]
    public void MapView_Fov_MatchesRelativeWidthAcrossResolutions()
    {
        // ソース: 1600px幅の画像を半分だけ表示(zoom=1, view=800)
        var source = new PaneViewState(1.0, 0, 0, 800, 600, 1600, 1200);
        // ターゲット: 800px幅の画像、ビューは400px
        var target = new PaneViewState(0.123, 5, 7, 400, 300, 800, 600);

        ViewTransform mapped = CompareSync.MapView(CompareSyncMode.FieldOfView, source, target);

        // 同じ相対幅(50%)を写すには 800*0.5=400px 分を view 400px へ → zoom=1
        Assert.Equal(1.0, mapped.Zoom, 10);
        // ソース中心 rel=(0.25, 0.25) → ターゲット中心 (200,150) → 原点 (0,0)
        Assert.Equal(0, mapped.OriginX, 10);
        Assert.Equal(0, mapped.OriginY, 10);
    }

    [Fact]
    public void MapView_Pixel_KeepsZoomAndRelativeCenter()
    {
        var source = new PaneViewState(2.0, 100, 100, 500, 500, 1000, 1000);
        var target = new PaneViewState(1.0, 0, 0, 500, 500, 2000, 2000);

        ViewTransform mapped = CompareSync.MapView(CompareSyncMode.PixelZoom, source, target);

        Assert.Equal(2.0, mapped.Zoom, 10);
        // ソース中心 = 100 + 500/(2*2) = 225 → rel 0.225 → ターゲット中心 450
        // 原点 = 450 - 500/(2*2) = 325
        Assert.Equal(325, mapped.OriginX, 10);
        Assert.Equal(325, mapped.OriginY, 10);
    }

    [Fact]
    public void MapView_Fov_RoundTripsBackToSource()
    {
        var source = new PaneViewState(3.0, 421.5, 260.25, 812, 597, 1920, 1080);
        var target = new PaneViewState(1.0, 0, 0, 640, 512, 640, 480);

        // 同じ形状へ写せば恒等
        ViewTransform same = CompareSync.MapView(CompareSyncMode.FieldOfView, source, source);
        Assert.Equal(source.Zoom, same.Zoom, 10);
        Assert.Equal(source.OriginX, same.OriginX, 10);
        Assert.Equal(source.OriginY, same.OriginY, 10);

        ViewTransform forward = CompareSync.MapView(CompareSyncMode.FieldOfView, source, target);
        var targetState = target with
        {
            Zoom = forward.Zoom,
            OriginX = forward.OriginX,
            OriginY = forward.OriginY,
        };
        ViewTransform back = CompareSync.MapView(CompareSyncMode.FieldOfView, targetState, source);

        Assert.Equal(source.Zoom, back.Zoom, 8);
        Assert.Equal(source.OriginX, back.OriginX, 6);
        Assert.Equal(source.OriginY, back.OriginY, 6);
    }

    [Fact]
    public void MapCursor_ScalesByRelativePosition()
    {
        // 同サイズなら恒等
        (double sameX, double sameY) = CompareSync.MapCursor(5, 7, (10, 10), (10, 10));
        Assert.Equal(5.0, sameX, 10);
        Assert.Equal(7.0, sameY, 10);

        // 20x20の画素(9,9)の中心 rel=0.475 → 40x40では 0.475*40-0.5 = 18.5
        (double x, double y) = CompareSync.MapCursor(9, 9, (20, 20), (40, 40));
        Assert.Equal(18.5, x, 10);
        Assert.Equal(18.5, y, 10);
    }
}
