using System.Windows.Threading;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Views;

/// <summary>
/// 水平射影・垂直射影の窓の開閉と計算し直し(計算の取り消し・結果の採用)。
/// </summary>
/// <remarks>
/// <para>
/// いまの表示での求め方(画像・フレーム・ROI・表示モード)は呼び出し側(MainWindow)が作り、ここは窓ごとに
/// 出している結果と比べて計算し直すかを決め(<see cref="ProjectionRefreshPlan"/>)、計算を UI スレッドの外で走らせて
/// 窓へ出す。MainWindow を作らずに検証できるよう、表示の状態と窓の作り方は引数で受け取る。
/// </para>
/// <para>
/// 計算し直しの契機は同じ UI の手番で重なる(画像の差し替えで ROI が消えた通知と差し替え後の呼び出しなど)ので、
/// <see cref="Refresh"/> は手番の後に1回だけ判断する。求め方が変わったら・送り・画像の差し替えで前の計算は
/// すぐ取り消す。取り消した計算は、置き換えられていなければ終わるときに計算し直しを求める(送った先の画像で
/// 求め直すか、再生中なら停止したときに計算し直すことを知らせる)。
/// </para>
/// </remarks>
internal sealed class ProjectionWindowController
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<ProjectionDirection, ProjectionRequest?> _buildRequest;
    private readonly Func<bool> _isPlaying;
    private readonly Func<RawImage, int, bool> _isCurrent;
    private readonly Func<ProjectionDirection, ProjectionWindow?, ProjectionWindow> _createWindow;
    private readonly Action<ProjectionWindow> _showWindow;

    private ProjectionWindow? _horizontal;
    private ProjectionWindow? _vertical;

    // 窓が出している結果・断る理由の求め方(再生中の知らせ・まだ出していなければなし)
    private readonly Dictionary<ProjectionDirection, ProjectionRequest> _shown = new();

    // 走っている計算(なければ null)と、その完了(テストで待つ)
    private ProjectionJob? _job;
    private Task _jobTask = Task.CompletedTask;
    private DispatcherOperation? _scheduled;
    private bool _shutDown;

    /// <summary>窓の管理を作る。</summary>
    /// <param name="dispatcher">UI スレッドのディスパッチャ(計算し直しの判断を手番の後へ送る)。</param>
    /// <param name="buildRequest">いまの表示での求め方を作る(画像がなければ null)。</param>
    /// <param name="isPlaying">再生中か。</param>
    /// <param name="isCurrent">画像・フレームがいまも表示中か(計算中に送った・差し替えたら結果を出さない)。</param>
    /// <param name="createWindow">窓を作る(もう一方の窓が開いていればそれを渡す。並べて開くため)。</param>
    /// <param name="showWindow">作った窓を表示する。</param>
    internal ProjectionWindowController(
        Dispatcher dispatcher,
        Func<ProjectionDirection, ProjectionRequest?> buildRequest,
        Func<bool> isPlaying,
        Func<RawImage, int, bool> isCurrent,
        Func<ProjectionDirection, ProjectionWindow?, ProjectionWindow> createWindow,
        Action<ProjectionWindow> showWindow)
    {
        _dispatcher = dispatcher;
        _buildRequest = buildRequest;
        _isPlaying = isPlaying;
        _isCurrent = isCurrent;
        _createWindow = createWindow;
        _showWindow = showWindow;
    }

    /// <summary>窓が閉じられた(✕ で閉じたときにツールバーのトグルを戻すため)。</summary>
    internal event Action<ProjectionDirection>? WindowClosed;

    /// <summary>開いている窓(なければ null)。</summary>
    /// <param name="direction">向き。</param>
    /// <returns>窓。</returns>
    internal ProjectionWindow? WindowFor(ProjectionDirection direction) =>
        direction == ProjectionDirection.Horizontal ? _horizontal : _vertical;

    /// <summary>計算中か(走っている計算がある)。</summary>
    internal bool IsComputing => _job is not null;

    /// <summary>窓を開いてすぐ計算する(画像のクリックは要らない)。開いていれば何もしない。</summary>
    /// <param name="direction">向き。</param>
    internal void Open(ProjectionDirection direction)
    {
        if (_shutDown || WindowFor(direction) is not null)
        {
            return;
        }

        ProjectionWindow window = _createWindow(direction, WindowFor(Other(direction)));
        window.Closed += (_, _) => OnWindowClosed(direction, window);
        if (direction == ProjectionDirection.Horizontal)
        {
            _horizontal = window;
        }
        else
        {
            _vertical = window;
        }

        _showWindow(window);
        Refresh();
    }

    /// <summary>窓を閉じる(開いていなければ何もしない)。</summary>
    /// <param name="direction">向き。</param>
    internal void Close(ProjectionDirection direction) => WindowFor(direction)?.Close();

    /// <summary>計算を取り消して窓を閉じる(メインウィンドウを閉じるとき)。</summary>
    internal void Shutdown()
    {
        _shutDown = true;
        _scheduled?.Abort();
        _scheduled = null;
        CancelRunning();
        _horizontal?.Close();
        _vertical?.Close();
    }

    /// <summary>
    /// 開いている窓を、いまの表示で計算し直すよう求める(判断は UI の手番の後に1回だけ行う)。
    /// </summary>
    internal void Refresh()
    {
        if (_shutDown || _scheduled is not null || (_horizontal is null && _vertical is null && _job is null))
        {
            return;
        }

        _scheduled = _dispatcher.InvokeAsync(RunRefresh, DispatcherPriority.Background);
    }

    /// <summary>走っている計算を取り消す(送り・画像の差し替えのとき)。終わるときに計算し直しを求める。</summary>
    internal void CancelRunning()
    {
        _job?.Cts.Cancel();
    }

    /// <summary>求めた計算し直しと走っている計算が終わるまで待つ(テスト用)。</summary>
    /// <returns>待つタスク。</returns>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            if (_scheduled is { } scheduled)
            {
                await scheduled;
                continue;
            }

            if (_job is not null)
            {
                await _jobTask;
                continue;
            }

            return;
        }
    }

    private static ProjectionDirection Other(ProjectionDirection direction) =>
        direction == ProjectionDirection.Horizontal ? ProjectionDirection.Vertical : ProjectionDirection.Horizontal;

    private void OnWindowClosed(ProjectionDirection direction, ProjectionWindow window)
    {
        if (!ReferenceEquals(WindowFor(direction), window))
        {
            return;
        }

        if (direction == ProjectionDirection.Horizontal)
        {
            _horizontal = null;
        }
        else
        {
            _vertical = null;
        }

        _shown.Remove(direction);
        WindowClosed?.Invoke(direction);

        // この窓のための計算だけが走っていれば取り消す
        Refresh();
    }

    private void RunRefresh()
    {
        _scheduled = null;
        if (_shutDown)
        {
            return;
        }

        var states = new List<ProjectionWindowState>();
        foreach (ProjectionDirection direction in new[] { ProjectionDirection.Horizontal, ProjectionDirection.Vertical })
        {
            if (WindowFor(direction) is null || _buildRequest(direction) is not { } next)
            {
                continue;
            }

            states.Add(new ProjectionWindowState(
                direction, next, _shown.GetValueOrDefault(direction),
                _job?.Requests.FirstOrDefault(request => request.Direction == direction)));
        }

        ProjectionRefreshPlan plan = ProjectionRefreshPlan.Decide(states, _isPlaying());

        // 新しく計算するとき、続ける窓のない計算(求め方が変わった・窓を閉じた)は取り消す。置き換えた計算は
        // 終わっても計算し直しを求めない(この判断で求め方を決め直している)
        bool cancel = plan.CancelRunningJob || plan.Compute.Count > 0
            || (_job is { } job && !job.Requests.Any(request => WindowFor(request.Direction) is not null));
        if (cancel && _job is { } running)
        {
            _job = null;
            running.Cts.Cancel();
        }

        foreach (ProjectionDirection direction in plan.ShowPlayback)
        {
            ProjectionRequest next = states.First(state => state.Direction == direction).Next;
            WindowFor(direction)?.ShowMessage(next.Header, ProjectionTargets.Playback, next.MaxCode);
            _shown.Remove(direction);
        }

        foreach (ProjectionRequest refused in plan.ShowRefusal)
        {
            WindowFor(refused.Direction)?.ShowMessage(
                refused.Header, refused.RefusalReason ?? "射影を取れません。", refused.MaxCode);
            _shown[refused.Direction] = refused;
        }

        if (plan.Compute.Count > 0)
        {
            var started = new ProjectionJob(plan.Compute);
            _job = started;
            _jobTask = RunJobAsync(started);
        }
    }

    /// <summary>計算を UI スレッドの外で走らせ、終わったら結果を窓へ出す。</summary>
    private async Task RunJobAsync(ProjectionJob job)
    {
        foreach (ProjectionRequest request in job.Requests)
        {
            WindowFor(request.Direction)?.ShowBusy(request.Header);
        }

        // 水平・垂直を同じ対象で求めるときは1回の走査で両方を求める(10億画素を2度読まない)
        IReadOnlyList<ProjectionPass> passes = ProjectionRefreshPlan.Passes(job.Requests);
        var progress = new Progress<(int Pass, double Fraction)>(report =>
        {
            if (!ReferenceEquals(_job, job))
            {
                return;
            }

            double fraction = (report.Pass + report.Fraction) / passes.Count;
            foreach (ProjectionRequest request in job.Requests)
            {
                WindowFor(request.Direction)?.ReportProgress(fraction);
            }
        });
        CancellationToken token = job.Cts.Token;
        ProjectionResult[] results;
        try
        {
            results = await Task.Run(
                () =>
                {
                    var computed = new ProjectionResult[passes.Count];
                    for (int i = 0; i < passes.Count; i++)
                    {
                        ProjectionPass pass = passes[i];
                        computed[i] = ProjectionTargets.Compute(
                            pass.Image, pass.Frame, pass.Target, pass.Axes, token, new PassProgress(progress, i));
                    }

                    return computed;
                },
                token);
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            // 取り消された・計算中に画像が破棄された。置き換えられていなければ、いまの表示で計算し直す
            if (ReferenceEquals(_job, job))
            {
                _job = null;
                Refresh();
            }

            return;
        }
        catch (Exception ex)
        {
            AppLog.Error("射影の計算に失敗", ex);
            if (ReferenceEquals(_job, job))
            {
                _job = null;
                foreach (ProjectionRequest request in job.Requests)
                {
                    WindowFor(request.Direction)?.ShowMessage(
                        request.Header, $"射影を計算できませんでした: {ex.Message}", request.MaxCode);
                    _shown[request.Direction] = request;
                }
            }

            return;
        }

        if (!ReferenceEquals(_job, job))
        {
            return;
        }

        _job = null;

        // 計算中に画像を差し替えた・フレームを送ったなら、結果は表示中の画像・フレームのものではない
        // (フレーム送りは画像がそのままなので、画像の照合だけでは前のフレームの値を表示してしまう)
        if (job.Requests.Any(request => !_isCurrent(request.Image, request.Frame)))
        {
            Refresh();
            return;
        }

        foreach (ProjectionRequest request in job.Requests)
        {
            if (WindowFor(request.Direction) is not { } window)
            {
                continue;
            }

            int pass = Enumerable.Range(0, passes.Count).First(i => ReferenceEquals(passes[i].Image, request.Image)
                && passes[i].Frame == request.Frame && passes[i].Target.Equals(request.Target));
            ProjectionProfile? profile = request.Direction == ProjectionDirection.Horizontal
                ? results[pass].Horizontal
                : results[pass].Vertical;
            if (profile is null)
            {
                continue;
            }

            window.ShowResult(
                request.Header, profile, ProjectionTargets.AxisOf(request.Target, request.Direction), request.MaxCode);
            _shown[request.Direction] = request;
        }
    }

    /// <summary>走っている射影の計算(取り消しと、何を求めているか)。</summary>
    private sealed class ProjectionJob(IReadOnlyList<ProjectionRequest> requests)
    {
        internal CancellationTokenSource Cts { get; } = new();

        internal IReadOnlyList<ProjectionRequest> Requests { get; } = requests;
    }

    /// <summary>1回の走査の進み具合を、何番目の走査かを添えて UI スレッドへ送る。</summary>
    private sealed class PassProgress(IProgress<(int Pass, double Fraction)> target, int pass) : IProgress<double>
    {
        public void Report(double value) => target.Report((pass, value));
    }
}
