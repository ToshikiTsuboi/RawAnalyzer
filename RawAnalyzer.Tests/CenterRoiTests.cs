using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>「中央に ROI を設定」(Ctrl+Shift+G)で置く ROI の検証。</summary>
public class CenterRoiTests
{
    [Fact]
    public void PlainImage_GetsQuarterAreaRoiAtTheCenter()
    {
        Assert.Equal(new RegionOfInterest(50, 25, 100, 50), CenterRoi.Compute(200, 100));

        // 1画素の画像でも空の ROI にしない
        Assert.Equal(new RegionOfInterest(0, 0, 1, 1), CenterRoi.Compute(1, 1));
    }

    [Theory]
    [InlineData(2, 0, 0)] // 「全体」は長秒の段
    [InlineData(2, 1, 0)] // 長秒
    [InlineData(2, 2, 1)] // 短秒
    [InlineData(3, 2, 1)] // 中秒
    [InlineData(3, 3, 2)] // 短秒
    [InlineData(2, 5, 1)] // 範囲外は最後の段
    [InlineData(2, -1, 0)] // 未選択は長秒
    public void HdrSplitView_PutsTheRoiInsideTheTargetStage(int stages, int targetIndex, int expectedStage)
    {
        // HDR分割ビューは各段を横に並べた画像なので、画像全体の中央は段の継ぎ目(2段なら W/2、3段なら W/3 と 2W/3)
        // になる。以前は継ぎ目をまたぐ ROI を作り、長秒と短秒の画素を混ぜた平均・σを ROI 統計として出していた
        const int segmentWidth = 100;
        const int height = 80;
        int stage = CenterRoi.StageForTarget(targetIndex, stages);
        Assert.Equal(expectedStage, stage);

        RegionOfInterest roi = CenterRoi.Compute(segmentWidth * stages, height, segmentWidth, stage);

        // 調整対象の段の中央に、その段の1/4の面積で置く
        Assert.Equal(new RegionOfInterest(stage * segmentWidth + 25, 20, 50, 40), roi);
        Assert.True(roi.X >= stage * segmentWidth);
        Assert.True(roi.X + roi.Width <= (stage + 1) * segmentWidth);
    }
}
