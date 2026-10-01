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
