using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 射影の窓を計算し直すかの判断。ROI・送り・画像の差し替え・表示モードの変更のたびに、開いている窓ごとに
/// いまの表示での求め方と、窓が出している・計算中の求め方を比べて決める。
/// </summary>
public class ProjectionRefreshPlanTests
{
    private static readonly RawImage Image = TestImages.FromCodes(new ushort[16], 4, 4);
    private static readonly RawImage Other = TestImages.FromCodes(new ushort[16], 4, 4);

    [Fact]
    public void SameRequestAsShown_DoesNothing()
    {
        // 同じ契機が重なっても(ROI の解除の通知と画像の差し替えなど)、出している結果と同じなら全画素を読み直さない
        ProjectionRequest shown = Request(ProjectionDirection.Horizontal);

        ProjectionRefreshPlan plan = Decide(new ProjectionWindowState(ProjectionDirection.Horizontal, shown, shown, null));

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void ChangedRoiFrameOrImage_Recomputes()
    {
        ProjectionRequest shown = Request(ProjectionDirection.Horizontal);
        ProjectionRequest[] changed =
        {
            shown with { Target = new SourceRoiTarget(new RegionOfInterest(1, 1, 2, 2)) },
            shown with { Frame = 1 },
            shown with { Image = Other },
        };

        foreach (ProjectionRequest next in changed)
        {
            ProjectionRefreshPlan plan = Decide(new ProjectionWindowState(ProjectionDirection.Horizontal, next, shown, null));
            Assert.Equal(new[] { next }, plan.Compute);
            Assert.False(plan.CancelRunningJob);
        }
    }

    [Fact]
    public void ChangedWhileComputing_CancelsThePreviousComputationAtOnce()
    {
        // ROI・送りの変更で、前の対象の全画素を読み続けずにすぐ取り消して計算し直す
        ProjectionRequest pending = Request(ProjectionDirection.Vertical);
        ProjectionRequest next = pending with { Frame = 2 };

        ProjectionRefreshPlan plan = Decide(new ProjectionWindowState(ProjectionDirection.Vertical, next, null, pending));

        Assert.True(plan.CancelRunningJob);
        Assert.Equal(new[] { next }, plan.Compute);
    }

    [Fact]
    public void SameRequestBeingComputed_KeepsTheRunningComputation()
    {
        ProjectionRequest pending = Request(ProjectionDirection.Vertical);

        ProjectionRefreshPlan plan = Decide(new ProjectionWindowState(ProjectionDirection.Vertical, pending, null, pending));

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void OpeningTheOtherWindow_RestartsBothInOnePass()
    {
        // 水平の計算中に垂直の窓を開くと、走っている計算を取り消して両方を1回の走査で求める(全画素を2度読まない)
        ProjectionRequest horizontal = Request(ProjectionDirection.Horizontal);
        ProjectionRequest vertical = Request(ProjectionDirection.Vertical);

        ProjectionRefreshPlan plan = Decide(
            new ProjectionWindowState(ProjectionDirection.Horizontal, horizontal, null, horizontal),
            new ProjectionWindowState(ProjectionDirection.Vertical, vertical, null, null));

        Assert.True(plan.CancelRunningJob);
        Assert.Equal(new[] { horizontal, vertical }, plan.Compute);
        ProjectionPass pass = Assert.Single(ProjectionRefreshPlan.Passes(plan.Compute));
        Assert.Equal(ProjectionAxes.Both, pass.Axes);
        Assert.Same(Image, pass.Image);
    }

    [Fact]
    public void DifferentTargets_AreSeparatePasses()
    {
        ProjectionRequest horizontal = Request(ProjectionDirection.Horizontal);
        ProjectionRequest vertical = Request(ProjectionDirection.Vertical) with
        {
            Target = new SourceRoiTarget(new RegionOfInterest(0, 0, 2, 2)),
        };

        IReadOnlyList<ProjectionPass> passes = ProjectionRefreshPlan.Passes(new[] { horizontal, vertical });

        Assert.Equal(2, passes.Count);
        Assert.Equal(ProjectionAxes.Horizontal, passes[0].Axes);
        Assert.Equal(ProjectionAxes.Vertical, passes[1].Axes);
    }

    [Fact]
    public void RefusedTarget_ShowsReasonWithoutComputingAndCancelsObsoleteWork()
    {
        // HDR分割ビューの画像全体の垂直射影・象限をまたぐ ROI などは計算せず理由を出す。計算中だったものは取り消す
        ProjectionRequest pending = Request(ProjectionDirection.Vertical);
        ProjectionRequest refused = pending with { Target = new UnsupportedRoiTarget("理由") };

        ProjectionRefreshPlan plan = Decide(new ProjectionWindowState(ProjectionDirection.Vertical, refused, null, pending));

        Assert.Equal(new[] { refused }, plan.ShowRefusal);
        Assert.Empty(plan.Compute);
        Assert.True(plan.CancelRunningJob);

        // 同じ理由をもう出していれば何もしない
        Assert.True(Decide(new ProjectionWindowState(ProjectionDirection.Vertical, refused, refused, null)).IsEmpty);
    }

    [Fact]
    public void OneWindowRefusedWhileTheOtherKeepsComputing_DoesNotCancel()
    {
        // HDR分割ビューへ入ると画像全体の水平射影は求め、垂直射影は断る。水平の計算は続ける
        ProjectionRequest horizontal = Request(ProjectionDirection.Horizontal);
        ProjectionRequest refused = Request(ProjectionDirection.Vertical) with { Target = new UnsupportedRoiTarget("理由") };

        ProjectionRefreshPlan plan = Decide(
            new ProjectionWindowState(ProjectionDirection.Horizontal, horizontal, null, horizontal),
            new ProjectionWindowState(ProjectionDirection.Vertical, refused, null, null));

        Assert.False(plan.CancelRunningJob);
        Assert.Empty(plan.Compute);
        Assert.Equal(new[] { refused }, plan.ShowRefusal);
    }

    [Fact]
    public void Playing_DoesNotComputeAndTellsItRecomputesOnStop()
    {
        // 再生中は送りのたびに全画素を読まない(停止したときの送りの後の計算し直しで求める)
        ProjectionRequest pending = Request(ProjectionDirection.Horizontal);
        ProjectionRequest next = pending with { Frame = 3 };

        ProjectionRefreshPlan plan = ProjectionRefreshPlan.Decide(
            new[]
            {
                new ProjectionWindowState(ProjectionDirection.Horizontal, next, null, pending),
                new ProjectionWindowState(ProjectionDirection.Vertical, Request(ProjectionDirection.Vertical), null, null),
            },
            playing: true);

        Assert.Equal(new[] { ProjectionDirection.Horizontal, ProjectionDirection.Vertical }, plan.ShowPlayback);
        Assert.Empty(plan.Compute);
        Assert.True(plan.CancelRunningJob);
    }

    [Fact]
    public void NoOpenWindow_DoesNothing()
    {
        Assert.True(ProjectionRefreshPlan.Decide(Array.Empty<ProjectionWindowState>(), playing: false).IsEmpty);
    }

    private static ProjectionRefreshPlan Decide(params ProjectionWindowState[] windows) =>
        ProjectionRefreshPlan.Decide(windows, playing: false);

    private static ProjectionRequest Request(ProjectionDirection direction) =>
        new(direction, Image, 0, new WholeImageTarget(), "対象: 画像全体 (4×4)", 65535);
}
