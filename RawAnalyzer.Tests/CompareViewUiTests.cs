using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RawAnalyzer.App.Compare;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class CompareViewUiTests
{
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(4, 2, 2)]
    public Task Layout_OnlyLoadedImagesOccupyGrid(int count, int columns, int rows) => WpfTestHost.Run(async () =>
    {
        using var fixture = new ImageFixture();
        var view = NewView();
        try
        {
            for (int i = 0; i < count; i++) Assert.True(await view.AddPaneFromPathAsync(fixture.Path));
            await ArrangeAsync(view, 1280, 720);
            var grid = (UniformGrid)view.FindName("PaneGrid");
            Assert.Equal(count, grid.Children.Count);
            Assert.Equal(columns, grid.Columns);
            Assert.Equal(rows, grid.Rows);
            Assert.Equal(1280, grid.ActualWidth, 2);
            Assert.True(grid.ActualHeight > 650);

            Assert.Equal(count == 0 ? Visibility.Visible : Visibility.Collapsed,
                ((Button)view.FindName("EmptyAddButton")).Visibility);
            var add = (Button)view.FindName("AddImageButton");
            Assert.Equal(count < 4, add.IsEnabled);
            Assert.Equal(Visibility.Visible, add.Visibility);
            Assert.Equal($"{count} / 4枚", ((TextBlock)view.FindName("PaneCountText")).Text);
            CaptureIfRequested(view, $"compare-{count}");
            if (count == 4)
            {
                Assert.False(await view.AddPaneFromPathAsync(fixture.Path));
                Assert.Equal(4, view.PaneCount);
            }
        }
        finally
        {
            await view.CloseAllAsync();
        }
    });

    [Fact]
    public Task AddButton_PickerCancelAndLoadingRestoreControls() => WpfTestHost.Run(async () =>
    {
        using var fixture = new ImageFixture();
        var view = NewView();
        var picker = new TaskCompletionSource<ComparePane?>();
        int calls = 0;
        view.PanePicker = _ => { calls++; return picker.Task; };
        var add = (Button)view.FindName("AddImageButton");
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(add.IsEnabled);
        Assert.Equal("読込中…", add.Content);
        Assert.False(await view.AddPaneFromPathAsync(fixture.Path));
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, calls);
        picker.SetResult(null);
        await DrainAsync();
        Assert.True(add.IsEnabled);
        Assert.Equal(0, view.PaneCount);
        view.PanePicker = async token => await ComparePane.LoadAsync(fixture.Path, null, token);
        add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            await WaitUntilAsync(() => view.PaneCount == 1 && add.IsEnabled);
            Assert.Equal(Visibility.Collapsed, ((Button)view.FindName("EmptyAddButton")).Visibility);
        }
        finally
        {
            await view.CloseAllAsync();
        }
    });

    [Fact]
    public Task ClosePane_RedistributesSpaceAndRestoresAddButton() => WpfTestHost.Run(async () =>
    {
        using var fixture = new ImageFixture();
        var view = NewView();
        try
        {
            for (int i = 0; i < 4; i++) Assert.True(await view.AddPaneFromPathAsync(fixture.Path));
            await ArrangeAsync(view, 1280, 720);
            for (int count = 3; count >= 0; count--)
            {
                var grid = (UniformGrid)view.FindName("PaneGrid");
                var closing = (ComparePaneView)grid.Children[0];
                Button close = Descendants<Button>(closing).Single(button =>
                    AutomationProperties.GetName(button) == "ペインを閉じる");
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await DrainAsync();
                Assert.Equal(count, view.PaneCount);
                Assert.Equal(count, grid.Children.Count);
                Assert.True(((Button)view.FindName("AddImageButton")).IsEnabled);
                if (count > 0)
                {
                    Assert.Equal("A", ((TextBlock)((ComparePaneView)grid.Children[0]).FindName("LabelText")).Text);
                }
            }

            Assert.Equal(Visibility.Visible, ((Button)view.FindName("EmptyAddButton")).Visibility);
        }
        finally
        {
            await view.CloseAllAsync();
        }
    });

    [Fact]
    public Task CloseAll_AbandonsOldLoadWithoutEnablingNewLoadButtonEarly() => WpfTestHost.Run(async () =>
    {
        using var fixture = new ImageFixture();
        var view = NewView();
        var oldSource = new TaskCompletionSource<ComparePane?>();
        var nextSource = new TaskCompletionSource<ComparePane?>();
        view.PaneLoader = (_, _) => oldSource.Task;
        Task<bool> oldLoad = view.AddPaneFromPathAsync(fixture.Path);
        await view.CloseAllAsync();
        view.PaneLoader = (_, _) => nextSource.Task;
        Task<bool> nextLoad = view.AddPaneFromPathAsync(fixture.Path);
        ComparePane abandoned = await ComparePane.LoadAsync(fixture.Path, null);
        oldSource.SetResult(abandoned);
        Assert.False(await oldLoad);
        Assert.Throws<ObjectDisposedException>(() => abandoned.Image.GetPixel(0, 0));
        Assert.False(((Button)view.FindName("AddImageButton")).IsEnabled);
        nextSource.SetResult(await ComparePane.LoadAsync(fixture.Path, null));
        try
        {
            Assert.True(await nextLoad);
            Assert.Equal(1, view.PaneCount);
            Assert.True(((Button)view.FindName("AddImageButton")).IsEnabled);
        }
        finally
        {
            await view.CloseAllAsync();
        }
    });

    private static CompareView NewView() => new()
    {
        PaneLoader = async (path, token) => await ComparePane.LoadAsync(path, null, token),
    };

    private static async Task ArrangeAsync(CompareView view, int width, int height)
    {
        var renders = new List<Task>();
        var grid = (UniformGrid)view.FindName("PaneGrid");
        foreach (ComparePaneView pane in grid.Children)
        {
            var ready = new TaskCompletionSource();
            EventHandler<RawAnalyzer.App.Controls.ViewportStateEventArgs>? handler = null;
            handler = (_, _) =>
            {
                pane.ViewportControl.ViewportStateChanged -= handler;
                ready.TrySetResult();
            };
            pane.ViewportControl.ViewportStateChanged += handler;
            renders.Add(ready.Task);
        }

        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        foreach (ComparePaneView pane in grid.Children) pane.ViewportControl.FitToView();
        await Task.WhenAll(renders).WaitAsync(TimeSpan.FromSeconds(5));
        await DrainAsync();
    }

    private static Task DrainAsync() => Dispatcher.CurrentDispatcher.InvokeAsync(
        () => { }, DispatcherPriority.ApplicationIdle).Task;

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            Assert.True(DateTime.UtcNow < deadline, "比較画面の更新が完了しませんでした。");
            await Task.Delay(10);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void CaptureIfRequested(FrameworkElement view, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("RAWANALYZER_UI_SNAPSHOTS");
        if (string.IsNullOrEmpty(directory)) return;
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight,
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }

    private sealed class ImageFixture : IDisposable
    {
        internal string Path { get; }

        internal ImageFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            const int width = 480, height = 320;
            var pixels = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * width + x) * 3;
                    pixels[offset] = (byte)(x * 255 / (width - 1));
                    pixels[offset + 1] = (byte)(y * 255 / (height - 1));
                    pixels[offset + 2] = (byte)(((x / 24 + y / 24) % 2 == 0) ? 75 : 150);
                }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Rgb24, null, pixels, width * 3)));
            using var output = File.Create(Path);
            encoder.Save(output);
        }

        public void Dispose() => File.Delete(Path);
    }
}
