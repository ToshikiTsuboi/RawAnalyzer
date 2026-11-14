using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RawAnalyzer.App;
using RawAnalyzer.App.Compare;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class GraphDockTests
{
    [Fact]
    public Task DockKeepsPlotSpaceAndViewportOverlaysThenUsesTheTabBandWhenSpaceRunsOut() => WpfTestHost.Run(async () =>
    {
        using var fixture = new Fixture();
        await fixture.ShowAsync();
        var window = fixture.Window;
        var dock = fixture.Dock;
        var viewport = Find<ImageViewport>(window, "Viewport");
        Assert.Empty(fixture.Tabs.Items);
        await fixture.OpenImageAsync();
        WpfTestHost.Toggle(Find<ToggleButton>(window, "HorizontalProjectionToggle"));
        await WaitUntil(() => fixture.SelectedView is ProjectionGraphView { PlotView.Values.Length: > 0 });
        var projection = (ProjectionGraphView)fixture.SelectedView!;
        var canvas = Find<Canvas>(projection.PlotView, "PlotCanvas");
        double defaultPlotHeight = canvas.ActualHeight;
        var row = Find<RowDefinition>(window, "GraphDockRow");
        Capture(window, "c6b-docked-projection");
        Assert.True(defaultPlotHeight >= 140, $"PlotCanvas {defaultPlotHeight:F1} DIP / ドック {row.ActualHeight:F1} DIP / 中央列 {Find<Grid>(window, "GraphLayout").ActualHeight:F1} DIP");
        Assert.True(canvas.ActualWidth >= 200);
        Assert.True(viewport.ActualHeight >= 180);
        Assert.Empty(window.OwnedWindows.OfType<GraphWindow>());

        WpfTestHost.Toggle(Find<ToggleButton>(window, "VerticalProjectionToggle"));
        await WaitUntil(() => fixture.SelectedView is ProjectionGraphView
            { Direction: ProjectionDirection.Vertical, PlotView.Values.Length: > 0 });
        Assert.True(CanvasHeight(fixture.SelectedView!) >= 140);
        WpfTestHost.Toggle(Find<ToggleButton>(window, "ProfileToggle"));
        viewport.Focus();
        IInputElement? focused = FocusManager.GetFocusedElement(window);
        Assert.Same(viewport, focused);
        await fixture.ClickPixelAsync(3, 4);
        await WaitUntil(() => fixture.SelectedView is LineProfileView { CurrentPoint: (3, 4) });
        Assert.Same(focused, FocusManager.GetFocusedElement(window));
        var line = (LineProfileView)fixture.SelectedView!;
        Assert.True(CanvasHeight(line) >= 140);
        var imageArea = Find<Grid>(window, "ViewportLayout");
        Assert.Equal(viewport.TranslatePoint(new Point(), window).X, dock.TranslatePoint(new Point(), window).X, 1);
        foreach (string overlay in new[] { "PlaybackBar", "LevelOverlayBox", "InteractionHint" })
        {
            FrameworkElement item = Find<FrameworkElement>(window, overlay);
            Assert.True(item.IsVisible);
            Assert.InRange(item.TranslatePoint(new Point(), imageArea).Y + item.ActualHeight,
                0, imageArea.ActualHeight + 0.1);
        }
        Capture(window, "c6b-docked-profile");

        var splitter = Find<GridSplitter>(window, "GraphDockSplitter");
        var peer = new GridSplitterAutomationPeer(splitter);
        Assert.IsAssignableFrom<ITransformProvider>(peer.GetPattern(PatternInterface.Transform));
        double originalHeight = row.Height.Value;
        DragSplitter(splitter, 150);
        await LayoutAsync(window);
        Assert.True(row.Height.Value < originalHeight);
        Assert.InRange(CanvasHeight(line), 80, 82);
        double savedHeight = dock.RequestedHeight;
        Assert.Equal(row.Height.Value, savedHeight, 2);
        Assert.True(viewport.ActualHeight >= 180);

        window.Width = 720;
        await LayoutAsync(window);
        Assert.True(dock.IsVisible);
        Assert.True(viewport.ActualWidth < 314);
        Assert.Null(fixture.SelectedView);
        Assert.Equal(GraphKind.LineProfile, dock.SelectedKind);
        Assert.Equal(3, fixture.Tabs.Items.Count);
        Assert.True(Find<Button>(dock, "PopOutGraphButton").IsVisible);
        Assert.True(Find<Button>(dock, "CloseGraphButton").IsVisible);
        Assert.Equal(savedHeight, dock.RequestedHeight);
        Capture(window, "c6b-narrow-tab-band");

        window.Width = 1280;
        await WaitUntil(() => ReferenceEquals(fixture.SelectedView, line));
        await LayoutAsync(window);
        Assert.Equal(savedHeight, row.Height.Value, 2);
        Assert.Equal(GraphKind.LineProfile, dock.SelectedKind);

        double heightNeeded = row.MinHeight + GraphDock.MinimumImageHeight + GraphDock.SplitterHeight;
        double width = dock.ActualWidth;
        var shortLayout = dock.ArrangeDock(width, heightNeeded - 1, compare: false);
        Assert.False(shortLayout.Expanded);
        Assert.Equal(32, shortLayout.Height);
        Assert.Null(fixture.SelectedView);
        Assert.Equal(GraphKind.LineProfile, dock.SelectedKind);
        Assert.Equal(savedHeight, dock.RequestedHeight);
        var enoughSpace = dock.ArrangeDock(width, heightNeeded + 1, compare: false);
        Assert.True(enoughSpace.Expanded);
        Assert.Same(line, fixture.SelectedView);
    });

    [Fact]
    public Task PopOutAndDockBackReuseTheGraphAndWindowCloseResetsTheToggle() => WpfTestHost.Run(async () =>
    {
        using var fixture = new Fixture();
        await fixture.ShowAsync();
        await fixture.OpenImageAsync();
        var window = fixture.Window;
        var horizontal = Find<ToggleButton>(window, "HorizontalProjectionToggle");
        var vertical = Find<ToggleButton>(window, "VerticalProjectionToggle");
        WpfTestHost.Toggle(horizontal);
        await WaitUntil(() => fixture.SelectedView is ProjectionGraphView { PlotView.Values.Length: > 0 });
        var graph = (ProjectionGraphView)fixture.SelectedView!;
        graph.ZoomAt(new Point(110, 40), 120);
        ProfileAxisRange zoom = graph.HorizontalRange;
        string? csv = graph.BuildTable(',');
        WpfTestHost.Toggle(vertical);
        await WaitUntil(() => fixture.Dock.SelectedKind == GraphKind.VerticalProjection && fixture.Dock.IsVisible);
        SelectTab(fixture.Tabs, GraphKind.HorizontalProjection);
        await WaitUntil(() => ReferenceEquals(fixture.SelectedView, graph));
        WpfTestHost.Invoke(Find<Button>(fixture.Dock, "PopOutGraphButton"));
        var floating = await FloatWindowAsync(ProjectionDirection.Horizontal);
        Assert.Same(graph, floating.View);
        Assert.Same(window, floating.Owner);
        Assert.False(floating.ShowActivated);
        Assert.Equal(GraphKind.VerticalProjection, fixture.Dock.SelectedKind);
        WpfTestHost.CaptureDialogIfRequested(floating, "c6b-floating-projection");
        WpfTestHost.Invoke(Find<Button>(fixture.Dock, "PopOutGraphButton"));
        var second = await FloatWindowAsync(ProjectionDirection.Vertical);
        Assert.Equal(floating.Left + 40, second.Left, 1);
        Assert.Equal(floating.Top + 40, second.Top, 1);
        Assert.False(second.ShowActivated);

        WpfTestHost.Invoke(((DockPanel)floating.Content).Children.OfType<Button>().Single());
        await WaitUntil(() => ReferenceEquals(fixture.SelectedView, graph));
        Assert.Equal(csv, graph.BuildTable(','));
        Assert.Equal(zoom, graph.HorizontalRange);
        Assert.True(horizontal.IsChecked);
        second.Close();
        Assert.False(vertical.IsChecked);
        Assert.True(horizontal.IsChecked);
        WpfTestHost.Invoke(Find<Button>(fixture.Dock, "CloseGraphButton"));
        await WaitUntil(() => !fixture.Dock.IsVisible);
        Assert.False(horizontal.IsChecked);

        async Task<ProjectionWindow> FloatWindowAsync(ProjectionDirection direction)
        {
            await WaitUntil(() => window.OwnedWindows.OfType<ProjectionWindow>().Any(item => item.View.Direction == direction));
            return window.OwnedWindows.OfType<ProjectionWindow>().Single(item => item.View.Direction == direction);
        }
    });

    [Fact]
    public Task FullscreenKeepsTheDockAndCompareOnlyHidesDockedGraphs() => WpfTestHost.Run(async () =>
    {
        using var fixture = new Fixture();
        await fixture.ShowAsync();
        await fixture.OpenImageAsync();
        var window = fixture.Window;
        var vm = (MainViewModel)window.DataContext;
        var viewport = Find<ImageViewport>(window, "Viewport");
        WpfTestHost.Toggle(Find<ToggleButton>(window, "HorizontalProjectionToggle"));
        await WaitUntil(() => fixture.SelectedView is ProjectionGraphView);
        WpfTestHost.Invoke(Find<Button>(fixture.Dock, "PopOutGraphButton"));
        await WaitUntil(() => window.OwnedWindows.OfType<ProjectionWindow>().Any());
        var floating = window.OwnedWindows.OfType<ProjectionWindow>().Single();
        vm.IsFullscreen = true;
        SendKey(window, Key.V);
        await WaitUntil(() => fixture.Dock.SelectedKind == GraphKind.VerticalProjection && fixture.Dock.IsVisible);
        Assert.True(fixture.Dock.IsVisible);
        Assert.True(floating.IsVisible);
        vm.IsFullscreen = false;
        await LayoutAsync(window);
        WpfTestHost.Toggle(Find<ToggleButton>(window, "CompareToolToggle"));
        await WaitUntil(() => vm.IsCompareMode);
        await LayoutAsync(window);
        Assert.False(fixture.Dock.IsVisible);
        Assert.True(floating.IsVisible);
        var compare = Find<CompareView>(window, "CompareArea");
        Assert.Equal(Find<Grid>(window, "GraphLayout").ActualHeight, compare.ActualHeight, 1);
        WpfTestHost.Toggle(Find<ToggleButton>(window, "CompareToolToggle"));
        await WaitUntil(() => !vm.IsCompareMode && fixture.Dock.IsVisible);
        Assert.Equal(GraphKind.VerticalProjection, fixture.Dock.SelectedKind);

        WpfTestHost.Toggle(Find<ToggleButton>(window, "ProfileToggle"));
        viewport.Focus();
        IInputElement? focused = FocusManager.GetFocusedElement(window);
        await fixture.ClickPixelAsync(3, 4);
        await WaitUntil(() => fixture.SelectedView is LineProfileView);
        SelectTab(fixture.Tabs, GraphKind.VerticalProjection);
        viewport.SetRoi(new RegionOfInterest(1, 1, 10, 10));
        await LayoutAsync(window);
        Assert.Equal(GraphKind.VerticalProjection, fixture.Dock.SelectedKind);
        var next = Find<Button>(window, "NextFrameButton");
        WpfTestHost.Invoke(next);
        await WaitUntil(() => viewport.Frame == 1);
        Assert.Equal(GraphKind.VerticalProjection, fixture.Dock.SelectedKind);
        viewport.Focus();
        await fixture.ClickPixelAsync(4, 5);
        await WaitUntil(() => fixture.SelectedView is LineProfileView { CurrentPoint: (4, 5) });
        Assert.Same(focused, FocusManager.GetFocusedElement(window));
        var marker = typeof(ImageViewport).GetField("_profileMarkerVisible", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)marker.GetValue(viewport)!);
        WpfTestHost.Invoke(Find<Button>(fixture.Dock, "CloseGraphButton"));
        await WaitUntil(() => fixture.Dock.SelectedKind == GraphKind.VerticalProjection);
        Assert.False((bool)marker.GetValue(viewport)!);
    });

    [Fact]
    public Task SessionRestoresHeightAndPerGraphDestinationWithoutReopeningGraphs() => WpfTestHost.Run(async () =>
    {
        using var fixture = new Fixture();
        await fixture.ShowAsync();
        await fixture.OpenImageAsync();
        fixture.Dock.RequestedHeight = 313;
        WpfTestHost.Toggle(Find<ToggleButton>(fixture.Window, "HorizontalProjectionToggle"));
        await WaitUntil(() => fixture.SelectedView is ProjectionGraphView);
        WpfTestHost.Invoke(Find<Button>(fixture.Dock, "PopOutGraphButton"));
        await WaitUntil(() => fixture.Window.OwnedWindows.OfType<ProjectionWindow>().Any());
        fixture.Window.Close();
        SessionState state = new SessionStore(fixture.Directory).Load();
        Assert.Equal(313, state.GraphDockHeight);
        Assert.Equal(new[] { GraphKind.HorizontalProjection }, state.FloatingGraphs);

        fixture.Reopen();
        await fixture.ShowAsync();
        Assert.False(fixture.Dock.IsVisible);
        Assert.Empty(fixture.Tabs.Items);
        Assert.Empty(fixture.Window.OwnedWindows.OfType<GraphWindow>());
        Assert.Equal(313, fixture.Dock.RequestedHeight);
        await fixture.OpenImageAsync();
        WpfTestHost.Toggle(Find<ToggleButton>(fixture.Window, "HorizontalProjectionToggle"));
        await WaitUntil(() => fixture.Window.OwnedWindows.OfType<ProjectionWindow>().Any());
        WpfTestHost.Toggle(Find<ToggleButton>(fixture.Window, "VerticalProjectionToggle"));
        await WaitUntil(() => fixture.SelectedView is ProjectionGraphView { Direction: ProjectionDirection.Vertical });
        Assert.Equal(313, Find<RowDefinition>(fixture.Window, "GraphDockRow").Height.Value);
    });

    private static void DragSplitter(GridSplitter splitter, double distance)
    {
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        splitter.RaiseEvent(new DragDeltaEventArgs(0, distance) { RoutedEvent = Thumb.DragDeltaEvent });
        splitter.RaiseEvent(new DragCompletedEventArgs(0, distance, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }

    private static void SendKey(Window window, Key key) =>
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
            Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private static void SelectTab(TabControl tabs, GraphKind kind)
    {
        var tab = tabs.Items.OfType<TabItem>().Single(item => Equals(item.Tag, kind));
        var peer = new TabItemAutomationPeer(tab, new TabControlAutomationPeer(tabs));
        ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)!).Select();
    }

    private static double CanvasHeight(GraphView view) => Find<Canvas>(view.PlotView, "PlotCanvas").ActualHeight;

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("RAWANALYZER_UI_SNAPSHOTS") is not { Length: > 0 } directory)
            return;

        // メイン画面は ElementName と DataContext を使うので、結び付けたままクライアントを描画する。
        var view = (FrameworkElement)window.Content;
        DpiScale dpi = VisualTreeHelper.GetDpi(view);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(view.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX,
            dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }

    private static T Find<T>(FrameworkElement root, string name) where T : class =>
        Assert.IsAssignableFrom<T>(root.FindName(name));

    private static async Task LayoutAsync(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        for (int i = 0; i < 150 && !ready(); i++)
            await Task.Delay(30);
        Assert.True(ready());
    }

    private sealed class Fixture : IDisposable
    {
        private bool _closed;
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        internal MainWindow Window { get; private set; }
        internal GraphDock Dock => Find<GraphDock>(Window, "AnalysisGraphDock");
        internal TabControl Tabs => Find<TabControl>(Dock, "GraphTabs");
        internal GraphView? SelectedView => Find<Border>(Dock, "GraphContent").Child as GraphView;

        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            string image = Path.Combine(Directory, "scene.raw");
            File.WriteAllBytes(image, Enumerable.Range(0, 32 * 24 * 2).Select(i => (byte)(i % 220)).ToArray());
            var format = new RawFormat { Width = 32, Height = 24, BitDepth = 8, FrameCount = 2 };
            new SessionStore(Directory).Update(state =>
                SessionStore.TouchFileFormat(state, SessionStore.NormalizeKey(image), format));
            new RecentFilesStore(Directory).Add(image);
            Window = NewWindow();
        }

        private MainWindow NewWindow()
        {
            var window = new MainWindow(Directory) { Width = 1280, Height = 820, ShowActivated = false };
            window.Closed += (_, _) => _closed = true;
            return window;
        }

        internal void Reopen()
        {
            _closed = false;
            Window = NewWindow();
        }

        internal async Task ShowAsync()
        {
            Window.Show();
            await LayoutAsync(Window);
        }

        internal async Task OpenImageAsync()
        {
            var recent = Find<ItemsControl>(Window, "EmptyCanvasRecent");
            await WaitUntil(() => recent.ItemContainerGenerator.ContainerFromIndex(0) is ContentPresenter);
            var item = (ContentPresenter)recent.ItemContainerGenerator.ContainerFromIndex(0);
            WpfTestHost.Invoke(Assert.IsType<Button>(VisualTreeHelper.GetChild(item, 0)));
            await WaitUntil(() => Find<ImageViewport>(Window, "Viewport").Image is not null
                && Window.DataContext is MainViewModel { HasImage: true, IsLoading: false });
            await LayoutAsync(Window);
        }

        internal async Task ClickPixelAsync(int x, int y)
        {
            var viewport = Find<ImageViewport>(Window, "Viewport");
            object? args = typeof(MainWindow).GetMethod("OnProfilePointClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Window, [viewport, new CursorPixelEventArgs { X = x, Y = y, IsInsideImage = true }]);
            Assert.Null(args);
            await LayoutAsync(Window);
        }

        public void Dispose()
        {
            if (!_closed)
                Window.Close();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
