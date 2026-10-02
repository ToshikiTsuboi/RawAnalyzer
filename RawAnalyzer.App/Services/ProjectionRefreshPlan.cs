using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>開いている射影の窓1つの状態。</summary>
/// <param name="Direction">窓の向き。</param>
/// <param name="Next">いまの表示(画像・フレーム・ROI・表示モード)での求め方。</param>
/// <param name="Shown">窓が出している結果・断る理由の求め方(まだ出していない・再生中の知らせなら null)。</param>
/// <param name="Pending">計算中の求め方(計算していなければ null)。</param>
internal readonly record struct ProjectionWindowState(
    ProjectionDirection Direction, ProjectionRequest Next, ProjectionRequest? Shown, ProjectionRequest? Pending);

/// <summary>1回の走査で求める射影(同じ画像・フレーム・対象の向きをまとめる)。</summary>
/// <param name="Image">対象画像。</param>
/// <param name="Frame">フレーム。</param>
/// <param name="Target">対象。</param>
/// <param name="Axes">求める向き。</param>
internal sealed record ProjectionPass(RawImage Image, int Frame, RoiAnalysisTarget Target, ProjectionAxes Axes);

/// <summary>
/// 射影の窓を計算し直すかの判断。ROI・送り・画像の差し替え・表示モードの変更のたびに、開いている窓ごとに
/// いまの表示での求め方を作って決める。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>窓が出している結果と同じ求め方なら計算し直さない(同じ契機が重なっても1回で済む)。</item>
/// <item>同じ求め方を計算中ならその計算を続ける。別の窓が新しく計算するときは、走っている計算を取り消し、
///   続けるはずだった向きも一緒に計算し直す(水平・垂直が同じ対象なら1回の走査で両方を求める)。</item>
/// <item>求め方が変わった計算はすぐ取り消す(前の ROI・フレームの全画素を読み続けない)。</item>
/// <item>断る対象(象限・段をまたぐ ROI など)は計算せず理由を出す。</item>
/// <item>再生中は計算せず、停止したときに計算し直すことを知らせる(送りのたびに全画素を読まない)。</item>
/// </list>
/// </remarks>
/// <param name="ShowPlayback">再生中の知らせを出す窓。</param>
/// <param name="ShowRefusal">断る理由を出す求め方。</param>
/// <param name="Compute">新しい計算で求める求め方(計算中を出す)。</param>
/// <param name="CancelRunningJob">走っている計算を取り消すか。</param>
internal sealed record ProjectionRefreshPlan(
    IReadOnlyList<ProjectionDirection> ShowPlayback,
    IReadOnlyList<ProjectionRequest> ShowRefusal,
    IReadOnlyList<ProjectionRequest> Compute,
    bool CancelRunningJob)
{
    /// <summary>何もしない(開いている窓がない・どの窓も今の結果を出している)か。</summary>
    internal bool IsEmpty => ShowPlayback.Count == 0 && ShowRefusal.Count == 0 && Compute.Count == 0 && !CancelRunningJob;

    /// <summary>計算し直すかを決める。</summary>
    /// <param name="windows">開いている窓の状態。</param>
    /// <param name="playing">再生中か。</param>
    /// <returns>判断。</returns>
    internal static ProjectionRefreshPlan Decide(IReadOnlyList<ProjectionWindowState> windows, bool playing)
    {
        bool running = windows.Any(window => window.Pending is not null);
        if (playing)
        {
            return new ProjectionRefreshPlan(
                windows.Select(window => window.Direction).ToList(), Array.Empty<ProjectionRequest>(),
                Array.Empty<ProjectionRequest>(), running);
        }

        var refusals = new List<ProjectionRequest>();
        var fresh = new List<ProjectionRequest>();
        var kept = new List<ProjectionRequest>();
        foreach (ProjectionWindowState window in windows)
        {
            if (window.Next.Equals(window.Shown) && window.Pending is null)
            {
                continue;
            }

            if (!window.Next.IsComputable)
            {
                if (!window.Next.Equals(window.Shown))
                {
                    refusals.Add(window.Next);
                }

                continue;
            }

            if (window.Next.Equals(window.Pending))
            {
                kept.Add(window.Next);
            }
            else if (!window.Next.Equals(window.Shown))
            {
                fresh.Add(window.Next);
            }
        }

        // 新しく計算する窓があれば、走っている計算を取り消して続けるはずだった向きもまとめて計算し直す。
        // なければ、どの窓も続けない計算(求め方が変わった・断る対象になった)だけを取り消す
        bool startJob = fresh.Count > 0;
        IReadOnlyList<ProjectionRequest> compute = startJob ? kept.Concat(fresh).ToList() : Array.Empty<ProjectionRequest>();
        bool cancel = running && (startJob || kept.Count == 0);
        return new ProjectionRefreshPlan(Array.Empty<ProjectionDirection>(), refusals, compute, cancel);
    }

    /// <summary>
    /// 求め方を、1回の走査で求められるもの(同じ画像・フレーム・対象)ごとにまとめる。
    /// </summary>
    /// <param name="requests">求め方(求められるもの)。</param>
    /// <returns>走査の一覧。</returns>
    internal static IReadOnlyList<ProjectionPass> Passes(IEnumerable<ProjectionRequest> requests)
    {
        var passes = new List<ProjectionPass>();
        foreach (ProjectionRequest request in requests)
        {
            ProjectionAxes axes = ProjectionTargets.ToAxes(request.Direction);
            int index = passes.FindIndex(pass => ReferenceEquals(pass.Image, request.Image)
                && pass.Frame == request.Frame && pass.Target.Equals(request.Target));
            if (index < 0)
            {
                passes.Add(new ProjectionPass(request.Image, request.Frame, request.Target, axes));
            }
            else
            {
                passes[index] = passes[index] with { Axes = passes[index].Axes | axes };
            }
        }

        return passes;
    }
}
