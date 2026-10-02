using System.Windows.Controls;
using System.Windows.Threading;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;
using static RawAnalyzer.Tests.ProfileWindowParts;

namespace RawAnalyzer.Tests;

/// <summary>
/// 射影の窓の開閉と計算し直し(MainWindow を作らず、表示の状態を差し替えて確かめる)。
/// </summary>
[Collection("WPF UI")]
public class ProjectionWindowControllerTests
{
    private const ProjectionDirection H = ProjectionDirection.Horizontal;
    private const ProjectionDirection V = ProjectionDirection.Vertical;

    [Fact]
    public Task Open_ComputesWholeImageAtOnce() => WpfTestHost.Run(async () =>
    {
        // 押すとすぐ計算して窓を開く(画像のクリックは要らない)。ROI がなければ画像全体
        using var view = new FakeView();
        ProjectionWindowController controller = view.Controller();
        try
        {
            controller.Open(H);
            await controller.WhenIdleAsync();

            ProjectionWindow window = controller.WindowFor(H)!;
            Assert.Equal("対象: 画像全体 (4×2) · フレーム 1/2 · 各列 2 画素の平均", Find<TextBlock>(window, "TargetText").Text);
            Assert.Equal(Table("x", (0, 2, 0, 4), (1, 3, 1, 5), (2, 4, 2, 6), (3, 5, 3, 7)), window.BuildTable(','));
            Assert.Null(controller.WindowFor(V));
            Assert.False(controller.IsComputing);
        }
        finally
        {
            controller.Shutdown();
        }
    });

    [Fact]
    public Task RoiChangeAndClear_Recompute() => WpfTestHost.Run(async () =>
    {
        using var view = new FakeView();
        ProjectionWindowController controller = view.Controller();
        try
        {
            controller.Open(V);
            await controller.WhenIdleAsync();
            ProjectionWindow window = controller.WindowFor(V)!;
            Assert.StartsWith("y,mean,min,max" + Environment.NewLine + "0,1.5,0,3", window.BuildTable(','));

            // ROI(1,0,2,2): 行 0 は (1+2)/2、行 1 は (5+6)/2。座標は ROI の元画像の座標
            view.Roi = new RegionOfInterest(1, 1, 2, 1);
            controller.Refresh();
            await controller.WhenIdleAsync();
            Assert.Equal(Table("y", (1, 5.5, 5, 6)), window.BuildTable(','));
            Assert.StartsWith("対象: ROI (1, 1, 2×1)", Find<TextBlock>(window, "TargetText").Text);

            view.Roi = null;
            controller.Refresh();
            await controller.WhenIdleAsync();
            Assert.StartsWith("y,mean,min,max" + Environment.NewLine + "0,1.5,0,3", window.BuildTable(','));
        }
        finally
        {
            controller.Shutdown();
        }
    });

    [Fact]
    public Task SameState_DoesNotRecompute() => WpfTestHost.Run(async () =>
    {
        // 同じ契機が重なっても(ROI の解除の通知と画像の差し替えなど)、出している結果と同じ求め方なら全画素を読み直さない
        using var view = new FakeView();
        ProjectionWindowController controller = view.Controller();
        try
        {
            controller.Open(H);
            await controller.WhenIdleAsync();
            ProjectionWindow window = controller.WindowFor(H)!;
            double[] shown = Find<ProfilePlotView>(window, "Plot").Values;

            controller.Refresh();
            controller.Refresh();
            await controller.WhenIdleAsync();

            Assert.Same(shown, Find<ProfilePlotView>(window, "Plot").Values);
        }
        finally
        {
            controller.Shutdown();
        }
    });

    [Fact]
    public Task BothWindows_ShowTheirOwnDirection() => WpfTestHost.Run(async () =>
    {
        // 水平と垂直は別々の窓で、同時に開いて並べられる
        using var view = new FakeView();
        ProjectionWindowController controller = view.Controller();
        try
        {
            controller.Open(H);
            controller.Open(V);
            await controller.WhenIdleAsync();

            Assert.StartsWith("x,mean,min,max", controller.WindowFor(H)!.BuildTable(','));
            Assert.StartsWith("y,mean,min,max", controller.WindowFor(V)!.BuildTable(','));
            Assert.Same(controller.WindowFor(H), view.OtherWindowWhenCreated[V]);
        }
        finally
        {
            controller.Shutdown();
        }
    });

