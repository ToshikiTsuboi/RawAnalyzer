using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RawAnalyzer.App;
using RawAnalyzer.App.Compare;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Themes;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class MainWindowPhaseOneUiTests
{
    [Theory]
    [InlineData(720)]
    [InlineData(860)]
    [InlineData(1000)]
    [InlineData(1280)]
    public Task Toolbar_CommandsFitInBarOrOverflowInPriorityOrder(int width) => WpfTestHost.Run(async () =>
    {
        using var settings = new TemporaryDirectory();
        var window = new MainWindow(settings.Path) { Width = width, ShowActivated = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var vm = (MainViewModel)window.DataContext;
            vm.HasImage = true;
            var toolbar = (ToolBar)window.FindName("ToolBarPanel");
            var main = (ToolBarPanel)toolbar.Template.FindName("PART_ToolBarPanel", toolbar);
            var overflow = (ToolBarOverflowPanel)toolbar.Template.FindName("PART_ToolBarOverflowPanel", toolbar);
            var button = (ToggleButton)toolbar.Template.FindName("OverflowButton", toolbar);

            var pinned = Assert.IsType<StackPanel>(toolbar.Items[0]);
            Assert.Equal(2, pinned.Children.OfType<Button>().Count());
            var mainViewTools = (StackPanel)window.FindName("MainViewTools");
            Assert.Equal(4, mainViewTools.Children.OfType<Button>().Count());
            Assert.IsType<ComboBox>(window.FindName("DisplayModeCombo"));
            Assert.False(ToolBar.GetIsOverflowItem(pinned));
            Assert.True(new Rect(toolbar.RenderSize).Contains(BoundsIn(pinned, toolbar)));

            var items = toolbar.Items.OfType<ButtonBase>().ToArray();
            Assert.Equal(new[] { "ZebraToggle", "CompareToolToggle", "SaveToolButton", "CommandSearchToolButton" },
                items.Select(item => item.Name));
            int firstOverflow = Array.FindIndex(items, ToolBar.GetIsOverflowItem);
            if (width == 720) Assert.True(firstOverflow >= 0);
            if (width == 1280) Assert.Equal(-1, firstOverflow);
            Assert.Equal(firstOverflow >= 0, toolbar.HasOverflowItems);
            Assert.Equal(firstOverflow >= 0, button.IsVisible);
            if (firstOverflow >= 0)
            {
                WpfTestHost.Toggle(button);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.True(toolbar.IsOverflowOpen);
                Assert.True(overflow.IsVisible);
                var overflowItems = items.Skip(firstOverflow).ToArray();
                Assert.All(overflowItems, item =>
                {
                    Assert.Equal(overflowItems[0].ActualWidth, item.ActualWidth);
                    Assert.Equal(HorizontalAlignment.Left, item.HorizontalContentAlignment);
                });
            }

            for (int i = 0; i < items.Length; i++)
            {
                FrameworkElement item = items[i];
                Panel panel = firstOverflow >= 0 && i >= firstOverflow ? overflow : main;
                Assert.Same(panel, VisualTreeHelper.GetParent(item));
                Assert.True(item.IsVisible);
                Assert.True(item.IsEnabled);
                Assert.True(item.ActualWidth > 0);
                Assert.True(new Rect(panel.RenderSize).Contains(BoundsIn(item, panel)));
            }

            if (width == 720)
            {
                WpfTestHost.CaptureIfRequested((FrameworkElement)overflow.Parent, "main-toolbar-overflow-720");
                WpfTestHost.Toggle(button);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.False(toolbar.IsOverflowOpen);
            }
            if (width is 720 or 1280)
            {
                WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, $"main-toolbar-{width}");
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task RightPanel_ActionButtonsStayOnOneRowAtDefaultWidthWithScrollbar() => WpfTestHost.Run(async () =>
    {
        using var settings = new TemporaryDirectory();
        var window = new MainWindow(settings.Path) { ShowActivated = false };
        try
        {
            window.Show();
            ((MainViewModel)window.DataContext).HasImage = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            var panel = (Border)window.FindName("RightPanel");
            var scroll = Descendants<ScrollViewer>(panel).First();
            Assert.Equal(1280, window.ActualWidth, 0);
            Assert.Equal(820, window.ActualHeight, 0);
            Assert.Equal(240, panel.ActualWidth, 0);
            Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);

            foreach (string name in new[] { "DisplayActionRow", "WhiteBalanceActionRow" })
            {
                var row = (WrapPanel)window.FindName(name);
                ButtonBase[] buttons = row.Children.OfType<ButtonBase>().ToArray();
                Assert.Equal(2, buttons.Length);
                Rect first = BoundsIn(buttons[0], row);
                Rect second = BoundsIn(buttons[1], row);
                Assert.True(first.Right <= second.Left && second.Right <= row.ActualWidth + 0.01,
                    $"{name}: {first} / {second} / {row.ActualWidth}");
                Assert.InRange(Math.Abs(first.Top - second.Top), 0, 1);
            }

            WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, "right-panel-240");
            ((WrapPanel)window.FindName("WhiteBalanceActionRow")).BringIntoView();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, "right-panel-240-scrolled");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task RightPanel_ButtonsFitAtMinimumWidth() => WpfTestHost.Run(async () =>
    {
        using var settings = new TemporaryDirectory();
        var window = new MainWindow(settings.Path) { Width = 1000, ShowActivated = false };
        try
        {
            window.Show();
            ((ColumnDefinition)window.FindName("RightColumn")).Width = new GridLength(190);
            var vm = (MainViewModel)window.DataContext;
            vm.HasImage = true;
            vm.HistogramIsSampled = true;
            ((Expander)window.FindName("ColorMatrixSection")).IsExpanded = true;
            ((Expander)window.FindName("FormatSection")).IsExpanded = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            var panel = (Border)window.FindName("RightPanel");
            var scroll = Descendants<ScrollViewer>(panel).First();
            var content = (FrameworkElement)scroll.Content;
            var buttons = Descendants<ButtonBase>(content).Where(button => button.IsVisible
                && button is not RepeatButton && button.TemplatedParent is null).ToArray();
            Assert.Equal(190, panel.ActualWidth, 0);
            Assert.True(buttons.Length >= 8);
            Assert.All(Descendants<NumericSliderRow>(content), row =>
            {
                Assert.True(row.IsCompact);
                Assert.True(((Slider)row.FindName("ValueSlider")).ActualWidth >= 120);
            });
            Assert.All(buttons, button =>
            {
                Rect bounds = BoundsIn(button, content);
                Assert.True(bounds.Left >= 0 && bounds.Right <= content.ActualWidth + 0.01,
                    $"右パネル: {button.Name} {button.Content} {bounds} / {content.ActualWidth}");
            });
            foreach (string name in new[] { "DisplayActionRow", "WhiteBalanceActionRow" })
            {
                var row = (WrapPanel)window.FindName(name);
                Assert.True(BoundsIn((FrameworkElement)row.Children[0], row).Bottom
                    <= BoundsIn((FrameworkElement)row.Children[1], row).Top);
            }
            WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, "right-panel-190");
        }
        finally
        {
            window.Close();
        }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    [Fact]
    public Task Toolbar_OverflowButtonClosesOnSecondPress() => WpfTestHost.Run(async () =>
    {
        using var settings = new TemporaryDirectory();
        var window = new MainWindow(settings.Path) { Width = 720, ShowActivated = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var toolbar = (ToolBar)window.FindName("ToolBarPanel");
            var button = (ToggleButton)toolbar.Template.FindName("OverflowButton", toolbar);
            var popup = (Popup)toolbar.Template.FindName("OverflowPopup", toolbar);
            Assert.Equal(ClickMode.Press, button.ClickMode);
            Assert.True(toolbar.HasOverflowItems);
            Assert.False(popup.IsOpen);
            for (int click = 0; click < 2; click++)
            {
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.MouseDownEvent });
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(click == 0, toolbar.IsOverflowOpen);
                Assert.Equal(click == 0, popup.IsOpen);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public Task CompareFileNames_MixedRawNamesRemainDistinctAtWindowWidth720(int paneCount) => WpfTestHost.Run(async () =>
    {
        using var settings = new TemporaryDirectory();
        Directory.CreateDirectory(settings.Path);
        var window = new MainWindow(settings.Path) { Width = 720, ShowActivated = false };
        var compare = (CompareView)window.FindName("CompareArea");
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(720, window.ActualWidth, 0);
            WpfTestHost.Toggle((ToggleButton)window.FindName("CompareToolToggle"));
            Assert.True(compare.IsVisible);
            var layout = (Button)compare.FindName("LayoutButton");
            WpfTestHost.Invoke(layout);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            WpfTestHost.Invoke(layout.ContextMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "グリッド")));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            var format = new RawFormat { Width = 1920, Height = 1080, BitDepth = 12, Bayer = BayerPattern.Rggb };
            string[] paths = PaneNameDisplayTests.PairedNames.Take(paneCount)
                .Select(name => Path.Combine(settings.Path, name)).ToArray();
            var add = (Button)compare.FindName("AddImageButton");
            foreach (string path in paths)
            {
                await using (FileStream file = File.Create(path))
                {
                    file.SetLength(2L * format.Width * format.Height);
                }
                compare.PanePicker = async token => await ComparePane.LoadAsync(path, format, token);
                int expectedCount = compare.PaneCount + 1;
                WpfTestHost.Invoke(add);
                await WaitUntilAsync(() => compare.PaneCount == expectedCount);
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = (UniformGrid)compare.FindName("PaneGrid");
            Assert.Equal(paneCount, grid.Children.Count);
            Assert.Equal((2, 2), (grid.Rows, grid.Columns));
            var names = grid.Children.Cast<ComparePaneView>()
                .Select(pane => (TextBlock)pane.FindName("FileNameText")).ToArray();
            Assert.All(names, name => Assert.True(name.ActualWidth >= 80));
            if (paneCount == 4)
            {
                foreach (TextBlock name in names)
                {
                    name.Width = 80;
                }
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            var shown = new HashSet<string>();
            for (int i = 0; i < paneCount; i++)
            {
                TextBlock name = names[i];
                Run[] runs = name.Inlines.OfType<Run>().ToArray();
                string rendered = string.Concat(runs.Select(run => run.Text));
                Assert.True(shown.Add(rendered), $"重複した表示名 {rendered}");
                if (paneCount == 4 || i < 2)
                {
                    string difference = i % 2 == 0 ? "a" : "b";
                    Assert.True(rendered.Length > 3);
                    Assert.EndsWith("…", rendered);
                    Assert.Equal(difference, Assert.Single(runs, run =>
                        ReferenceEquals(run.Foreground, name.FindResource("AccentBrush"))).Text);
                }
                Assert.Equal(paths[i], name.ToolTip);
            }
            if (paneCount == 4)
            {
                WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, "compare-4-raw-name-width-80");
                foreach (TextBlock name in names)
                {
                    name.ClearValue(FrameworkElement.WidthProperty);
                }
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, $"compare-{paneCount}-raw-720");
        }
        finally
        {
            await compare.CloseAllAsync();
            window.Close();
        }
    });

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public Task CompareMode_RestoresRightPanelUnlessUserChangesIt(
        bool initiallyVisible, bool changeInCompare, bool expectedVisible) => WpfTestHost.Run(async () =>
    {
        using var settings = new TemporaryDirectory();
        new SessionStore(settings.Path).Update(state => state.RightPanelVisible = initiallyVisible);
        var window = new MainWindow(settings.Path) { Width = 720, ShowActivated = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var compare = (CompareView)window.FindName("CompareArea");
            var right = (Border)window.FindName("RightPanel");
            var show = (Button)window.FindName("RightEdgeStrip");
            var tools = (StackPanel)window.FindName("MainViewTools");
            var zebra = (ToggleButton)window.FindName("ZebraToggle");
            var save = (Button)window.FindName("SaveToolButton");
            var search = (Button)window.FindName("CommandSearchToolButton");
            var toggle = (ToggleButton)window.FindName("CompareToolToggle");
            var toolbar = (ToolBar)window.FindName("ToolBarPanel");
            double initialWidth = right.ActualWidth;
            Assert.Equal(initiallyVisible, right.IsVisible);

            WpfTestHost.Toggle(toggle);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(compare.IsVisible);
            Assert.False(right.IsVisible);
            Assert.True(show.IsVisible);
            Assert.All(new FrameworkElement[] { tools, zebra, save }, item =>
                Assert.Equal(Visibility.Collapsed, item.Visibility));
            Assert.True(toggle.IsVisible);
            Assert.True(search.IsVisible);
            Assert.False(toolbar.HasOverflowItems);

            if (changeInCompare)
            {
                WpfTestHost.Invoke(show);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.True(right.IsVisible);
                if (!expectedVisible)
                {
                    WpfTestHost.Invoke((Button)window.FindName("HideRightPanelButton"));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.False(right.IsVisible);
                }
            }

            WpfTestHost.Invoke((Button)compare.FindName("ExitCompareButton"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(compare.IsVisible);
            Assert.Equal(expectedVisible, right.IsVisible);
            Assert.True(tools.IsVisible);
            Assert.All(new FrameworkElement[] { zebra, save }, item =>
                Assert.Equal(Visibility.Visible, item.Visibility));
            if (initiallyVisible && expectedVisible)
            {
                Assert.Equal(initialWidth, right.ActualWidth);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task CompareLayout_AutomationSelectionSurvivesWindowRestart() => WpfTestHost.Run(async () =>
    {
        using var settings = new TemporaryDirectory();
        var window = new MainWindow(settings.Path) { ShowActivated = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            WpfTestHost.Toggle((ToggleButton)window.FindName("CompareToolToggle"));
            var compare = (CompareView)window.FindName("CompareArea");
            var layout = (Button)compare.FindName("LayoutButton");
            Assert.Equal(CompareLayoutMode.Auto, compare.LayoutMode);
            WpfTestHost.Invoke(layout);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(layout.ContextMenu.IsOpen);
            var vertical = layout.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "縦並び"));
            WpfTestHost.Invoke(vertical);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(layout.ContextMenu.IsOpen);
            Assert.Equal(CompareLayoutMode.Vertical, compare.LayoutMode);
        }
        finally
        {
            window.Close();
        }

        var reopened = new MainWindow(settings.Path);
        try
        {
            var compare = (CompareView)reopened.FindName("CompareArea");
            Assert.Equal(CompareLayoutMode.Vertical, compare.LayoutMode);
            Assert.Equal("配置: 縦並び", ((Button)compare.FindName("LayoutButton")).Content);
            Assert.True(((MainViewModel)reopened.DataContext).RightPanelVisible);
        }
        finally
        {
            reopened.Close();
        }
    });

    [Fact]
    public Task CompareCursor_ReportsSynchronizedCodesAndClearsWhenComparisonEnds() => WpfTestHost.Run(async () =>
    {
        using var settings = new TemporaryDirectory();
        Directory.CreateDirectory(settings.Path);
        var format = new RawFormat { Width = 16, Height = 16, BitDepth = 12 };
        var window = new MainWindow(settings.Path) { Width = 720, ShowActivated = false };
        var compare = (CompareView)window.FindName("CompareArea");
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            WpfTestHost.Toggle((ToggleButton)window.FindName("CompareToolToggle"));
            var add = (Button)compare.FindName("AddImageButton");
            for (int i = 0; i < 2; i++)
            {
                string path = Path.Combine(settings.Path, $"cursor-{i}.raw");
                using (var file = new BinaryWriter(File.Create(path)))
                {
                    ushort value = (ushort)(1432 - 34 * i);
                    for (int pixel = 0; pixel < format.Width * format.Height; pixel++)
                    {
                        file.Write(value);
                    }
                }

                compare.PanePicker = async token => await ComparePane.LoadAsync(path, format, token);
                int expectedCount = i + 1;
                WpfTestHost.Invoke(add);
                await WaitUntilAsync(() => compare.PaneCount == expectedCount);
            }

            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = (UniformGrid)compare.FindName("PaneGrid");
            var second = (ComparePaneView)grid.Children[1];
            ImageViewportTests.PressWithControl(second.ViewportControl, Key.Right);
            var status = (TextBlock)window.FindName("CompareCursorStatus");
            await WaitUntilAsync(() => status.Text.EndsWith("A 1432 · B 1398 · Δ −34"));
            Assert.Matches(@"^x \d+ · y \d+ · A 1432 · B 1398 · Δ −34$", status.Text);
            Assert.True(status.IsVisible);
            WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, "compare-cursor-status-720");

            ImageViewportTests.PressWithControl(second.ViewportControl, Key.Escape);
            Assert.Empty(status.Text);
            Assert.False(status.IsVisible);
            ImageViewportTests.PressWithControl(second.ViewportControl, Key.Right);
            await WaitUntilAsync(() => status.IsVisible);

            WpfTestHost.Invoke((Button)compare.FindName("ExitCompareButton"));
            await WaitUntilAsync(() => !compare.IsVisible);
            Assert.Empty(status.Text);
            Assert.False(status.IsVisible);
        }
        finally
        {
            await compare.CloseAllAsync();
            window.Close();
        }
    });

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready())
        {
            Assert.False(timeout.IsCancellationRequested, "比較画面の更新が完了しませんでした。");
            await Task.Delay(10);
        }
    }

    [Fact]
    public Task ImageOverlays_HideEmptyBoxesAndMoveLevelAbovePlayback() => WpfTestHost.Run(async () =>
    {
        using var settings = new TemporaryDirectory();
        var window = new MainWindow(settings.Path) { ShowActivated = false };
        try
        {
            window.Show();
            var vm = (MainViewModel)window.DataContext;
            vm.HasImage = true;
            vm.HasRoi = true;
            var cursor = (Border)window.FindName("CursorOverlayBox");
            var roi = (Border)window.FindName("RoiOverlayBox");
            var level = (Border)window.FindName("LevelOverlayBox");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.All(new[] { cursor, roi, level }, box => Assert.Equal(Visibility.Collapsed, box.Visibility));

            vm.CursorOverlayText = "x 10 · y 20";
            vm.RoiOverlayText = "ROI 16×16";
            vm.LevelOverlayText = "L1";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.All(new[] { cursor, roi, level }, box => Assert.Equal(Visibility.Visible, box.Visibility));
            Assert.Equal(12, level.Margin.Bottom);
            vm.HasSequence = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(44, level.Margin.Bottom);
            vm.CursorOverlayText = "";
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Collapsed, cursor.Visibility);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task FrameStepButtons_HaveDistinctSymbolsAndShortcutTooltips() => WpfTestHost.Run(() =>
    {
        using var settings = new TemporaryDirectory();
        var window = new MainWindow(settings.Path);
        try
        {
            var previous = (Button)window.FindName("PreviousFrameButton");
            var next = (Button)window.FindName("NextFrameButton");
            var play = (ToggleButton)window.FindName("PlayToggle");
            Assert.Equal(IconGlyphs.StepBack, previous.Content);
            Assert.Equal(IconGlyphs.StepForward, next.Content);
            Assert.Equal(IconGlyphs.Play, play.Tag);
            Assert.Equal("前のフレームへ", AutomationProperties.GetName(previous));
            Assert.Equal("次のフレームへ", AutomationProperties.GetName(next));
            Assert.Contains("Page Up", Assert.IsType<string>(previous.ToolTip));
            Assert.Contains("Page Down", Assert.IsType<string>(next.ToolTip));
        }
        finally
        {
            window.Close();
        }
    });

    private static Rect BoundsIn(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

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
