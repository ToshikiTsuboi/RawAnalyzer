using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RawAnalyzer.App;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class StatusBarUiTests
{
    [Fact]
    public Task CursorValue_DoesNotMoveWhenCoordinateDigitsChange() => WpfTestHost.Run(() =>
    {
        var window = new MainWindow(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))
            { Width = 720, ShowActivated = false };
        try
        {
            window.Show();
            var vm = (MainViewModel)window.DataContext;
            vm.HasImage = true;
            var bar = (Border)window.FindName("StatusBarPanel");
            var value = (TextBlock)window.FindName("CursorStatusValue");
            vm.SetCursorStatus(new CursorReadout.Text("(9, 9)", "raw=2", "R", ""));
            window.UpdateLayout();
            double left = value.TranslatePoint(new Point(), bar).X;
            vm.SetCursorStatus(new CursorReadout.Text("(32767, 32767)", "raw=65535", "Gr", ""));
            window.UpdateLayout();
            Assert.Equal("raw=65535", value.Text);
            Assert.Equal(left, value.TranslatePoint(new Point(), bar).X);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task RestoreOriginal_ReopensProcessedRawWithItsLoadedFormat() => WpfTestHost.Run(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "capture.raw");
        var format = new RawFormat { Width = 16, Height = 16, BitDepth = 12, Bayer = BayerPattern.Rggb, FrameCount = 2 };
        byte[] data = new byte[16 * 16 * 2 * 2];
        data[0] = 13;
        data[16 * 16 * 2] = 21;
        File.WriteAllBytes(path, data);
        new SessionStore(directory).Update(session => SessionStore.TouchFileFormat(session, SessionStore.NormalizeKey(path), format));
        new RecentFilesStore(directory).Add(path);
        var window = new MainWindow(directory) { ShowActivated = false };
        try
        {
            window.Show();
            var vm = (MainViewModel)window.DataContext;
            var viewport = (ImageViewport)window.FindName("Viewport");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var recent = (ItemsControl)window.FindName("EmptyCanvasRecent");
            var item = (ContentPresenter)recent.ItemContainerGenerator.ContainerFromIndex(0);
            var open = (Button)System.Windows.Media.VisualTreeHelper.GetChild(item, 0);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => viewport.Image is not null);
            RawImage source = viewport.Image!;
            viewport.SetFrame(1);

            using RawImage processed = RawImage.FromPixels(format with { FrameCount = 1 }, new ushort[16 * 16]);
            var apply = typeof(MainWindow).GetMethod("ApplyProcessedImageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True(await (Task<bool>)apply.Invoke(window, [source, processed, "テスト加工", null, null, null])!);
            Assert.True(vm.IsProcessed);
            Assert.Equal((ushort)0, viewport.Image!.GetPixel(0, 0));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, "status-processed");

            var badge = (Button)window.FindName("ProcessedBadge");
            badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(badge.ContextMenu.IsOpen);
            var restore = Assert.IsType<MenuItem>(Assert.Single(badge.ContextMenu.Items));
            Assert.Equal("元の画像に戻す", restore.Header);
            restore.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            badge.ContextMenu.IsOpen = false;
            await WaitForAsync(() => !vm.IsProcessed && viewport.Image is not null);
            Assert.Equal(1, viewport.Frame);
            Assert.Equal((ushort)(21 << 4), viewport.Image!.GetPixel(0, 0, viewport.Frame));
        }
        finally
        {
            window.Close();
            await viewportCleanup();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        async Task viewportCleanup()
        {
            await ((ImageViewport)window.FindName("Viewport")).ClearImageAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    });

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
