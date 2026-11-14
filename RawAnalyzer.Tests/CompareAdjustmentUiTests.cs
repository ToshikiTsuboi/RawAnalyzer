using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using RawAnalyzer.App;
using RawAnalyzer.App.Compare;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class CompareAdjustmentUiTests
{
    [Fact]
    public Task EmptyComparison_DisablesDisplayAdjustmentAndShowsReason() => WpfTestHost.Run(async () =>
    {
        using var fixture = new TestWindow();
        MainWindow window = fixture.Window;
        await fixture.EnterCompareAsync();
        var display = Find<Expander>(window, "DisplaySection");
        var banner = Find<Border>(window, "AdjustPanelReasonBanner");
        Assert.False(display.IsEnabled);
        Assert.True(banner.IsVisible);
        Assert.Equal(CommandDisabledReasons.NoActiveComparePane, Assert.IsType<TextBlock>(banner.Child).Text);
        Assert.False(Command(window, "auto-contrast").IsEnabled());
        Assert.False(Command(window, "reset-display").IsEnabled());

        ComparePaneView pane = await fixture.AddPaneAsync(10);
        Assert.True(display.IsEnabled);
        Assert.False(banner.IsVisible);
        WpfTestHost.Invoke(Find<Button>(pane, "CloseButton"));
        await DrainAsync();
        Assert.False(display.IsEnabled);
        Assert.True(banner.IsVisible);
    });

    [Fact]
    public Task PanelSwitchesBitDepthAndEditsLinkedPanesWithoutUpdatingNormalView() => WpfTestHost.Run(async () =>
    {
        using var fixture = new TestWindow(openRecent: true);
        MainWindow window = fixture.Window;
        var vm = (MainViewModel)window.DataContext;
        var viewport = Find<ImageViewport>(window, "Viewport");
        window.Show();
        await DrainAsync();
        var recent = Find<ItemsControl>(window, "EmptyCanvasRecent");
        var item = Assert.IsType<ContentPresenter>(recent.ItemContainerGenerator.ContainerFromIndex(0));
        WpfTestHost.Invoke(Assert.IsType<Button>(System.Windows.Media.VisualTreeHelper.GetChild(item, 0)));
        await WaitUntilAsync(() => viewport.Image is not null);
        vm.GainDb = 3;
        vm.Gamma = 1.4;
        vm.Contrast = 1.25;
        vm.BlackLevel = 18;
        vm.WhiteLevel = 900;
        vm.HdrTargetVisible = true;
        var normal = (vm.Gain, vm.Gamma, vm.Contrast, vm.BlackLevel, vm.WhiteLevel, vm.BlackLevelMax);
        FieldInfo lutField = typeof(ImageViewport).GetField("_lut", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object normalLut = lutField.GetValue(viewport)!;

        await fixture.EnterCompareAsync();
        await WaitUntilAsync(() => fixture.Compare.PaneCount == 1);
        ComparePaneView first = fixture.Compare.ActivePane!;
        ComparePaneView second = await fixture.AddPaneAsync(12);
        var gain = Find<NumericSliderRow>(window, "DisplayGainRow");
        var gamma = Find<NumericSliderRow>(window, "DisplayGammaRow");
        var contrast = Find<NumericSliderRow>(window, "DisplayContrastRow");
        var black = Find<NumericSliderRow>(window, "DisplayBlackRow");
        var white = Find<NumericSliderRow>(window, "DisplayWhiteRow");
        var display = Find<Expander>(window, "DisplaySection");
        foreach (string section in new[] { "HistogramSection", "WhiteBalanceSection", "ColorMatrixSection",
                     "FormatSection", "HdrAdjustTargetRow", "AdjustPanelReasonBanner" })
        {
            Assert.False(Find<FrameworkElement>(window, section).IsVisible);
        }
        foreach (string id in new[] { "hist-log", "hist-cumulative", "hist-channel" })
        {
            AppCommand command = Command(window, id);
            Assert.False(command.IsEnabled());
            Assert.Equal(CommandDisabledReasons.CompareMode, command.DisabledReason!());
        }

        // パネルを開く際のレイアウトによる描画が済んでから、編集による描画要求を監視する。
        await DrainAsync();
        int normalRenderRequests = 0;
        viewport.NavigatorStateChanged += (_, _) => normalRenderRequests++;
        SetSlider(gain, 6);
        SetSlider(gamma, 1.25);
        SetSlider(contrast, 1.35);
        SetSlider(black, 500);
        SetSlider(white, 2800);
        DisplaySettings b = second.Pane!.Display;
        Assert.Equal(new DisplaySettings { GainDb = 6, Gamma = 1.25, Contrast = 1.35,
            BlackCode = 500, WhiteCode = 2800 }, b);
        DisplaySettings a = first.Pane!.Display;
        Assert.Equal(DisplaySettings.CreateDefault(10), a);
        Assert.Equal(4095, white.Maximum);
        Assert.Equal(4095, white.DefaultValue);
        Assert.Equal(-20, gain.Minimum);
        Assert.Equal(60, gain.Maximum);
        Assert.Equal($"= ×{b.GainLinear:0.###}", gain.Note);
        Assert.Equal("表示調整 · 調整対象: B", display.Header);
        await DrainAsync();
        WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, "compare-adjustment-before-switch-b");

        int paneChanges = 0;
        first.DisplayChanged += _ => paneChanges++;
        second.DisplayChanged += _ => paneChanges++;
        Activate(first);
        await DrainAsync();
        Assert.Equal("表示調整 · 調整対象: A", display.Header);
        Assert.Equal(1023, white.Maximum);
        Assert.Equal(1023, white.Value);
        Assert.Equal(1023, white.DefaultValue);
        Assert.Equal(a.GainDb, gain.Value);
        WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, "compare-adjustment-after-switch-a");
        Activate(second);
        await DrainAsync();
        Assert.Equal(4095, white.Maximum);
        Assert.Equal(2800, white.Value);
        Assert.Equal(a, first.Pane.Display);
        Assert.Equal(b, second.Pane.Display);
        Assert.Equal(0, paneChanges);

        WpfTestHost.Toggle(Find<ToggleButton>(second, "LinkToggle"));
        WpfTestHost.Toggle(Find<ToggleButton>(first, "LinkToggle"));
        await DrainAsync();
        SetSlider(black, 256);
        SetSlider(white, 768);
        await DrainAsync();
        Assert.Equal(1025, second.Pane.Display.BlackCode);
        Assert.Equal(3074, second.Pane.Display.WhiteCode);
        Assert.Equal(0, normalRenderRequests);
        Assert.Same(normalLut, lutField.GetValue(viewport));
        Assert.Equal(normal, (vm.Gain, vm.Gamma, vm.Contrast, vm.BlackLevel, vm.WhiteLevel, vm.BlackLevelMax));

        WpfTestHost.Invoke(Find<Button>(fixture.Compare, "ExitCompareButton"));
        await WaitUntilAsync(() => !vm.IsCompareMode);
        Assert.Equal(normal, (vm.Gain, vm.Gamma, vm.Contrast, vm.BlackLevel, vm.WhiteLevel, vm.BlackLevelMax));
        Assert.Same(normalLut, lutField.GetValue(viewport));
        Assert.Equal(normal.BlackLevel, black.Value);
        Assert.Equal(normal.WhiteLevel, white.Value);
        Assert.True(Find<FrameworkElement>(window, "HdrAdjustTargetRow").IsVisible);
    });

    [Fact]
    public Task AutoAndResetShortcutsAndPanelButtonsTargetOnlyTheActivePane() => WpfTestHost.Run(async () =>
    {
        using var fixture = new TestWindow();
        await fixture.EnterCompareAsync();
        ComparePaneView first = await fixture.AddPaneAsync(10);
        ComparePaneView second = await fixture.AddPaneAsync(12);
        var window = fixture.Window;
        DisplaySettings a = first.Pane!.Display;
        DisplaySettings b = second.Pane!.Display;
        var white = Find<NumericSliderRow>(window, "DisplayWhiteRow");
        ImageViewportTests.PressWithControl(window, Key.A);
        await WaitUntilAsync(() => second.Pane.Display != b);
        Assert.Equal(a, first.Pane.Display);
        Assert.Equal(second.Pane.Display.WhiteCode, white.Value);
        ImageViewportTests.PressWithControl(window, Key.R, shift: true);
        Assert.Equal(b, second.Pane.Display);
        Assert.Equal(a, first.Pane.Display);

        Activate(first);
        WpfTestHost.Invoke(Find<Button>(window, "DisplayAutoButton"));
        await WaitUntilAsync(() => first.Pane.Display != a);
        Assert.Equal(b, second.Pane.Display);
        WpfTestHost.Invoke(Find<Button>(window, "DisplayResetButton"));
        await DrainAsync();
        Assert.Equal(a, first.Pane.Display);
        Assert.Equal(b, second.Pane.Display);
    });

    [Fact]
    public Task AutoContrast_AppliesToOriginalPaneAfterSwitchWithoutChangingPanel() => WpfTestHost.Run(async () =>
    {
        using var fixture = new TestWindow();
        await fixture.EnterCompareAsync();
        ComparePaneView first = await fixture.AddPaneAsync(10);
        ComparePaneView second = await fixture.AddPaneAsync(12);
        DisplaySettings a = first.Pane!.Display;
        DisplaySettings b = second.Pane!.Display;
        var result = new TaskCompletionSource<AutoLevels?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken? runningToken = null;
        second.CalculateAutoLevelsAsync = (_, token) =>
        {
            runningToken = token;
            return result.Task;
        };

        WpfTestHost.Invoke(Find<Button>(fixture.Window, "DisplayAutoButton"));
        await DrainAsync();
        Assert.NotNull(runningToken);
        try
        {
            Activate(first);
            Assert.Same(first, fixture.Compare.ActivePane);
            Assert.False(runningToken.Value.IsCancellationRequested);
        }
        finally
        {
            result.SetResult(new AutoLevels(100, 700));
        }

        await WaitUntilAsync(() => second.Pane.Display == b with { BlackCode = 100, WhiteCode = 700 });
        Assert.Equal(a, first.Pane.Display);
        Assert.Equal("表示調整 · 調整対象: A", Find<Expander>(fixture.Window, "DisplaySection").Header);
        Assert.Equal(a.BlackCode, Find<NumericSliderRow>(fixture.Window, "DisplayBlackRow").Value);
        var white = Find<NumericSliderRow>(fixture.Window, "DisplayWhiteRow");
        Assert.Equal(a.WhiteCode, white.Value);
        Assert.Equal(1023, white.Maximum);
    });

    [Fact]
    public Task AutoContrast_HeaderButtonsApplyBothPendingResults() => WpfTestHost.Run(async () =>
    {
        using var fixture = new TestWindow();
        await fixture.EnterCompareAsync();
        ComparePaneView first = await fixture.AddPaneAsync(10);
        ComparePaneView second = await fixture.AddPaneAsync(12);
        DisplaySettings a = first.Pane!.Display;
        DisplaySettings b = second.Pane!.Display;
        var firstResult = new TaskCompletionSource<AutoLevels?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResult = new TaskCompletionSource<AutoLevels?>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.CalculateAutoLevelsAsync = (_, _) => firstResult.Task;
        second.CalculateAutoLevelsAsync = (_, _) => secondResult.Task;

        try
        {
            WpfTestHost.Invoke(Find<Button>(first, "AutoButton"));
            await DrainAsync();
            Assert.Same(first, fixture.Compare.ActivePane);
            WpfTestHost.Invoke(Find<Button>(second, "AutoButton"));
            await DrainAsync();
            Assert.Same(second, fixture.Compare.ActivePane);
            secondResult.SetResult(new AutoLevels(100, 700));
            await WaitUntilAsync(() => second.Pane.Display == b with { BlackCode = 100, WhiteCode = 700 });
        }
        finally
        {
            firstResult.TrySetResult(new AutoLevels(50, 300));
            secondResult.TrySetResult(new AutoLevels(100, 700));
        }

        await WaitUntilAsync(() => first.Pane.Display == a with { BlackCode = 50, WhiteCode = 300 });
        Assert.Equal(b with { BlackCode = 100, WhiteCode = 700 }, second.Pane.Display);
    });

    [Fact]
    public Task AutoContrast_DiscardsPendingResultAfterTargetCloses() => WpfTestHost.Run(async () =>
    {
        using var fixture = new TestWindow();
        await fixture.EnterCompareAsync();
        ComparePaneView first = await fixture.AddPaneAsync(10);
        ComparePaneView second = await fixture.AddPaneAsync(12);
        ComparePane target = second.Pane!;
        DisplaySettings a = first.Pane!.Display;
        DisplaySettings b = target.Display;
        var result = new TaskCompletionSource<AutoLevels?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken? runningToken = null;
        second.CalculateAutoLevelsAsync = (_, token) =>
        {
            runningToken = token;
            return result.Task;
        };
        int changes = 0;
        second.DisplayChanged += _ => changes++;
        WpfTestHost.Invoke(Find<Button>(fixture.Window, "DisplayAutoButton"));
        await DrainAsync();
        Assert.NotNull(runningToken);
        try
        {
            WpfTestHost.Invoke(Find<Button>(second, "CloseButton"));
            await DrainAsync();
            Assert.Same(first, fixture.Compare.ActivePane);
            Assert.True(runningToken.Value.IsCancellationRequested);
        }
        finally
        {
            result.SetResult(new AutoLevels(100, 700));
            await DrainAsync();
        }
        Assert.Equal(b, target.Display);
        Assert.Equal(a, first.Pane.Display);
        Assert.Equal(0, changes);
    });

    private static T Find<T>(FrameworkElement parent, string name) => Assert.IsAssignableFrom<T>(parent.FindName(name));

    private static AppCommand Command(MainWindow window, string id) =>
        ((IReadOnlyList<AppCommand>)typeof(MainWindow).GetProperty("Commands", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!).Single(command => command.Id == id);

    private static void Activate(ComparePaneView pane) => CompareViewUiTests.Click(pane, MouseButton.Right);

    private static void SetSlider(NumericSliderRow row, double value)
    {
        var peer = new SliderAutomationPeer(Find<Slider>(row, "ValueSlider"));
        ((IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue)!).SetValue(value);
    }

    private static Task DrainAsync() =>
        Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready())
        {
            Assert.False(timeout.IsCancellationRequested, "比較画面の更新が完了しませんでした。");
            await Task.Delay(10);
        }
    }

    private sealed class TestWindow : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        internal MainWindow Window { get; }
        internal CompareView Compare { get; }

        internal TestWindow(bool openRecent = false)
        {
            Directory.CreateDirectory(_directory);
            if (openRecent)
            {
                var format = new RawFormat { Width = 64, Height = 48, BitDepth = 10 };
                string path = CreateRaw("a.raw", format);
                new RecentFilesStore(_directory).Add(path);
                new SessionStore(_directory).Update(state =>
                {
                    SessionStore.TouchFileFormat(state, SessionStore.NormalizeKey(path), format);
                    state.LeftPanelVisible = false;
                });
            }
            Window = new MainWindow(_directory) { ShowActivated = false, Width = 1280 };
            Compare = Find<CompareView>(Window, "CompareArea");
        }

        private string CreateRaw(string fileName, RawFormat format)
        {
            string path = Path.Combine(_directory, fileName);
            using var writer = new BinaryWriter(File.Create(path));
            int maxCode = (1 << format.BitDepth) - 1;
            for (int y = 0; y < format.Height; y++)
            {
                for (int x = 0; x < format.Width; x++)
                {
                    writer.Write((ushort)(maxCode / 8 + ((x ^ y) % 64) * maxCode / 96));
                }
            }
            return path;
        }

        internal async Task EnterCompareAsync()
        {
            if (!Window.IsVisible)
            {
                Window.Show();
            }
            await DrainAsync();
            WpfTestHost.Toggle(Find<ToggleButton>(Window, "CompareToolToggle"));
            await DrainAsync();
            WpfTestHost.Invoke(Find<Button>(Window, "RightEdgeStrip"));
            await DrainAsync();
        }

        internal async Task<ComparePaneView> AddPaneAsync(int bitDepth)
        {
            var format = new RawFormat { Width = 64, Height = 48, BitDepth = bitDepth };
            int count = Compare.PaneCount + 1;
            string path = CreateRaw($"pane-{count}.raw", format);
            Compare.PanePicker = async token => await ComparePane.LoadAsync(path, format, token);
            WpfTestHost.Invoke(Find<Button>(Compare, "AddImageButton"));
            await WaitUntilAsync(() => Compare.PaneCount == count);
            await DrainAsync();
            return Compare.ActivePane!;
        }

        public void Dispose()
        {
            Window.Close();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