    [Fact]
    public Task FrameMovedWhileComputing_ShowsTheMovedFrame() => WpfTestHost.Run(async () =>
    {
        // 計算中にフレームを送ったら(画像は同じ)、前のフレームの結果を送った後のフレームの値として出さず、
        // 送った後のフレームで計算し直す
        using var view = new FakeView();
        view.MoveFrameDuringFirstComputation = true;
        ProjectionWindowController controller = view.Controller();
        try
        {
            controller.Open(H);
            await controller.WhenIdleAsync();

            // frame1 は frame0 + 100
            Assert.StartsWith("x,mean,min,max" + Environment.NewLine + "0,102,100,104",
                controller.WindowFor(H)!.BuildTable(','));
            Assert.Contains("フレーム 2/2", Find<TextBlock>(controller.WindowFor(H)!, "TargetText").Text);
        }
        finally
        {
            controller.Shutdown();
        }
    });

    [Fact]
    public Task CanceledAfterMovingBack_EndsBusyAndKeepsTheShownResult() => WpfTestHost.Run(async () =>
    {
        // フレームを送って計算中に元のフレームへ戻した(送りで計算を取り消した)ら、出していた結果がいまの表示の
        // ものなので計算し直さず、計算中の表示をやめてその結果のまま出す(計算中のまま残さない)
        using var view = new FakeView();
        ProjectionWindowController controller = view.Controller();
        try
        {
            controller.Open(H);
            await controller.WhenIdleAsync();
            ProjectionWindow window = controller.WindowFor(H)!;
            string? shown = window.BuildTable(',');
            string header = Find<TextBlock>(window, "TargetText").Text;

            view.Gate = new SemaphoreSlim(0);
            view.Frame = 1;
            controller.Refresh();
            await WaitUntil(() => controller.IsComputing);
            Assert.True(window.IsBusy);
            Assert.Contains("フレーム 2/2", Find<TextBlock>(window, "TargetText").Text);

            view.Frame = 0;
            controller.CancelRunning();
            await controller.WhenIdleAsync();

            Assert.False(window.IsBusy);
            Assert.Equal(shown, window.BuildTable(','));
            Assert.Equal(header, Find<TextBlock>(window, "TargetText").Text);
        }
        finally
        {
            controller.Shutdown();
        }
    });

    [Fact]
    public Task RefusedWhileTheOtherKeepsComputing_IsNotOverwrittenByTheResult() => WpfTestHost.Run(async () =>
    {
        // 水平・垂直を1回の走査で計算している途中で、垂直だけ断る対象になった(HDR 分割ビューの画像全体の垂直射影)。
        // 水平のための計算は続けるが、終わった結果で垂直の窓の理由を上書きしない
        using var view = new FakeView { Gate = new SemaphoreSlim(0) };
        ProjectionWindowController controller = view.Controller();
        try
        {
            controller.Open(H);
            controller.Open(V);
            await WaitUntil(() => controller.IsComputing);

            view.SegmentWidth = 2;
            controller.Refresh();
            await WaitUntil(() => Find<TextBlock>(controller.WindowFor(V)!, "StatsText").Text
                == ProjectionTargets.HdrSplitWholeImageVertical);
            Assert.True(controller.IsComputing); // 水平のための計算は続ける
            view.Gate.Release(10);
            await controller.WhenIdleAsync();

            Assert.NotNull(controller.WindowFor(H)!.BuildTable(','));
            Assert.Null(controller.WindowFor(V)!.BuildTable(','));
            Assert.Equal(ProjectionTargets.HdrSplitWholeImageVertical,
                Find<TextBlock>(controller.WindowFor(V)!, "StatsText").Text);
        }
        finally
        {
            controller.Shutdown();
        }
    });

