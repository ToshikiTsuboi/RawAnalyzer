using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 射影の窓の対象の決め方(ROI があれば ROI、なければ画像全体)・断る理由・横軸の座標・対象の説明。
/// </summary>
public class ProjectionTargetsTests
{
    private const ProjectionDirection H = ProjectionDirection.Horizontal;
    private const ProjectionDirection V = ProjectionDirection.Vertical;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoRoi_IsWholeImage(bool horizontal)
    {
        ProjectionDirection direction = horizontal ? H : V;
        Assert.Equal(new WholeImageTarget(), Resolve(direction, roi: null));

        // 画像の外(余白)だけをドラッグした画素数0の ROI も、ヒストグラムと同じく ROI なしとみなす
        Assert.Equal(new WholeImageTarget(), Resolve(direction, new RegionOfInterest(10, 10, 0, 0)));
    }

    [Fact]
    public void Roi_IsClampedSourceRectangle()
    {
        Assert.Equal(
            new SourceRoiTarget(new RegionOfInterest(100, 200, 300, 400)),
            Resolve(H, new RegionOfInterest(100, 200, 300, 400)));
        Assert.Equal(
            new SourceRoiTarget(new RegionOfInterest(3900, 2900, 100, 100)),
            Resolve(V, new RegionOfInterest(3900, 2900, 500, 500)));

        UnsupportedRoiTarget outside = Assert.IsType<UnsupportedRoiTarget>(
            Resolve(H, new RegionOfInterest(5000, 0, 10, 10)));
        Assert.Contains("範囲外", outside.Reason);
    }

    [Fact]
    public void ChannelSplit_RoiInOneQuadrant_IsThatChannelsGrid()
    {
        // 4000×3000 RGGB の分割表示(象限 2000×1500)。右上の象限(Gr)の (2100, 100) から 3×3 は、
        // 元画像の x = 201, 203, 205 / y = 200, 202, 204 の Gr の格子
        ChannelRoiTarget channel = Assert.IsType<ChannelRoiTarget>(
            Resolve(H, new RegionOfInterest(2100, 100, 3, 3), split: true));

        Assert.Equal(new ChannelRegion(201, 200, 3, 3), channel.Region);
        Assert.Equal(BayerChannel.Gr, channel.Channel);
        Assert.Equal(new RegionOfInterest(2100, 100, 3, 3), channel.DisplayRoi);
    }

