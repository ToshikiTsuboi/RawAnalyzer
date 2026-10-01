using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 表示倍率(DPI)が 100% 以外のときの ImageViewport の描画。実際の DPI はテストで変えられないので、
/// 倍率を固定するフック(OverrideDeviceScale)で 125%・150%・200% を再現する。
/// </summary>
[Collection("WPF UI")]
public class ImageViewportDeviceScaleTests
{
    private const int ViewWidth = 240;
    private const int ViewHeight = 180;

    // 位置ごとに値が変わる模様(列・行の取り違えや重なりがあれば食い違う)
    private static readonly ushort[] PatternValues = { 8000, 30000, 56000 };

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public Task DrawsBitmapAtDeviceResolution(double scale) => WpfTestHost.Run(async () =>
    {
        // 以前は表示倍率によらず DIP の大きさ(240×180)の 96dpi のビットマップを描き、
        // それが表示倍率ぶん最近傍で引き伸ばされていた。デバイス画素の数だけ描き、各デバイス画素には
        // その中心に写る元画像の画素を描く
        (ImageViewport viewport, RawImage image) = CreateViewport(scale, 64, 64);
        try
        {
            const double zoom = 2.5;
            await CenterOnAndWaitAsync(viewport, 20, 30, zoom);

            (BitmapSource bitmap, Rect placed, byte[] pixels) = Rendered(viewport);
            Assert.Equal(DeviceScaling.DevicePixels(ViewWidth, scale), bitmap.PixelWidth);
            Assert.Equal(DeviceScaling.DevicePixels(ViewHeight, scale), bitmap.PixelHeight);
            Assert.Equal(96 * scale, bitmap.DpiX, 9);
            Assert.Equal(new Rect(0, 0, ViewWidth, ViewHeight), placed);

            Assert.Empty(PatternMismatches(viewport, image, bitmap, pixels, zoom * scale));
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    [Theory]
    [InlineData(1.0, 4)]
    [InlineData(1.25, 2)]
    [InlineData(1.5, 2)]
    [InlineData(2.0, 2)]
    public Task ZoomedOut_UsesPyramidLevelForDevicePixels(double scale, int expectedFactor) =>
        WpfTestHost.Run(async () =>
    {
        // 縮小表示(全体表示など)の品質パスはデバイス画素に見合う縮小レベルで描く。以前は DIP 基準で選び、
        // 高DPIではモニタの解像度より粗いレベル(ズーム 1/4 で常に 1/4)を引き伸ばしていた
        const int size = 128;
        (ImageViewport viewport, RawImage image) = CreateViewport(scale, size, size);
        TilePyramid pyramid = TilePyramid.Create(image);
        try
        {
            viewport.SetPyramid(pyramid);
            const double zoom = 0.25;
            ViewportStateEventArgs shown = await CenterOnAndWaitAsync(viewport, size / 2, size / 2, zoom);

            Assert.Equal(expectedFactor, shown.RenderedFactor);
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    [Fact]
    public Task DpiChange_RedrawsAtNewDeviceResolution() => WpfTestHost.Run(async () =>
    {
        // 別の倍率のモニタへ移した・表示スケールを変えたときは、新しいデバイス解像度で描き直す
        (ImageViewport viewport, RawImage image) = CreateViewport(1.0, 64, 64);
        try
        {
            await CenterOnAndWaitAsync(viewport, 20, 30, 2.5);
            Assert.Equal(ViewWidth, Rendered(viewport).Bitmap.PixelWidth);

            Task<ViewportStateEventArgs> redrawn = NextPresentAsync(viewport, _ => true);
            viewport.OverrideDeviceScale(1.5);
            await redrawn.WaitAsync(TimeSpan.FromSeconds(10));

            (BitmapSource bitmap, Rect placed, byte[] pixels) = Rendered(viewport);
            Assert.Equal(360, bitmap.PixelWidth);
            Assert.Equal(270, bitmap.PixelHeight);
            Assert.Equal(new Rect(0, 0, ViewWidth, ViewHeight), placed);
            Assert.Empty(PatternMismatches(viewport, image, bitmap, pixels, viewport.Zoom * 1.5));
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public Task ActualSize_MapsEachImagePixelToOneDevicePixel(double scale) => WpfTestHost.Run(async () =>
    {
        // 「等倍 (1:1)」は元画像の 1 画素を画面の 1 デバイス画素に写す。以前は 1 画素 = 1 DIP にしていたため、
        // 125% では元画像の画素が 1,1,1,2 デバイス画素、150% では 1,2,1,2 デバイス画素と周期的に幅を変え、
        // 列 FPN に見える偽の縞が出ていた
        (ImageViewport viewport, RawImage image) = CreateViewport(scale, 64, 64);
        try
        {
            // 縮小レベルがあっても、操作直後の速報から元画像のまま描く(デバイス基準で等倍以上は粗いレベルへ
            // 落とさない。DIP 基準のズーム 1/倍率 で判断すると、200% の等倍の速報が 1/4 のレベルになる)
            viewport.SetPyramid(TilePyramid.Create(image));
            Task<ViewportStateEventArgs> shown = NextPresentAsync(viewport, _ => true);
            viewport.ActualSize();
            ViewportStateEventArgs first = await shown.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1 / scale, viewport.Zoom, 12);
            Assert.Equal(1.0, first.DeviceZoom); // ステータスバーの倍率は 100%
            Assert.Equal(1, first.RenderedFactor);
            (BitmapSource bitmap, _, byte[] pixels) = Rendered(viewport);
            Assert.Empty(PatternMismatches(viewport, image, bitmap, pixels, deviceZoom: 1.0));
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    [Fact]
    public Task DpiChange_KeepsActualSize_AndKeepsFitSize() => WpfTestHost.Run(async () =>
    {
        // 別の倍率のモニタへ移しても、等倍は元画像の 1 画素を 1 デバイス画素に写し続ける。
        // 全体表示のように整数倍でないズームは、画面に占める大きさ(DIP 基準のズーム)を保つ
        (ImageViewport viewport, RawImage image) = CreateViewport(1.0, 64, 64);
        try
        {
            Task<ViewportStateEventArgs> shown = NextPresentAsync(viewport, e => e.Zoom == 1.0);
            viewport.ActualSize();
            await shown.WaitAsync(TimeSpan.FromSeconds(10));

            Task<ViewportStateEventArgs> redrawn = NextPresentAsync(viewport, _ => true);
            viewport.OverrideDeviceScale(1.5);
            await redrawn.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1 / 1.5, viewport.Zoom, 12);
            (BitmapSource bitmap, _, byte[] pixels) = Rendered(viewport);
            Assert.Empty(PatternMismatches(viewport, image, bitmap, pixels, deviceZoom: 1.0));

            viewport.FitToView();
            double fit = viewport.Zoom;
            Assert.Equal(ViewHeight / 64.0, fit); // 縦で収まる 2.8125(1.5 倍しても整数にならない)
            redrawn = NextPresentAsync(viewport, _ => true);
            viewport.OverrideDeviceScale(1.25);
            await redrawn.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(fit, viewport.Zoom);
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public Task RawOverlay_AppearsAtDeviceZoom32_AndFitsItsCell(double scale) => WpfTestHost.Run(async () =>
    {
        // raw 値オーバーレイは元画像 1 画素が 32 デバイス画素以上(倍率表示 3200% 以上)で出す。
        // 「ここを拡大」・欠陥一覧からの移動はこのズーム(RawOverlayZoom)へ寄せる。高DPIではこのときの
        // マスが 32 DIP より小さい(200% で 16 DIP)ので、コード値 5 桁がマスに収まる大きさの文字で描く
        const int size = 16;
        var format = new RawFormat { Width = size, Height = size, BitDepth = 16 };
        var codes = new ushort[size * size];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(60000 + i); // 5 桁。中央 (8,8) は 60136
        }

        using RawImage image = TestImages.FromCodes(codes, format);
        var viewport = new ImageViewport();
        viewport.OverrideDeviceScale(scale);
        viewport.Measure(new Size(ViewWidth, ViewHeight));
        viewport.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        viewport.SetImage(image, format);
        try
        {
            double zoom = viewport.RawOverlayZoom;
            Assert.Equal(32 / scale, zoom, 12);
            ViewportStateEventArgs shown = await CenterOnAndWaitAsync(viewport, 8, 8, zoom);
            Assert.Equal(32.0, shown.DeviceZoom); // ステータスバーの倍率は 3200%

            viewport.UpdateLayout(); // OnRender → 画素値オーバーレイ
            List<GlyphRun> runs = CollectGlyphRuns(VisualTreeHelper.GetDrawing(viewport));
            Assert.Contains(runs, run => new string(run.Characters.ToArray()) == "60136");
            Assert.All(runs, run => Assert.True(
                run.AdvanceWidths.Sum() <= zoom,
                $"{new string(run.Characters.ToArray())}: 幅 {run.AdvanceWidths.Sum()} > マス {zoom}"));
        }
        finally
        {
            await viewport.ClearImageAsync();
        }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public Task CaptureView_CopiesWhatIsOnScreenAtDeviceResolution(double scale) => WpfTestHost.Run(async () =>
    {
        // 「表示をクリップボードへコピー」は画面と同じデバイス画素で作る。以前のように DIP の大きさの 96dpi で
        // 作ると、デバイス解像度で描いた表示が最近傍で表示倍率ぶん間引かれ、等倍でも元画像の画素が不規則に抜ける
        (ImageViewport viewport, RawImage image) = CreateViewport(scale, 64, 64);
        try
        {
            Task<ViewportStateEventArgs> shown = NextPresentAsync(viewport, _ => true);
            viewport.ActualSize();
            await shown.WaitAsync(TimeSpan.FromSeconds(10));
            (BitmapSource onScreen, _, byte[] expected) = Rendered(viewport);

            RenderTargetBitmap copy = viewport.CaptureView();

            Assert.Equal(onScreen.PixelWidth, copy.PixelWidth);
            Assert.Equal(onScreen.PixelHeight, copy.PixelHeight);
            var copied = new byte[copy.PixelWidth * copy.PixelHeight * 4];
            copy.CopyPixels(copied, copy.PixelWidth * 4, 0);
            int differing = Enumerable.Range(0, expected.Length).Count(i => copied[i] != expected[i]);
            Assert.Equal(0, differing);
        }
        finally
        {
            await viewport.ClearImageAsync();
            image.Dispose();
        }
    });

    private static List<GlyphRun> CollectGlyphRuns(Drawing? drawing)
    {
        var runs = new List<GlyphRun>();
        Collect(drawing);
        return runs;

        void Collect(Drawing? node)
        {
            switch (node)
            {
                case DrawingGroup group:
                    foreach (Drawing child in group.Children)
                    {
                        Collect(child);
                    }

                    break;
                case GlyphRunDrawing { GlyphRun: { } run }:
                    runs.Add(run);
                    break;
            }
        }
    }

    private static (ImageViewport Viewport, RawImage Image) CreateViewport(double scale, int width, int height)
    {
        var codes = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                codes[y * width + x] = PatternValue(x, y);
            }
        }

        var format = new RawFormat { Width = width, Height = height, BitDepth = 16 };
        RawImage image = TestImages.FromCodes(codes, format);
        var viewport = new ImageViewport();
        viewport.OverrideDeviceScale(scale);
        viewport.Measure(new Size(ViewWidth, ViewHeight));
        viewport.Arrange(new Rect(0, 0, ViewWidth, ViewHeight));
        viewport.SetImage(image, format);
        return (viewport, image);
    }

    private static ushort PatternValue(int x, int y) => PatternValues[(x + 2 * y) % PatternValues.Length];

    /// <summary>
    /// 描画結果の各デバイス画素が、その中心に写る元画像の画素(デバイス基準のズームで求める)の値で
    /// 描かれていない位置。
    /// </summary>
    private static List<string> PatternMismatches(
        ImageViewport viewport, RawImage image, BitmapSource bitmap, byte[] pixels, double deviceZoom)
    {
        DisplayLut lut = DisplayLut.Create(new DisplayParameters());
        var mismatches = new List<string>();
        for (int dy = 0; dy < bitmap.PixelHeight; dy++)
        {
            double sourceY = viewport.OriginY + (dy + 0.5) / deviceZoom;
            for (int dx = 0; dx < bitmap.PixelWidth; dx++)
            {
                double sourceX = viewport.OriginX + (dx + 0.5) / deviceZoom;
                byte actual = pixels[(dy * bitmap.PixelWidth + dx) * 4];
                byte expected = sourceX < 0 || sourceY < 0 || sourceX >= image.Width || sourceY >= image.Height
                    ? ViewportRenderer.BackgroundGray
                    : lut.Map(PatternValue((int)sourceX, (int)sourceY));
                if (actual != expected)
                {
                    mismatches.Add($"({dx},{dy}): {actual}(期待 {expected})");
                }
            }
        }

        return mismatches;
    }

    /// <summary>CenterOn の描画(品質パス)が表示されるまで待つ。</summary>
    private static async Task<ViewportStateEventArgs> CenterOnAndWaitAsync(
        ImageViewport viewport, int x, int y, double zoom)
    {
        Task<ViewportStateEventArgs> shown = NextPresentAsync(viewport, e => e.Zoom == zoom);
        viewport.CenterOn(x, y, zoom);
        return await shown.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Task<ViewportStateEventArgs> NextPresentAsync(
        ImageViewport viewport, Func<ViewportStateEventArgs, bool> match)
    {
        var shown = new TaskCompletionSource<ViewportStateEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ViewportStateEventArgs>? handler = null;
        handler = (_, e) =>
        {
            if (match(e))
            {
                viewport.ViewportStateChanged -= handler;
                shown.TrySetResult(e);
            }
        };
        viewport.ViewportStateChanged += handler;
        return shown.Task;
    }

    /// <summary>直近に表示された描画結果のビットマップ、置いた矩形(DIP)と画素(BGRA)。</summary>
    private static (BitmapSource Bitmap, Rect Placed, byte[] Pixels) Rendered(ImageViewport viewport)
    {
        viewport.UpdateLayout(); // OnRender → 描画結果のビットマップを描く
        ImageDrawing drawing = Assert.Single(CollectImages(VisualTreeHelper.GetDrawing(viewport)));
        var bitmap = (BitmapSource)drawing.ImageSource;
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return (bitmap, drawing.Rect, pixels);
    }

    private static List<ImageDrawing> CollectImages(Drawing? drawing)
    {
        var images = new List<ImageDrawing>();
        Collect(drawing);
        return images;

        void Collect(Drawing? node)
        {
            switch (node)
            {
                case DrawingGroup group:
                    foreach (Drawing child in group.Children)
                    {
                        Collect(child);
                    }

                    break;
                case ImageDrawing image:
                    images.Add(image);
                    break;
            }
        }
    }
}
