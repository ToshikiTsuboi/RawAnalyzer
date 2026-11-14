using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RawAnalyzer.App;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class NavigatorTests
{
    [Fact]
    public void ViewFrame_ClipsOverscanAndMapsBackToImageCoordinates()
    {
        var image = new Size(4000, 2000);
        var thumbnail = new Size(160, 80);
        Rect frame = NavigatorGeometry.ViewFrame(image, thumbnail, new Rect(-300, 500, 1300, 1000));
        Assert.Equal(new Rect(0, 20, 40, 40), frame);
        Assert.Equal(new Point(1000, 1500), NavigatorGeometry.ImagePoint(image, thumbnail, frame.BottomRight));
        Assert.Equal(new Point(0, 2000), NavigatorGeometry.ImagePoint(image, thumbnail, new Point(-4, 90)));
        Assert.True(NavigatorGeometry.Fits(image, new Rect(-100, 0, 4200, 2000)));
        Assert.False(NavigatorGeometry.Fits(image, new Rect(100, 0, 4000, 2000)));
    }
}

[Collection("WPF UI")]
public class NavigatorUiTests
{
    [Fact]
    public Task RenderAsync_DoesNotWaitOnUiThreadAndUsesCoarsestPyramidLevel() => WpfTestHost.Run(async () =>
    {
        var format = new RawFormat { Width = 128, Height = 64, BitDepth = 16 };
        // 各最粗ブロックの平均は同じ。元画像を直接サンプリングすると縞が残る。
        ushort[] pixels = Enumerable.Range(0, format.Width * format.Height)
            .Select(i => (ushort)(i % 2 == 0 ? 0 : 65534)).ToArray();
        using var image = RawImage.FromPixels(format, pixels);
        var pyramid = await TilePyramid.CreateAsync(image);
        DisplayLut lut = DisplayLut.Create(new DisplayParameters());
        var source = new NavigatorThumbnail(pyramid.Levels[^1], null, 1, image.Width, image.Height,
            ViewportDisplayMode.Raw, BayerPattern.None, lut, null, null, 0, null);
        image.Dispose();
        var bitmap = await source.RenderAsync(1.5, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(240, bitmap.PixelWidth);
        Assert.Equal(120, bitmap.PixelHeight);
        Assert.Equal(PixelFormats.Bgra32, bitmap.Format);
        Assert.Equal(144, bitmap.DpiX);
        var output = new byte[240 * 120 * 4];
        bitmap.CopyPixels(output, 240 * 4, 0);
        Assert.All(Enumerable.Range(0, 240 * 120), i =>
        {
            Assert.Equal(lut.Map(32767), output[4 * i]);
            Assert.Equal(255, output[4 * i + 3]);
        });
    });

    [Fact]
    public Task Navigator_HidesAtFitAndMovesFrameWithoutRebuildingThumbnail() => WpfTestHost.Run(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        var window = new MainWindow(directory) { ShowActivated = false };
        var viewport = (ImageViewport)window.FindName("Viewport");
        var format = new RawFormat { Width = 768, Height = 384, BitDepth = 16 };
        using var image = RawImage.FromPixels(format,
            Enumerable.Range(0, format.Width * format.Height)
                .Select(i => (ushort)(i % 768 * 64 + i / 768 * 32)).ToArray());
        try
        {
            window.Show();
            var vm = (RawAnalyzer.App.ViewModels.MainViewModel)window.DataContext;
            vm.HasImage = true;
            vm.HasSequence = true;
            vm.LevelOverlayText = "間引き表示 L1";
            viewport.SetImage(image, format);
            viewport.SetPyramid(await TilePyramid.CreateAsync(image));
            var navigator = (Border)window.FindName("NavigatorBox");
            var preview = (Image)window.FindName("NavigatorImage");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (preview.Source is null) await Task.Delay(10, timeout.Token);
            var thumbnail = preview.Source;
            Assert.Equal(Visibility.Collapsed, navigator.Visibility);

            viewport.CenterOn(192, 192, 3);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(navigator.IsVisible);
            var frame = (FrameworkElement)window.FindName("NavigatorFrame");
            var level = (Border)window.FindName("LevelOverlayBox");
            var relativeTo = (Visual)window.Content;
            Assert.True(navigator.TransformToAncestor(relativeTo).TransformBounds(new Rect(navigator.RenderSize)).Bottom
                < level.TransformToAncestor(relativeTo).TransformBounds(new Rect(level.RenderSize)).Top);
            double oldX = Canvas.GetLeft(frame);
            viewport.CenterOn(576, 192, 3);
            Assert.Same(thumbnail, preview.Source);
            Assert.True(Canvas.GetLeft(frame) > oldX);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, "navigator-zoom");
        }
        finally
        {
            await viewport.ClearImageAsync();
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    });
}
