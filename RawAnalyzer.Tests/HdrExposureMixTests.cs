using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// Raw表示のHDR素材で、露光の違う行を1つの母集団として扱う解析(欠陥検出・ノイズ測定)と、
/// Bayer の位相が一致しない行交互HDRの Bayer 系の表示・チャネル別集計を断る規約。
/// </summary>
public class HdrExposureMixTests
{
    private static RawFormat Format(HdrMode hdr, int frameCount = 1, int stages = 2) => new()
    {
        Width = 8, Height = 8, BitDepth = 12, Bayer = BayerPattern.Rggb, Hdr = hdr,
        FrameCount = frameCount, HdrStages = stages,
    };

    [Theory]
    [InlineData(HdrMode.LineInterleaved, 1, true)]
    [InlineData(HdrMode.LineInterleaved, 4, true)]   // 行交互の連続撮影も1フレームに全露光
    [InlineData(HdrMode.Auto, 1, true)]              // Auto でフレーム数1は行交互
    [InlineData(HdrMode.Auto, 2, false)]             // Auto でフレーム数=段数はフレーム連結(1フレーム=1露光)
    [InlineData(HdrMode.FrameSequential, 2, false)]
    [InlineData(HdrMode.None, 1, false)]
    [InlineData(HdrMode.Auto, 5, false)]             // レイアウトを決められない指定は従来どおり(分割もできない)
    public void InFrame_IsTrueOnlyForLineInterleaved(HdrMode hdr, int frameCount, bool expected)
    {
        Assert.Equal(expected, HdrExposureMix.InFrame(Format(hdr, frameCount)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Noise_LineInterleavedRawView_IsRefusedWhateverTheRoi(bool wholeImage)
    {
        RoiAnalysisTarget target = wholeImage
            ? new WholeImageTarget()
            : new SourceRoiTarget(new RegionOfInterest(2, 2, 4, 4));

        // 行交互HDRの Raw 表示では、どの ROI にも長秒と短秒の行が交互に入り、露光差(行ごとの段差)が
        // σ_total・σ_FPN・DR に乗る。以前は HDR を見ずに、もっともらしい値を警告なしで出していた
        string? reason = HdrExposureMix.NoiseRefusal(
            lineInterleavedRawView: true, splitSegmentWidth: 0, target);

        Assert.NotNull(reason);
        Assert.Contains("分割ビュー", reason);
    }

    [Fact]
    public void HorizontalProjection_LineInterleavedRawView_IsRefusedWithSplitViewGuidance()
    {
        // 行交互HDRの Raw 表示の水平射影(各列を縦に平均)は、どの列にも長秒と短秒の行が交互に入り、露光差が列ごとの
        // 平均・最大・最小に乗る。ノイズ測定・欠陥検出と同じく理由を示して分割ビューへ案内する。分割ビュー・
        // フレーム連結の Raw 表示(1フレーム=1露光)は断らない
        string? reason = HdrExposureMix.HorizontalProjectionRefusal(lineInterleavedRawView: true);

        Assert.NotNull(reason);
        Assert.StartsWith("行交互HDRのrawは、Raw表示のままでは水平射影を取れません", reason);
        Assert.Contains("HDR分割ビュー(Ctrl+5)にすると露光ごとに射影できます", reason);
        Assert.Contains("垂直射影", reason);
        Assert.Null(HdrExposureMix.HorizontalProjectionRefusal(lineInterleavedRawView: false));
    }

    [Theory]
    [InlineData(90, 0, 20, 10, true)]     // 段の境界(x=100)をまたぐ
    [InlineData(10, 5, 80, 40, false)]    // 長秒の段の中
    [InlineData(100, 5, 100, 40, false)]  // 短秒の段の中(右端まで)
    public void Noise_SplitView_RequiresRoiInsideOneStage(int x, int y, int width, int height, bool refused)
    {
        // 分割ビューは長秒(左)と短秒(右)を並べた1枚。段をまたぐと露光差が σ に乗る
        var target = new SourceRoiTarget(new RegionOfInterest(x, y, width, height));

        string? reason = HdrExposureMix.NoiseRefusal(
            lineInterleavedRawView: false, splitSegmentWidth: 100, target);

        Assert.Equal(refused, reason is not null);
    }

    [Fact]
    public void Noise_SplitView_WholeImage_IsRefusedAndGuidedToRoi()
    {
        // 「ROI内のみ」なし(画像全体)は両方の段を含み、平均も2露光の中間になる
        string? reason = HdrExposureMix.NoiseRefusal(
            lineInterleavedRawView: false, splitSegmentWidth: 100, new WholeImageTarget());

        Assert.NotNull(reason);
        Assert.Contains("ROI", reason);
    }

    [Fact]
    public void Noise_SplitViewChannelGrid_IsCheckedByItsSourceExtent()
    {
        // チャネル分割表示の1象限の ROI は元画像では2画素刻みの格子。格子の右端の画素で段をまたぐかを見る
        var inside = new ChannelRoiTarget(
            new ChannelRegion(90, 0, 5, 4), BayerChannel.R, new RegionOfInterest(45, 0, 5, 4));  // x=90..98
        var across = new ChannelRoiTarget(
            new ChannelRegion(90, 0, 6, 4), BayerChannel.R, new RegionOfInterest(45, 0, 6, 4));  // x=90..100

        Assert.Null(HdrExposureMix.NoiseRefusal(false, 100, inside));
        Assert.NotNull(HdrExposureMix.NoiseRefusal(false, 100, across));
    }

    [Fact]
    public void Noise_OrdinaryImage_IsMeasured()
    {
        Assert.Null(HdrExposureMix.NoiseRefusal(false, 0, new WholeImageTarget()));
    }

    [Theory]
    [InlineData(HdrMode.LineInterleaved, 1, 0, 0, BayerPattern.Rggb, false)]   // 既定(Bayer ありはライン単位2)
    [InlineData(HdrMode.LineInterleaved, 1, 2, 0, BayerPattern.Rggb, false)]
    [InlineData(HdrMode.LineInterleaved, 1, 2, 2, BayerPattern.Rggb, false)]
    [InlineData(HdrMode.LineInterleaved, 1, 4, -2, BayerPattern.Bggr, false)]
    [InlineData(HdrMode.LineInterleaved, 1, 1, 0, BayerPattern.Rggb, true)]    // ライン単位1
    [InlineData(HdrMode.LineInterleaved, 1, 3, 0, BayerPattern.Grbg, true)]    // ライン単位3
    [InlineData(HdrMode.LineInterleaved, 1, 2, 1, BayerPattern.Rggb, true)]    // 奇数の行オフセット
    [InlineData(HdrMode.LineInterleaved, 1, 0, -1, BayerPattern.Rggb, true)]
    [InlineData(HdrMode.Auto, 1, 1, 0, BayerPattern.Rggb, true)]               // Auto でフレーム数1は行交互
    [InlineData(HdrMode.LineInterleaved, 1, 1, 0, BayerPattern.None, false)]   // Bayer なしは色を使わない
    [InlineData(HdrMode.FrameSequential, 2, 1, 1, BayerPattern.Rggb, false)]   // フレーム連結は1フレーム=1露光
    [InlineData(HdrMode.None, 1, 1, 1, BayerPattern.Rggb, false)]
    public void BayerPhaseMismatch_IsTrueOnlyForOddLineBlockOrRowOffsetOfLineInterleaved(
        HdrMode hdr, int frameCount, int lineBlock, int rowOffset, BayerPattern bayer, bool expected)
    {
        // 検証 r2-render-rawview-hdr-phase-1。行交互HDRのファイル行の偶奇がセンサ行の偶奇と一致するのは、
        // ライン単位と行オフセットがともに偶数のときだけ(既定の場合の挙動は変えない)
        RawFormat format = Format(hdr, frameCount) with
        {
            Bayer = bayer, HdrLineBlock = lineBlock, HdrRowOffset = rowOffset,
        };

        Assert.Equal(expected, HdrExposureMix.BayerPhaseMismatch(format));
    }

    [Fact]
    public void ChannelHistogram_MismatchedPhase_IsRefusedWithSplitViewGuidance()
    {
        // チャネル別ヒストグラムはファイル行の偶奇でチャネルへ振り分けるので、位相が一致しないと R に Gb の値が混ざる
        // (8×32・ライン単位1で R に 19000 が16画素)。理由を示して分割ビューへ案内する
        string? reason = HdrExposureMix.ChannelHistogramRefusal(bayerPhaseMismatch: true);

        Assert.NotNull(reason);
        Assert.StartsWith("行交互HDRのrawは、ライン単位か行オフセットが奇数のため、Raw表示のままではチャネル別ヒストグラムを取れません", reason);
        Assert.Contains("R↔Gb・Gr↔B", reason);
        Assert.Contains("HDR分割ビュー(Ctrl+5)", reason);
        Assert.Null(HdrExposureMix.ChannelHistogramRefusal(bayerPhaseMismatch: false));
        Assert.Null(HdrExposureMix.BayerDisplayRefusal(bayerPhaseMismatch: false));
    }

    [Fact]
    public void WhiteBalance_MismatchedPhaseIsRefused_ButSplitFramesAreUsable()
    {
        RawFormat format = Format(HdrMode.LineInterleaved) with { HdrLineBlock = 1 };
        using RawImage source = TestImages.FromCodes(new ushort[format.Width * format.Height], format);
        string? reason = HdrExposureMix.WhiteBalanceRefusal(HdrExposureMix.BayerPhaseMismatch(format));
        Assert.NotNull(reason);
        Assert.Contains("自動WB", reason);
        Assert.Contains("スポイト", reason);
        Assert.Contains("Ctrl+5", reason);

        IReadOnlyList<RawImage> frames = HdrSplitter.Split(source);
        try
        {
            Assert.All(frames, frame => Assert.Null(
                HdrExposureMix.WhiteBalanceRefusal(HdrExposureMix.BayerPhaseMismatch(frame.Format))));
        }
        finally
        {
            foreach (RawImage frame in frames) frame.Dispose();
        }
    }

    [Fact]
    public void DefectDetection_LineInterleavedRawView_IsRefusedAndGuidedToSplitView()
    {
        // 行交互HDRの Raw 表示では同じ Bayer チャネルに長秒と短秒の行が交互に入り、チャネル別の mean±Nσ が
        // 混合分布から求まる(露光比4のフラットで σ≈750)。閾値が値域の外へ出て、黒点が0件になっていた
        string? reason = HdrExposureMix.DefectDetectionRefusal(
            Format(HdrMode.LineInterleaved), derivedViewShown: false);

        Assert.NotNull(reason);
        Assert.Contains("分割ビュー", reason);

        // 分割ビュー(段ごとに統計を取る)・合成ビュー・フレーム連結・HDRでない raw は検出できる
        Assert.Null(HdrExposureMix.DefectDetectionRefusal(Format(HdrMode.LineInterleaved), derivedViewShown: true));
        Assert.Null(HdrExposureMix.DefectDetectionRefusal(Format(HdrMode.FrameSequential, 2), derivedViewShown: false));
        Assert.Null(HdrExposureMix.DefectDetectionRefusal(Format(HdrMode.None), derivedViewShown: false));
    }
}
