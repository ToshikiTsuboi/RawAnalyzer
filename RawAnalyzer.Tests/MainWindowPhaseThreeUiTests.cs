using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RawAnalyzer.App;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class MainWindowPhaseThreeUiTests
{
    [Fact]
    public Task EmptyCanvas_OffersFiveRecentFilesAndCollapsesAfterOpeningOne() => WpfTestHost.Run(async () =>
    {
        using var settings = new TemporaryDirectory();
        var recentStore = new RecentFilesStore(settings.Path);
        string folder = Path.Combine(settings.Path, "captures");
        Directory.CreateDirectory(folder);
        string[] paths = Enumerable.Range(0, 6).Select(i => Path.Combine(folder, $"scene_{i}.png")).ToArray();
        var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Gray8, null, new byte[64], 8);
        foreach (string path in paths)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
            recentStore.Add(path);
        }

        var window = new MainWindow(settings.Path) { ShowActivated = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var vm = (MainViewModel)window.DataContext;
            var emptyCanvas = Find<Border>(window, "EmptyCanvas");
            var banner = Find<Border>(window, "AdjustPanelReasonBanner");
            var display = Find<Expander>(window, "DisplaySection");
            var recent = Find<ItemsControl>(window, "EmptyCanvasRecent");
            Assert.True(emptyCanvas.IsVisible);
            Assert.True(banner.IsVisible);
            Assert.False(display.IsEnabled);
            Assert.Equal(5, recent.Items.Count);
            var entry = Assert.IsType<ContentPresenter>(recent.ItemContainerGenerator.ContainerFromIndex(0));
            var button = Assert.IsType<Button>(VisualTreeHelper.GetChild(entry, 0));
            Assert.Equal(paths[^1], button.ToolTip);
            var details = Assert.IsType<StackPanel>(button.Content);
            Assert.Equal("scene_5.png", Assert.IsType<TextBlock>(details.Children[0]).Text);
            Assert.Equal("captures", Assert.IsType<TextBlock>(details.Children[1]).Text);
            var drag = (DragEventArgs)typeof(DragEventArgs)
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single().Invoke(new object[]
                {
                    new DataObject(DataFormats.FileDrop, new[] { paths[^1] }),
                    DragDropKeyStates.None, DragDropEffects.Copy, emptyCanvas, new Point(),
                });
            drag.RoutedEvent = DragDrop.DragOverEvent;
            emptyCanvas.RaiseEvent(drag);
            Assert.Same(window.FindResource("AccentBrush"), emptyCanvas.BorderBrush);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, "empty-canvas-drop");

            drag.Handled = false;
            drag.RoutedEvent = DragDrop.PreviewDragLeaveEvent;
            emptyCanvas.RaiseEvent(drag);
            Assert.Same(Brushes.Transparent, emptyCanvas.BorderBrush);

            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.HasImage) && vm.HasImage)
                {
                    opened.TrySetResult();
                }
            };
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Collapsed, emptyCanvas.Visibility);
            Assert.Equal(Visibility.Collapsed, banner.Visibility);
            Assert.True(display.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ToolStrip_UsesExclusiveModesAndSynchronizesWithMenu() => WpfTestHost.Run(async () =>
    {
        using var settings = new TemporaryDirectory();
        var window = new MainWindow(settings.Path) { ShowActivated = false };
        try
        {
            window.Show();
            var vm = (MainViewModel)window.DataContext;
            vm.HasImage = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var viewport = Find<ImageViewport>(window, "Viewport");
            var hint = Find<Border>(window, "InteractionHint");
            ToggleButton[] modes =
            [
                Find<ToggleButton>(window, "PanToggle"), Find<ToggleButton>(window, "RoiToggle"),
                Find<ToggleButton>(window, "ProfileToggle"), Find<ToggleButton>(window, "CanvasWbPickToggle"),
            ];
            var wb = Find<ToggleButton>(window, "WbPickToggle");
            var toolMenu = Find<Menu>(window, "MainMenuBar").Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "ツール(_T)"));
            MenuItem[] menuModes = new[] { "ROI選択モード", "ラインプロファイルモード", "WBスポイトモード" }
                .Select(name => toolMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, name))).ToArray();

            void AssertActive(int index, ViewportInteractionMode mode)
            {
                Assert.Equal(mode, viewport.InteractionMode);
                Assert.Same(modes[index], Assert.Single(modes, button => button.IsChecked == true));
                Assert.Equal(index == 3, wb.IsChecked);
                Assert.Equal(menuModes.Select((_, i) => i + 1 == index), menuModes.Select(item => item.IsChecked));
                Assert.Equal(ViewportModeHint.For(mode), hint.Tag);
                Assert.Equal(index == 0 ? Visibility.Collapsed : Visibility.Visible, hint.Visibility);
            }

            AssertActive(0, ViewportInteractionMode.Pan);
            Toggle(modes[1]);
            AssertActive(1, ViewportInteractionMode.RoiSelect);
            Toggle(modes[2]);
            AssertActive(2, ViewportInteractionMode.LineProfile);
            vm.GainDb = 3;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, "tool-strip-hint-and-adjustments");

            Find<Expander>(window, "WhiteBalanceSection").IsExpanded = false;
            Toggle(modes[3]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            AssertActive(3, ViewportInteractionMode.WhiteBalancePick);
            menuModes[0].SetCurrentValue(MenuItem.IsCheckedProperty, true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            AssertActive(1, ViewportInteractionMode.RoiSelect);
            modes[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            AssertActive(0, ViewportInteractionMode.Pan);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Toolbar_CompareAndCommandSearchWorkAtWidth720() => WpfTestHost.Run(async () =>
    {
        using var settings = new TemporaryDirectory();
        var window = new MainWindow(settings.Path) { Width = 720, ShowActivated = false };
        try
        {
            window.Show();
            var vm = (MainViewModel)window.DataContext;
            var compare = Find<ToggleButton>(window, "CompareToolToggle");
            var strip = Find<Border>(window, "CanvasToolStrip");
            var search = Find<Button>(window, "CommandSearchToolButton");
            var toolbar = Find<ToolBar>(window, "ToolBarPanel");
            var viewMenu = Find<Menu>(window, "MainMenuBar").Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "表示(_V)"));
            var compareMenu = viewMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "比較モード (複数画像を並べる)"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            toolbar.IsOverflowOpen = ToolBar.GetIsOverflowItem(compare);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(compare.IsVisible);
            compare.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(vm.IsCompareMode);
            Assert.True(compare.IsChecked);
            Assert.True(compareMenu.IsChecked);
            Assert.Equal(Visibility.Collapsed, strip.Visibility);

            compareMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(vm.IsCompareMode);
            Assert.False(compare.IsChecked);
            Assert.False(compareMenu.IsChecked);
            Assert.True(strip.IsVisible);

            toolbar.IsOverflowOpen = ToolBar.GetIsOverflowItem(search);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(search.IsVisible);
            Assert.Contains("Ctrl+Shift+P", Assert.IsType<string>(search.ToolTip));
            search.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.IsType<CommandPaletteWindow>(Assert.Single(window.OwnedWindows.Cast<Window>())).Close();
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AdjustmentSections_RestoreTheirStateWhenWindowReopens() => WpfTestHost.Run(async () =>
    {
        using var settings = new TemporaryDirectory();
        string[] names = ["DisplaySection", "HistogramSection", "WhiteBalanceSection", "ColorMatrixSection", "FormatSection"];
        var window = new MainWindow(settings.Path) { ShowActivated = false };
        try
        {
            window.Show();
            ((MainViewModel)window.DataContext).HasImage = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var sections = names.Select(name => Find<Expander>(window, name)).ToArray();
            Assert.Equal(new[] { true, true, true, false, false }, sections.Select(section => section.IsExpanded));
            foreach (Expander section in sections)
            {
                var header = Assert.IsType<ToggleButton>(section.Template.FindName("HeaderSite", section));
                Toggle(header);
            }

            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)window.Content;
            var panel = Find<Border>(window, "RightPanel");
            Rect bounds = panel.TransformToAncestor(content).TransformBounds(new Rect(panel.RenderSize));
            WpfTestHost.CaptureIfRequested(content, "adjustment-sections", bounds);
        }
        finally
        {
            window.Close();
        }

        var reopened = new MainWindow(settings.Path);
        try
        {
            Assert.Equal(new[] { false, false, false, true, true },
                names.Select(name => Find<Expander>(reopened, name).IsExpanded));
        }
        finally
        {
            reopened.Close();
        }
    });

    private static T Find<T>(Window window, string name) => Assert.IsType<T>(window.FindName(name));

    private static void Toggle(ToggleButton button) =>
        ((IToggleProvider)new ToggleButtonAutomationPeer(button).GetPattern(PatternInterface.Toggle)!).Toggle();

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "RawAnalyzerTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
