using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RawAnalyzer.App;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
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
            Assert.Equal(6, pinned.Children.OfType<Button>().Count());
            Assert.IsType<ComboBox>(window.FindName("DisplayModeCombo"));
            Assert.False(ToolBar.GetIsOverflowItem(pinned));
            Assert.True(new Rect(toolbar.RenderSize).Contains(BoundsIn(pinned, toolbar)));

            var items = toolbar.Items.Cast<FrameworkElement>().Skip(1).ToArray();
            Assert.Equal(new[] { "ZebraToggle", "ProfileToggle", "RoiToggle", "HorizontalProjectionToggle",
                "VerticalProjectionToggle", "SaveToolButton" }, items.Select(item => item.Name));
            int firstOverflow = Array.FindIndex(items, ToolBar.GetIsOverflowItem);
            if (width == 720) Assert.True(firstOverflow >= 0);
            if (width == 1280) Assert.Equal(-1, firstOverflow);
            Assert.Equal(firstOverflow >= 0, toolbar.HasOverflowItems);
            Assert.Equal(firstOverflow >= 0, button.IsVisible);
            if (firstOverflow >= 0)
            {
                button.IsChecked = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.True(toolbar.IsOverflowOpen);
                Assert.True(overflow.IsVisible);
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
                var roi = (ToggleButton)window.FindName("RoiToggle");
                Assert.True(ToolBar.GetIsOverflowItem(roi));
                var peer = new ToggleButtonAutomationPeer(roi);
                var toggle = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!;
                toggle.Toggle();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.True(roi.IsChecked);
                Assert.Contains("✓", TextsIn(roi));
                WpfTestHost.CaptureIfRequested((FrameworkElement)overflow.Parent, "main-toolbar-overflow-720");
                toggle.Toggle();

                vm.IsCompareMode = true;
                foreach (FrameworkElement item in items)
                {
                    Assert.False(item.IsEnabled);
                    Assert.Equal(CommandDisabledReasons.CompareMode, item.ToolTip);
                    Assert.True(ToolTipService.GetShowOnDisabled(item));
                }
                toolbar.IsOverflowOpen = false;
                vm.IsCompareMode = false;
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
            Assert.DoesNotContain(previous.Content.ToString()!, play.Content.ToString()!);
            Assert.DoesNotContain(next.Content.ToString()!, play.Content.ToString()!);
            Assert.NotEqual(previous.Content, next.Content);
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

    private static IEnumerable<string> TextsIn(DependencyObject element)
    {
        if (element is TextBlock text)
        {
            yield return text.Text;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            foreach (string childText in TextsIn(VisualTreeHelper.GetChild(element, i)))
            {
                yield return childText;
            }
        }
    }

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
