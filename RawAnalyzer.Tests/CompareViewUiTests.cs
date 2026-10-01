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

    [Theory]
    [InlineData(0)] // 視野(相対)
    [InlineData(1)] // 等倍(1:1)
    public Task AddPane_AfterZoomingExisting_NewPaneFollowsOnceLaidOut(int syncIndex) => WpfTestHost.Run(async () =>
    {
        // 解像度の違う2枚(Bは縦横2倍)。追加前はAが1列、追加後は2列でAのサイズも変わる
        using var small = new ImageFixture(480, 320);
        using var large = new ImageFixture(960, 640);
        var view = NewView();
        try
        {
            Assert.True(await view.AddPaneFromPathAsync(small.Path));
            await LayoutAsync(view, 1280, 720);
            ((ComboBox)view.FindName("SyncCombo")).SelectedIndex = syncIndex;
            ComparePaneView first = PaneAt(view, 0);
            ZoomAround(first, 4, 0.6, 0.4);
            double widthBefore = first.ViewportControl.ActualWidth;
            (double X, double Y) centerBefore = CenterOf(first);

            Assert.True(await view.AddPaneFromPathAsync(large.Path));
            await LayoutAsync(view, 1280, 720);

            ComparePaneView second = PaneAt(view, 1);
            Assert.True(second.ViewportControl.ActualWidth > 0 && second.ViewportControl.ActualHeight > 0);
            Assert.True(first.ViewportControl.ActualWidth < widthBefore);

            // 既存ペインは新ペインに引きずられない(サイズ変化でも倍率と中心を保つ)
            Assert.Equal(4, first.ViewportControl.Zoom, 10);
            Assert.Equal(centerBefore.X, CenterOf(first).X, 6);
            Assert.Equal(centerBefore.Y, CenterOf(first).Y, 6);

            // 新ペインはレイアウト確定後のサイズで既存ペインに揃う(全体表示のままにならない)
            Assert.Equal(RelativeCenterOf(first).X, RelativeCenterOf(second).X, 6);
            Assert.Equal(RelativeCenterOf(first).Y, RelativeCenterOf(second).Y, 6);
            if (syncIndex == 0)
            {
                Assert.Equal(RelativeWidthOf(first), RelativeWidthOf(second), 6);
            }
            else
            {
                Assert.Equal(first.ViewportControl.Zoom, second.ViewportControl.Zoom, 10);
            }
        }
        finally
        {
            await view.CloseAllAsync();
        }
    });

    [Theory]
    [InlineData(1, 0, false)] // 等倍→視野(等倍の基準はA、アクティブはAに揃えたB)
    [InlineData(0, 2, false)] // 視野→オフ
    [InlineData(2, 0, true)] // オフでAだけ拡大→視野
    public Task SwitchSyncMode_BaseUntouched_AllPanesKeepFittingAsGridChanges(
        int fromIndex, int toIndex, bool zoomOther) => WpfTestHost.Run(async () =>
    {
        // 視野・オフへの切替で基準が未操作なら、切替後も全ペインが全体表示に追従する。
        // 基準だけ追従が残ると、次のペイン増減で基準だけ再フィットし、基準の変換を
        // 写した他ペインとずれる(等倍への切替は SwitchToPixelZoom_… で確かめる)
        using var small = new ImageFixture(480, 320);
        using var large = new ImageFixture(960, 640);
        var view = NewView();
        try
        {
            SelectSync(view, fromIndex);
            Assert.True(await view.AddPaneFromPathAsync(small.Path));
            await LayoutAsync(view, 1280, 720);
            Assert.True(await view.AddPaneFromPathAsync(large.Path));
            await LayoutAsync(view, 1280, 720);
            if (zoomOther)
            {
                // ホイールでの拡大はアクティブを変えないので、基準のBは未操作のまま
                ZoomAround(PaneAt(view, 0), 4, 0.6, 0.4);
            }

            SelectSync(view, toIndex);
            AssertAllFitting(view);

            Assert.True(await view.AddPaneFromPathAsync(small.Path));
            await LayoutAsync(view, 1280, 720);
            AssertAllFitting(view);

            await ClosePaneAsync(view, 0);
            await LayoutAsync(view, 1280, 720);
            AssertAllFitting(view);
        }
        finally
        {
            await view.CloseAllAsync();
        }
    });

    [Theory]
    [InlineData(0, 1)] // 視野→等倍
    [InlineData(1, 0)] // 等倍→視野
    [InlineData(0, 2)] // 視野→オフ
    [InlineData(2, 0)] // オフでBだけ拡大→視野
    [InlineData(2, 1)] // オフでBだけ拡大→等倍
    public Task SwitchSyncMode_AfterZooming_AlignsToBaseAndKeepsViewsAsGridChanges(
        int fromIndex, int toIndex) => WpfTestHost.Run(async () =>
    {
        // 基準が操作済みなら、切替で他ペインが基準の視野へ揃い(基準自身は動かない)、
        // 以後のペイン増減でも既存ペインは倍率と中心を保つ。オフへの切替では何も動かない
        using var small = new ImageFixture(480, 320);
        using var large = new ImageFixture(960, 640);
        var view = NewView();
        try
        {
            SelectSync(view, fromIndex);
            Assert.True(await view.AddPaneFromPathAsync(small.Path));
            await LayoutAsync(view, 1280, 720);
            if (fromIndex != 2)
            {
                ZoomAround(PaneAt(view, 0), 4, 0.6, 0.4); // 同期中なので追加するBもこれに揃う
            }

            Assert.True(await view.AddPaneFromPathAsync(large.Path)); // 追加したBがアクティブ=基準
            await LayoutAsync(view, 1280, 720);
            if (fromIndex == 2)
            {
                ZoomAround(PaneAt(view, 1), 4, 0.6, 0.4); // オフなのでAは全体表示に追従したまま
            }

            ComparePaneView first = PaneAt(view, 0);
            ComparePaneView second = PaneAt(view, 1);
            (double Zoom, double X, double Y) firstView = ViewOf(first);
            (double Zoom, double X, double Y) secondView = ViewOf(second);

            SelectSync(view, toIndex);
            AssertKeepsView(secondView, second);
            if (toIndex == 2)
            {
                AssertKeepsView(firstView, first);
            }
            else
            {
                AssertSynced(toIndex, second, first);
            }

            firstView = ViewOf(first);
            Assert.True(await view.AddPaneFromPathAsync(small.Path));
            await LayoutAsync(view, 1280, 720);
            ComparePaneView third = PaneAt(view, 2);
            AssertKeepsView(firstView, first);
            AssertKeepsView(secondView, second);
            if (toIndex == 2)
            {
                AssertFitting(third); // オフでは新ペインは全体表示に追従する
            }
            else
            {
                AssertSynced(toIndex, second, third);
            }

            (double Zoom, double X, double Y) thirdView = ViewOf(third);
            await ClosePaneAsync(view, 0);
            await LayoutAsync(view, 1280, 720);
            AssertKeepsView(secondView, second);
            if (toIndex == 2)
            {
                AssertFitting(third);
            }
            else
            {
                AssertKeepsView(thirdView, third);
                AssertSynced(toIndex, second, third);
            }
        }
        finally
        {
            await view.CloseAllAsync();
        }
    });

    [Theory]
    [InlineData(0, false)] // 視野→等倍
    public Task SwitchToPixelZoom_BaseUntouched_ZoomsStayEqualAsGridAndSizeChange(
        int fromIndex, bool zoomOther) => WpfTestHost.Run(async () =>
    {
        // 等倍は同期中の全ペインの倍率を常に等しく保つ。切替の基準(アクティブ=最後に
        // 追加したB)が未操作なら、Bは全体表示に追従したまま倍率を他ペインへ写し、
        // ペインの増減やリサイズでBが再フィットしたらレイアウト確定後に合わせ直す
        using var small = new ImageFixture(480, 320);
        using var large = new ImageFixture(960, 640);
        var view = NewView();
        try
        {
            SelectSync(view, fromIndex);
            Assert.True(await view.AddPaneFromPathAsync(small.Path));
            await LayoutAsync(view, 1280, 720);
            Assert.True(await view.AddPaneFromPathAsync(large.Path));
            await LayoutAsync(view, 1280, 720);
            ComparePaneView fitBase = PaneAt(view, 1);
            if (zoomOther)
            {
                ZoomAround(PaneAt(view, 0), 4, 0.6, 0.4); // ホイールでの拡大はアクティブを変えない
            }

            SelectSync(view, 1);
            AssertPixelSyncedToFittingBase(view, fitBase);

            Assert.True(await view.AddPaneFromPathAsync(small.Path));
            await LayoutAsync(view, 1280, 720);
            AssertPixelSyncedToFittingBase(view, fitBase);

            await ClosePaneAsync(view, 0);
            await LayoutAsync(view, 1280, 720);
            AssertPixelSyncedToFittingBase(view, fitBase);

            await LayoutAsync(view, 1000, 600); // ビュー自体のリサイズ(ウィンドウ・全画面の切替)
            AssertPixelSyncedToFittingBase(view, fitBase);

            // オフへの切替では何も動かず、以後は合わせ直さない(Bだけが全体表示に追従する)
            ComparePaneView other = PaneAt(view, 1);
            (double Zoom, double X, double Y) otherView = ViewOf(other);
            SelectSync(view, 2);
            AssertKeepsView(otherView, other);
            AssertFitting(fitBase);
            Assert.True(await view.AddPaneFromPathAsync(small.Path));
            await LayoutAsync(view, 1000, 600);
            AssertKeepsView(otherView, other);
            AssertFitting(fitBase);
            AssertFitting(PaneAt(view, 2));
        }
        finally
        {
            await view.CloseAllAsync();
        }
    });

    [Fact]
    public Task PixelZoom_ClosingUntouchedBase_ActivePaneTakesOverAsBase() => WpfTestHost.Run(async () =>
    {
        // 未操作の基準を閉じても未操作の扱いは続く。アクティブのペインが基準を引き継いで
        // 全体表示に追従し、他ペインはその倍率へ揃い直す(閉じた基準の倍率のまま固まらない)
        using var small = new ImageFixture(480, 320);
        using var large = new ImageFixture(960, 640);
        using var middle = new ImageFixture(720, 480);
        var view = NewView();
        try
        {
            Assert.True(await view.AddPaneFromPathAsync(small.Path));
            await LayoutAsync(view, 1280, 720);
            Assert.True(await view.AddPaneFromPathAsync(large.Path));
            await LayoutAsync(view, 1280, 720);
            SelectSync(view, 1); // 基準はアクティブのB
            Assert.True(await view.AddPaneFromPathAsync(middle.Path)); // アクティブはCへ移る
            await LayoutAsync(view, 1280, 720);
            AssertPixelSyncedToFittingBase(view, PaneAt(view, 1));

            await ClosePaneAsync(view, 1);
            await LayoutAsync(view, 1280, 720);
            AssertPixelSyncedToFittingBase(view, PaneAt(view, 1));
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

    private static ComparePaneView PaneAt(CompareView view, int index) =>
        (ComparePaneView)((UniformGrid)view.FindName("PaneGrid")).Children[index];

    // ユーザーのズーム操作に相当(ApplyViewも操作と同じく全体表示への追従を止める)。
    // 表示中心を画像の相対位置(relX, relY)に置く
    private static void ZoomAround(ComparePaneView pane, double zoom, double relX, double relY)
    {
        var viewport = pane.ViewportControl;
        pane.ApplyView(zoom,
            relX * pane.Pane!.Image.Width - viewport.ActualWidth / (2 * zoom),
            relY * pane.Pane.Image.Height - viewport.ActualHeight / (2 * zoom));
    }

    private static (double X, double Y) CenterOf(ComparePaneView pane)
    {
        var viewport = pane.ViewportControl;
        return (viewport.OriginX + viewport.ActualWidth / (2 * viewport.Zoom),
            viewport.OriginY + viewport.ActualHeight / (2 * viewport.Zoom));
    }

    private static (double X, double Y) RelativeCenterOf(ComparePaneView pane)
    {
        (double x, double y) = CenterOf(pane);
        return (x / pane.Pane!.Image.Width, y / pane.Pane.Image.Height);
    }

    private static double RelativeWidthOf(ComparePaneView pane) =>
        pane.ViewportControl.ActualWidth / pane.ViewportControl.Zoom / pane.Pane!.Image.Width;

    private static double FitZoomOf(ComparePaneView pane) => Math.Min(
        pane.ViewportControl.ActualWidth / pane.Pane!.Image.Width,
        pane.ViewportControl.ActualHeight / pane.Pane.Image.Height);

    // 同期モードの選択(0=視野、1=等倍、2=オフ)。ユーザーの選択と同じくSelectionChangedが走る
    private static void SelectSync(CompareView view, int index) =>
        ((ComboBox)view.FindName("SyncCombo")).SelectedIndex = index;

    private static async Task ClosePaneAsync(CompareView view, int index)
    {
        Button close = Descendants<Button>(PaneAt(view, index)).Single(button =>
            AutomationProperties.GetName(button) == "ペインを閉じる");
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await DrainAsync();
    }

    private static (double Zoom, double X, double Y) ViewOf(ComparePaneView pane)
    {
        (double x, double y) = CenterOf(pane);
        return (pane.ViewportControl.Zoom, x, y);
    }

    // 倍率と表示中心が変わっていない(サイズ変化では中心を保って原点だけ動く)
    private static void AssertKeepsView((double Zoom, double X, double Y) expected, ComparePaneView pane)
    {
        Assert.Equal(expected.Zoom, pane.ViewportControl.Zoom, 10);
        Assert.Equal(expected.X, CenterOf(pane).X, 6);
        Assert.Equal(expected.Y, CenterOf(pane).Y, 6);
    }

    // 現在のサイズでの全体表示になっている
    private static void AssertFitting(ComparePaneView pane)
    {
        Assert.Equal(FitZoomOf(pane), pane.ViewportControl.Zoom, 10);
        Assert.Equal(0.5, RelativeCenterOf(pane).X, 6);
        Assert.Equal(0.5, RelativeCenterOf(pane).Y, 6);
    }

    private static void AssertAllFitting(CompareView view)
    {
        for (int i = 0; i < view.PaneCount; i++) AssertFitting(PaneAt(view, i));
    }

    // 同期モードどおりに揃っている(相対中心が一致し、視野なら相対幅、等倍なら倍率が一致)
    private static void AssertSynced(int syncIndex, ComparePaneView expected, ComparePaneView actual)
    {
        Assert.Equal(RelativeCenterOf(expected).X, RelativeCenterOf(actual).X, 6);
        Assert.Equal(RelativeCenterOf(expected).Y, RelativeCenterOf(actual).Y, 6);
        if (syncIndex == 0)
        {
            Assert.Equal(RelativeWidthOf(expected), RelativeWidthOf(actual), 6);
        }
        else
        {
            Assert.Equal(expected.ViewportControl.Zoom, actual.ViewportControl.Zoom, 10);
        }
    }

    // 等倍で未操作の基準に揃っている(基準は全体表示で、全ペインが基準と同じ倍率・相対中心)
    private static void AssertPixelSyncedToFittingBase(CompareView view, ComparePaneView fitBase)
    {
        AssertFitting(fitBase);
        for (int i = 0; i < view.PaneCount; i++) AssertSynced(1, fitBase, PaneAt(view, i));
    }

    // 自動の全体表示(FitToView)を挟まずにレイアウトだけ確定させる
    private static async Task LayoutAsync(CompareView view, int width, int height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        await DrainAsync();
    }

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

        internal ImageFixture(int width = 480, int height = 320)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
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