    [Fact]
    public Task Playing_ShowsItRecomputesOnStop() => WpfTestHost.Run(async () =>
    {
        using var view = new FakeView { Playing = true };
        ProjectionWindowController controller = view.Controller();
        try
        {
            controller.Open(H);
            await controller.WhenIdleAsync();
            ProjectionWindow window = controller.WindowFor(H)!;
            Assert.Equal(ProjectionTargets.Playback, Find<TextBlock>(window, "StatsText").Text);
            Assert.Null(window.BuildTable(','));

            // 停止したとき(送りの後の計算し直し)に表示中のフレームで計算する
            view.Playing = false;
            controller.Refresh();
            await controller.WhenIdleAsync();
            Assert.NotNull(window.BuildTable(','));
        }
        finally
        {
            controller.Shutdown();
        }
    });

    [Fact]
    public Task ChannelSplit_GuidesWithoutRoiAndUsesQuadrantChannel() => WpfTestHost.Run(async () =>
    {
        // 値 = y*8+x の 8×4 RGGB。チャネル分割表示で ROI がなければ象限を囲むよう案内し(チャネルを勝手に選ばない)、
        // 右上象限(Gr)の 2×2 を囲むと元画像 x=1,3 / y=0,2 の Gr の画素だけで射影を取る
        ushort[] codes = Enumerable.Range(0, 32).Select(i => (ushort)i).ToArray();
        using var view = new FakeView(TestImages.FromCodes(codes, 8, 4, 16, BayerPattern.Rggb)) { Split = true };
        ProjectionWindowController controller = view.Controller();
        try
        {
            controller.Open(H);
            await controller.WhenIdleAsync();
            ProjectionWindow window = controller.WindowFor(H)!;
            Assert.Equal(ProjectionTargets.ChannelSplitWithoutRoi, Find<TextBlock>(window, "StatsText").Text);
            Assert.Null(window.BuildTable(','));

            view.Roi = new RegionOfInterest(4, 0, 2, 2);
            controller.Refresh();
            await controller.WhenIdleAsync();
            Assert.Equal(
                $"x_display,x_source,mean,min,max{Environment.NewLine}4,1,9,1,17{Environment.NewLine}" +
                $"5,3,11,3,19{Environment.NewLine}",
                window.BuildTable(','));
            Assert.Contains("チャネル Gr", Find<TextBlock>(window, "TargetText").Text);

            // 象限をまたぐ ROI は理由を示して断る(前の結果は消す)
            view.Roi = new RegionOfInterest(3, 0, 2, 2);
            controller.Refresh();
            await controller.WhenIdleAsync();
            Assert.Contains("象限", Find<TextBlock>(window, "StatsText").Text);
            Assert.Null(window.BuildTable(','));
        }
        finally
        {
            controller.Shutdown();
        }
    });

    [Fact]
    public Task HdrSplitView_WholeImageVerticalIsRefusedButHorizontalIsComputed() => WpfTestHost.Run(async () =>
    {
        using var view = new FakeView { SegmentWidth = 2 };
        ProjectionWindowController controller = view.Controller();
        try
        {
            controller.Open(H);
            controller.Open(V);
            await controller.WhenIdleAsync();

            Assert.NotNull(controller.WindowFor(H)!.BuildTable(','));
            Assert.Null(controller.WindowFor(V)!.BuildTable(','));
            Assert.Equal(ProjectionTargets.HdrSplitWholeImageVertical,
                Find<TextBlock>(controller.WindowFor(V)!, "StatsText").Text);
        }
        finally
        {
            controller.Shutdown();
        }
    });

    [Fact]
    public Task ClosingTheWindow_IsReportedAndCloseReopens() => WpfTestHost.Run(async () =>
    {
        // 窓の ✕ で閉じたらツールバーのトグルを戻せるよう知らせる。もう一度開くと計算し直す
        using var view = new FakeView();
        ProjectionWindowController controller = view.Controller();
        var closed = new List<ProjectionDirection>();
        controller.WindowClosed += closed.Add;
        try
        {
            controller.Open(V);
            await controller.WhenIdleAsync();
            controller.WindowFor(V)!.Close();
            Assert.Equal(new[] { V }, closed);
            Assert.Null(controller.WindowFor(V));

            controller.Open(V);
            await controller.WhenIdleAsync();
            Assert.NotNull(controller.WindowFor(V)!.BuildTable(','));

            controller.Close(V);
            Assert.Equal(new[] { V, V }, closed);
        }
        finally
        {
            controller.Shutdown();
        }
    });

