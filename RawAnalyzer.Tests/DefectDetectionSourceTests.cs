using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 欠陥検出の結果を採用・補正に使ってよいか(検出した画像・フレーム・Bayerパターンのままか)の判定。
/// </summary>
/// <remarks>
/// MainWindow は検出結果の採用時と補正時にこの判定で一致を確かめ、右パネルで Bayer パターンを
/// 変えたら一覧を破棄する(HDR表示中は表示中の派生ビューのパターンが変わらないので残す)。
/// </remarks>
public class DefectDetectionSourceTests
{
    [Fact]
    public void BayerChangedAfterDetection_ResultIsNoLongerCurrent()
    {
        // レビューの再現。Bayer「なし」の全画素統計で白点を1.5σで検出すると、値の高い B の3画素が欠陥になるが、
        // 正しい RGGB で検出し直すと欠陥はない。右パネルで RGGB へ変えた後も前の一覧を補正に使えると、
        // RGGB の近傍(同じ B)で正常な B 画素を書き換える((3,1) が 11000 → 10000、保存にも出る)
        using RawImage image = TestImages.FromCodes(new ushort[]
        {
            1000, 2000, 1000, 2000,
            2000, 10000, 2000, 11000,
            1000, 2000, 1000, 2000,
            2000, 12000, 2000, 13000,
        }, 4, 4);
        DefectDetectionResult none = DefectPixelDetector.Detect(
            image, 0, 1.5, detectHot: true, detectDead: false, pattern: BayerPattern.None);
        DefectDetectionResult rggb = DefectPixelDetector.Detect(
            image, 0, 1.5, detectHot: true, detectDead: false, pattern: BayerPattern.Rggb);
        using RawImage stale = DefectCorrector.Correct(image, none.Defects, BayerPattern.Rggb);
        Assert.Equal(3, none.Defects.Count);
        Assert.Empty(rggb.Defects);
        Assert.Equal(11000, image.GetPixel(3, 1));
        Assert.Equal(10000, stale.GetPixel(3, 1));

        var source = new DefectDetectionSource(image, 0, BayerPattern.None);

        Assert.True(source.IsCurrent(image, 0, BayerPattern.None));
        Assert.False(source.IsCurrent(image, 0, BayerPattern.Rggb));
    }

    [Fact]
    public void OtherImageOrFrame_IsNotCurrent()
    {
        // 送り・差し替え・HDR表示の出入りの後の一覧は、配列の範囲内でも別の画素を指す
        var format = new RawFormat { Width = 2, Height = 2, FrameCount = 2, Bayer = BayerPattern.Rggb };
        using RawImage image = TestImages.FromCodes(new ushort[8], format);
        using RawImage other = TestImages.FromCodes(new ushort[8], format);
        var source = new DefectDetectionSource(image, 1, BayerPattern.Rggb);

        Assert.True(source.IsCurrent(image, 1, BayerPattern.Rggb));
        Assert.False(source.IsCurrent(image, 0, BayerPattern.Rggb));
        Assert.False(source.IsCurrent(other, 1, BayerPattern.Rggb));
        Assert.False(source.IsCurrent(null, 1, BayerPattern.Rggb));
    }
}