    [Fact]
    public void ChannelSplit_RoiAcrossQuadrants_IsRefusedWithReason()
    {
        UnsupportedRoiTarget refused = Assert.IsType<UnsupportedRoiTarget>(
            Resolve(V, new RegionOfInterest(1990, 100, 20, 20), split: true));
        Assert.Contains("象限", refused.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChannelSplit_WithoutRoi_AsksForQuadrantInsteadOfPickingAChannel(bool horizontal)
    {
        ProjectionDirection direction = horizontal ? H : V;
        // 4チャネルのどれかを勝手に選ばない(チャネルを混ぜた画像全体にもしない)。象限を ROI で囲むよう案内する
        UnsupportedRoiTarget refused = Assert.IsType<UnsupportedRoiTarget>(Resolve(direction, null, split: true));
        Assert.Equal(ProjectionTargets.ChannelSplitWithoutRoi, refused.Reason);
        Assert.Contains("象限全体を囲むとそのチャネル全体", refused.Reason);
    }

    [Fact]
    public void HdrSplitView_WholeImage_HorizontalIsAllowedButVerticalIsRefused()
    {
        // HDR分割ビューは各露光の段(幅 2000)を左右に並べた 4000×3000。各列は1つの段に収まるので画像全体の水平射影は
        // 取れるが、各行は長秒と短秒の段をまたぐので画像全体の垂直射影は露光差を平均に混ぜる
        Assert.Equal(new WholeImageTarget(), Resolve(H, null, segmentWidth: 2000));
        UnsupportedRoiTarget refused = Assert.IsType<UnsupportedRoiTarget>(Resolve(V, null, segmentWidth: 2000));
        Assert.Equal(ProjectionTargets.HdrSplitWholeImageVertical, refused.Reason);
        Assert.Contains("段", refused.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HdrSplitView_Roi_FollowsTheSegmentRule(bool horizontal)
    {
        ProjectionDirection direction = horizontal ? H : V;
        // ROI は既存の規則(段の中なら取れる、段をまたぐなら断る)。段全体を囲めばその段の垂直射影になる
        Assert.Equal(
            new SourceRoiTarget(new RegionOfInterest(2000, 0, 2000, 3000)),
            Resolve(direction, new RegionOfInterest(2000, 0, 2000, 3000), segmentWidth: 2000));
        UnsupportedRoiTarget refused = Assert.IsType<UnsupportedRoiTarget>(
            Resolve(direction, new RegionOfInterest(1900, 0, 200, 100), segmentWidth: 2000));
        Assert.Contains("段", refused.Reason);
    }

    [Fact]
    public void Axis_UsesRoiOriginAndSplitViewCoordinates()
    {
        var source = new SourceRoiTarget(new RegionOfInterest(100, 200, 3, 3));
        Assert.Equal(new ProjectionAxis(100, null), ProjectionTargets.AxisOf(source, H));
        Assert.Equal(new ProjectionAxis(200, null), ProjectionTargets.AxisOf(source, V));
        Assert.Equal(new ProjectionAxis(0, null), ProjectionTargets.AxisOf(new WholeImageTarget(), V));

        // チャネル分割表示は ROI を描いた表示座標に並べ、元画像の座標(2画素おき)も持つ
        var channel = new ChannelRoiTarget(
            new ChannelRegion(201, 200, 3, 3), BayerChannel.Gr, new RegionOfInterest(2100, 100, 3, 3));
        ProjectionAxis horizontal = ProjectionTargets.AxisOf(channel, H);
        ProjectionAxis vertical = ProjectionTargets.AxisOf(channel, V);
        Assert.Equal(new ProjectionAxis(2100, 201), horizontal);
        Assert.Equal(new ProjectionAxis(100, 200), vertical);
        Assert.True(horizontal.IsSplitDisplay);
        Assert.Equal(205, horizontal.SourceCoordinate(2));
        Assert.Equal(204, vertical.SourceCoordinate(2));
        Assert.Equal(102, new ProjectionAxis(100, null).SourceCoordinate(2));
    }

    [Fact]
    public void Describe_NamesTargetChannelAndFrame()
    {
        Assert.Equal("対象: 画像全体 (4000×3000)",
            ProjectionTargets.Describe(null, new WholeImageTarget(), 4000, 3000, ""));
        Assert.Equal("対象: ROI (100, 200, 640×480) · フレーム 3/10",
            ProjectionTargets.Describe(new RegionOfInterest(100, 200, 640, 480),
                new SourceRoiTarget(new RegionOfInterest(100, 200, 640, 480)), 4000, 3000, "フレーム 3/10"));
        Assert.Equal("対象: ROI (2100, 100, 3×3・チャネル分割表示の座標) · チャネル Gr",
            ProjectionTargets.Describe(new RegionOfInterest(2100, 100, 3, 3),
                new ChannelRoiTarget(new ChannelRegion(201, 200, 3, 3), BayerChannel.Gr,
                    new RegionOfInterest(2100, 100, 3, 3)), 4000, 3000, ""));

        // 断るときも何を選んでいるかは示す
        Assert.Equal("対象: ROI (1990, 100, 20×20)",
            ProjectionTargets.Describe(new RegionOfInterest(1990, 100, 20, 20),
                new UnsupportedRoiTarget("x"), 4000, 3000, ""));
        Assert.Equal("対象: ROI なし · HDR分割ビュー",
            ProjectionTargets.Describe(null, new UnsupportedRoiTarget("x"), 4000, 3000, "HDR分割ビュー"));
    }

    [Fact]
    public void SourceNote_ShowsHdrViewAndPageOrFrame()
    {
        Assert.Equal("", ProjectionTargets.SourceNote(0, 1, 0, 0, null));
        Assert.Equal("フレーム 3/10", ProjectionTargets.SourceNote(2, 10, 0, 0, null));
        Assert.Equal("ページ 2/5", ProjectionTargets.SourceNote(0, 1, 1, 5, null));
        Assert.Equal("HDR分割ビュー · フレーム 1/4", ProjectionTargets.SourceNote(0, 4, 0, 0, "HDR分割ビュー"));
    }

    [Fact]
    public void BuildRequest_CombinesTargetHeaderAndMaxCode()
    {
        // いまの表示の状態から求め方を作る(MainWindow はこの状態を渡すだけ)。画素数0の ROI は ROI なし
        using RawImage image = TestImages.FromCodes(new ushort[8 * 4], 8, 4, bitDepth: 12, BayerPattern.Rggb);
        var view = new ProjectionView(image, 0, new RegionOfInterest(2, 2, 0, 0), false, BayerPattern.Rggb, 0,
            "フレーム 1/3");

        ProjectionRequest request = ProjectionTargets.BuildRequest(ProjectionDirection.Vertical, view);

        Assert.Equal(new WholeImageTarget(), request.Target);
        Assert.Equal("対象: 画像全体 (8×4) · フレーム 1/3", request.Header);
        Assert.Equal(4095, request.MaxCode);
        Assert.Equal(ProjectionDirection.Vertical, request.Direction);
        Assert.Same(image, request.Image);
        Assert.Equal(request, ProjectionTargets.BuildRequest(ProjectionDirection.Vertical, view));
    }

    [Fact]
    public void Compute_ReadsTheResolvedPixels()
    {
        // 値 = y*8+x の 8×4 RGGB。分割表示の右上象限(Gr)の2×2 → 元画像 x=1,3 / y=0,2
        ushort[] codes = Enumerable.Range(0, 32).Select(i => (ushort)i).ToArray();
        using RawImage image = TestImages.FromCodes(codes, 8, 4, 16, BayerPattern.Rggb);

        RoiAnalysisTarget channel = ProjectionTargets.Resolve(H, new RegionOfInterest(4, 0, 2, 2), true, 8, 4,
            BayerPattern.Rggb, 0);
        ProjectionResult split = ProjectionTargets.Compute(image, 0, channel, ProjectionAxes.Both, CancellationToken.None);
        Assert.Equal(new[] { (1 + 17) / 2.0, (3 + 19) / 2.0 }, split.Horizontal!.Mean);
        Assert.Equal(new[] { (1 + 3) / 2.0, (17 + 19) / 2.0 }, split.Vertical!.Mean);

        // 画像全体: 列 x の平均は (x + x+8 + x+16 + x+24)/4 = x + 12、行 y の平均は 8y + 3.5
        ProjectionResult whole = ProjectionTargets.Compute(
            image, 0, new WholeImageTarget(), ProjectionAxes.Both, CancellationToken.None);
        Assert.Equal(Enumerable.Range(0, 8).Select(x => x + 12.0), whole.Horizontal!.Mean);
        Assert.Equal(Enumerable.Range(0, 4).Select(y => (8 * y) + 3.5), whole.Vertical!.Mean);

        // ROI(元画像の矩形)
        ProjectionResult roi = ProjectionTargets.Compute(image, 0,
            new SourceRoiTarget(new RegionOfInterest(2, 1, 2, 2)), ProjectionAxes.Horizontal, CancellationToken.None);
        Assert.Equal(new[] { (10 + 18) / 2.0, (11 + 19) / 2.0 }, roi.Horizontal!.Mean);
        Assert.Null(roi.Vertical);

        // 断る対象は別の画素で代用しない
        Assert.Throws<InvalidOperationException>(() => ProjectionTargets.Compute(
            image, 0, new UnsupportedRoiTarget("理由"), ProjectionAxes.Both, CancellationToken.None));
    }

    [Fact]
    public void Texts_ExplainDirectionAndTheOtherConvention()
    {
        // EMVA 1288 方式(結果を並べる軸で呼ぶ)。逆の流儀(平均する方向で呼ぶ HALCON など)では名前が入れ替わることを示す
        string horizontal = ProjectionText.Explanation(H);
        Assert.StartsWith("水平射影: 各列を縦に平均した値を x 座標に並べます。", horizontal);
        Assert.Contains("列 FPN", horizontal);
        Assert.Contains("EMVA 1288 の horizontal profile", horizontal);
        Assert.Contains("HALCON など)では「垂直射影」", horizontal);
        string vertical = ProjectionText.Explanation(V);
        Assert.StartsWith("垂直射影: 各行を横に平均した値を y 座標に並べます。", vertical);
        Assert.Contains("行 FPN", vertical);
        Assert.Contains("EMVA 1288 の vertical profile", vertical);
        Assert.Contains("HALCON など)では「水平射影」", vertical);

        Assert.StartsWith(horizontal, ProjectionText.ToolbarToolTip(H));
        Assert.StartsWith(vertical, ProjectionText.WindowHelp(V));
        Assert.Equal("水平射影 — x 座標(列ごとの平均) [px・画像座標]", ProjectionText.AxisTitle(H, false));
        Assert.Equal("垂直射影 — y 座標(行ごとの平均) [px・チャネル分割表示の座標]", ProjectionText.AxisTitle(V, true));
    }

    private static RoiAnalysisTarget Resolve(
        ProjectionDirection direction, RegionOfInterest? roi, bool split = false, int segmentWidth = 0) =>
        ProjectionTargets.Resolve(direction, roi, split, 4000, 3000, BayerPattern.Rggb, segmentWidth);
}