    [Fact]
    public Task Shutdown_ClosesBothWindowsAndStopsRefreshing() => WpfTestHost.Run(async () =>
    {
        using var view = new FakeView();
        ProjectionWindowController controller = view.Controller();
        controller.Open(H);
        controller.Open(V);
        await controller.WhenIdleAsync();

        controller.Shutdown();

        Assert.Null(controller.WindowFor(H));
        Assert.Null(controller.WindowFor(V));
        controller.Open(H);
        Assert.Null(controller.WindowFor(H));
    });

    /// <summary>条件が成り立つまで UI スレッドを回して待つ(計算の開始などを待つ)。</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "待っている状態になりませんでした");
            await Task.Delay(5);
        }
    }

    private static string Table(string axis, params (long Position, double Mean, double Min, double Max)[] rows)
    {
        string nl = Environment.NewLine;
        return $"{axis},mean,min,max{nl}" + string.Concat(rows.Select(row =>
            $"{row.Position},{ProfilePlotView.FormatValue(row.Mean)},{row.Min},{row.Max}{nl}"));
    }

    /// <summary>
    /// 表示の状態の代わり。既定は 4×2×2 フレームの 16bit: frame0 は値 = y*4+x(0 1 2 3 / 4 5 6 7)、frame1 は +100。
    /// </summary>
    private sealed class FakeView : IDisposable
    {
        internal FakeView(RawImage? image = null)
        {
            Image = image ?? TestImages.FromCodes(
                new ushort[] { 0, 1, 2, 3, 4, 5, 6, 7, 100, 101, 102, 103, 104, 105, 106, 107 },
                new RawFormat { Width = 4, Height = 2, BitDepth = 16, FrameCount = 2 });
        }

        internal RawImage Image { get; }

        internal int Frame { get; set; }

        internal RegionOfInterest? Roi { get; set; }

        internal bool Split { get; set; }

        internal int SegmentWidth { get; set; }

        internal bool Playing { get; set; }

        internal bool MoveFrameDuringFirstComputation { get; set; }

        /// <summary>設定すると、計算はこれを1つ取るまで待つ(取り消されたら止まる。計算中の状態を作るため)。</summary>
        internal SemaphoreSlim? Gate { get; set; }

        internal Dictionary<ProjectionDirection, ProjectionWindow?> OtherWindowWhenCreated { get; } = new();

        internal ProjectionWindowController Controller() => new(
            Dispatcher.CurrentDispatcher,
            Build,
            () => Playing,
            IsCurrent,
            (direction, other) =>
            {
                OtherWindowWhenCreated[direction] = other;
                return new ProjectionWindow(direction) { BusyDelay = TimeSpan.Zero };
            },
            Layout,
            (pass, token, progress) =>
            {
                Gate?.Wait(token);
                return ProjectionTargets.Compute(pass.Image, pass.Frame, pass.Target, pass.Axes, token, progress);
            });

        public void Dispose() => Image.Dispose();

        private ProjectionRequest? Build(ProjectionDirection direction)
        {
            RoiAnalysisTarget target = ProjectionTargets.Resolve(
                direction, Roi, Split, Image.Width, Image.Height, Image.Format.Bayer, SegmentWidth);
            string header = ProjectionTargets.Describe(Roi, target, Image.Width, Image.Height,
                ProjectionTargets.SourceNote(Frame, Image.FrameCount, 0, 0, null));
            return new ProjectionRequest(direction, Image, Frame, target, header, (1 << Image.Format.BitDepth) - 1);
        }

        private bool IsCurrent(RawImage image, int frame)
        {
            if (MoveFrameDuringFirstComputation)
            {
                // 計算中にフレームを送った(送りの契機の前に計算が終わった)ことにする
                MoveFrameDuringFirstComputation = false;
                Frame = 1;
            }

            return ReferenceEquals(image, Image) && frame == Frame;
        }
    }
}
