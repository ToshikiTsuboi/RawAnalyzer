using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 表示中のROIを「実際に集計する画素」へ対応づける処理と、その集計。
/// チャネル分割表示のROIはタイル座標なので、元画像の矩形として読んではいけない。
/// </summary>
public class RoiAnalysisTests
{
    [Fact]
    public void PopulationCount_IsPixelCountOfAnalyzedSet()
    {
        // ヒストグラムの表(CSV)に書く「対象の画素数」。ROIなしは1フレームの全画素
        Assert.Equal(4000L * 3000, RoiAnalysis.PopulationCount(new WholeImageTarget(), 4000, 3000));
        Assert.Equal(
            12L, RoiAnalysis.PopulationCount(new SourceRoiTarget(new RegionOfInterest(1, 2, 3, 4)), 4000, 3000));
        Assert.Equal(
            6L,
            RoiAnalysis.PopulationCount(
                new ChannelRoiTarget(new ChannelRegion(0, 0, 2, 3), BayerChannel.R, new RegionOfInterest(0, 0, 2, 3)),
                4000, 3000));
        Assert.Equal(0L, RoiAnalysis.PopulationCount(new UnsupportedRoiTarget("x"), 4000, 3000));
    }

    [Fact]
    public void ChannelSplit_RoiOnRTile_ReportsOnlyRPixels()
    {
        // 4×4 RGGB、R=1000 / G=2000 / B=4000。分割表示の左上2×2はRの4画素(すべて1000)。
        // 以前は同じ矩形を元画像の(0,0)-(1,1)として読み、R/Gr/Gb/Bの平均2250を返していた
        using RawImage image = Rggb4x4();
        var displayRoi = new RegionOfInterest(0, 0, 2, 2);

        RoiAnalysisTarget target = RoiAnalysis.Resolve(
            displayRoi, channelSplitLayout: true, 4, 4, BayerPattern.Rggb);
        RoiHistogram result = RoiAnalysis.ComputeHistogram(
            image, 0, target, BayerPattern.Rggb, byChannel: false, CancellationToken.None);

        ChannelRoiTarget channel = Assert.IsType<ChannelRoiTarget>(target);
        Assert.Equal(BayerChannel.R, channel.Channel);
        Assert.Equal(new RegionStatistics(1000, 0, 1000, 1000, 4), result.RoiStatistics);
        Assert.Equal(4, result.Histogram.Bins[1000]);
        Assert.Equal(4, result.Histogram.SampleCount);
    }

    [Fact]
    public void RawLayout_SameRoi_IsSourceRectangle()
    {
        // 通常表示では表示座標=元画像座標なので、同じ矩形は4チャネルを含む
        using RawImage image = Rggb4x4();

        RoiAnalysisTarget target = RoiAnalysis.Resolve(
            new RegionOfInterest(0, 0, 2, 2), channelSplitLayout: false, 4, 4, BayerPattern.Rggb);
        RoiHistogram result = RoiAnalysis.ComputeHistogram(
            image, 0, target, BayerPattern.Rggb, byChannel: false, CancellationToken.None);

        Assert.Equal(new SourceRoiTarget(new RegionOfInterest(0, 0, 2, 2)), target);
        Assert.Equal(2250, result.RoiStatistics!.Value.Mean);
    }

    [Fact]
    public void ChannelSplit_EachQuadrant_MapsToItsChannel()
    {
        // 8×6 → 象限 4×3。各象限の内側1画素の矩形。GBRG の象限は左上 Gb / 右上 B / 左下 R / 右下 Gr
        // (パターンごとの表そのものは BayerHelperTests が固定している)
        BayerChannel[,] expected =
        {
            { BayerChannel.Gb, BayerChannel.B },
            { BayerChannel.R, BayerChannel.Gr },
        };
        for (int quadY = 0; quadY < 2; quadY++)
        {
            for (int quadX = 0; quadX < 2; quadX++)
            {
                var roi = new RegionOfInterest(quadX * 4 + 1, quadY * 3 + 1, 2, 2);

                RoiAnalysisTarget target = RoiAnalysis.Resolve(roi, true, 8, 6, BayerPattern.Gbrg);

                ChannelRoiTarget channel = Assert.IsType<ChannelRoiTarget>(target);
                Assert.Equal(expected[quadY, quadX], channel.Channel);
                Assert.Equal(new ChannelRegion(2 + quadX, 2 + quadY, 2, 2), channel.Region);
                Assert.Equal(roi, channel.DisplayRoi);
            }
        }
    }

    [Theory]
    [InlineData(2, 1, 4, 4)]  // 4象限の中央(中央ROI)
    public void ChannelSplit_RoiAcrossQuadrants_IsRejected(int x, int y, int width, int height)
    {
        RoiAnalysisTarget target = RoiAnalysis.Resolve(
            new RegionOfInterest(x, y, width, height), true, 8, 6, BayerPattern.Rggb);

        UnsupportedRoiTarget unsupported = Assert.IsType<UnsupportedRoiTarget>(target);
        Assert.Contains("象限", unsupported.Reason);
    }

    [Fact]
    public void ChannelSplit_OddSizedImage_UsesOnlyDisplayedTiles()
    {
        // 5×5 は 4×4 のタイルとして表示される(最終行・列は並ばない)
        RoiAnalysisTarget outside = RoiAnalysis.Resolve(
            new RegionOfInterest(4, 0, 1, 5), true, 5, 5, BayerPattern.Rggb);
        RoiAnalysisTarget partial = RoiAnalysis.Resolve(
            new RegionOfInterest(3, 3, 2, 2), true, 5, 5, BayerPattern.Rggb);

        Assert.IsType<UnsupportedRoiTarget>(outside);
        ChannelRoiTarget channel = Assert.IsType<ChannelRoiTarget>(partial);
        Assert.Equal(new ChannelRegion(3, 3, 1, 1), channel.Region);
        Assert.Equal(new RegionOfInterest(3, 3, 1, 1), channel.DisplayRoi);
        Assert.Equal(BayerChannel.B, channel.Channel);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoRoi_IsWholeImage(bool channelSplitLayout)
    {
        Assert.IsType<WholeImageTarget>(
            RoiAnalysis.Resolve(null, channelSplitLayout, 8, 6, BayerPattern.Rggb));
    }

    [Fact]
    public void ChannelTarget_ByChannelHistogram_IsThatSingleChannel()
    {
        // 1チャネルだけのROIに4チャネルの内訳は作らない(全体=そのチャネル)
        using RawImage image = Rggb4x4();
        RoiAnalysisTarget target = RoiAnalysis.Resolve(
            new RegionOfInterest(2, 2, 2, 2), true, 4, 4, BayerPattern.Rggb);

        RoiHistogram result = RoiAnalysis.ComputeHistogram(
            image, 0, target, BayerPattern.Rggb, byChannel: true, CancellationToken.None);

        Assert.Null(result.Channels);
        Assert.Equal(4000, result.Histogram.Statistics.Mean);
    }

    [Fact]
    public void WholeImage_ByChannel_KeepsFourChannels()
    {
        using RawImage image = Rggb4x4();

        RoiHistogram result = RoiAnalysis.ComputeHistogram(
            image, 0, RoiAnalysis.Resolve(null, true, 4, 4, BayerPattern.Rggb),
            BayerPattern.Rggb, byChannel: true, CancellationToken.None);

        Assert.NotNull(result.Channels);
        Assert.Equal(4, result.Channels!.Count);
        Assert.Null(result.RoiStatistics);
        Assert.Equal(16, result.Histogram.SampleCount);
    }

    [Fact]
    public void RawLayout_OddAlignedRoi_ByChannel_CountsOnlyRoiPixels()
    {
        // レビューの再現。6×6 RGGB、ROI=(1,1,4,4) の中は1000、外は65535。チャネル別の表示でも
        // ヒストグラム(中央値・飽和率の元)と各チャネルは同時に出す ROI 統計と同じ16画素から集計する。
        // 以前は2x2境界へ外側に広げた36画素を集計し、中央値65535・飽和55.56%(ROI 統計は最小・最大とも1000)
        var codes = new ushort[36];
        Array.Fill(codes, (ushort)65535);
        for (int y = 1; y <= 4; y++)
        {
            for (int x = 1; x <= 4; x++)
            {
                codes[y * 6 + x] = 1000;
            }
        }

        using RawImage image = TestImages.FromCodes(codes, 6, 6, 16, BayerPattern.Rggb);
        RoiAnalysisTarget target = RoiAnalysis.Resolve(
            new RegionOfInterest(1, 1, 4, 4), channelSplitLayout: false, 6, 6, BayerPattern.Rggb);

        RoiHistogram result = RoiAnalysis.ComputeHistogram(
            image, 0, target, BayerPattern.Rggb, byChannel: true, CancellationToken.None);
        HistogramMetrics metrics = ImageAnalysis.ComputeHistogramMetrics(result.Histogram);

        Assert.Equal(new RegionStatistics(1000, 0, 1000, 1000, 16), result.RoiStatistics);
        Assert.Equal(16, result.Histogram.SampleCount);
        Assert.Equal(16, result.Histogram.Bins[1000]);
        Assert.Equal(1000, metrics.Median);
        Assert.Equal(0, metrics.SaturatedPercent);
        Assert.All(result.Channels!, channel =>
            Assert.Equal(new RegionStatistics(1000, 0, 1000, 1000, 4), channel.Statistics));
    }

    [Fact]
    public void ChannelTarget_Noise_UsesOnlyThatChannel()
    {
        // Rは2枚とも一定(ノイズ0)、他チャネルは揺らぐ。R象限のROIならσはすべて0。
        // 同じ矩形を元画像の矩形として読むと他チャネルの揺らぎを拾う
        using RawImage imageA = NoisyExceptR(seed: 1);
        using RawImage imageB = NoisyExceptR(seed: 2);
        var displayRoi = new RegionOfInterest(0, 0, 4, 4);
        RoiAnalysisTarget split = RoiAnalysis.Resolve(displayRoi, true, 8, 8, BayerPattern.Rggb);
        RoiAnalysisTarget raw = RoiAnalysis.Resolve(displayRoi, false, 8, 8, BayerPattern.Rggb);

        NoiseMeasurement channel = RoiAnalysis.MeasureNoise(
            imageA, imageB, 0, split, BayerPattern.Rggb, 0, CancellationToken.None);
        NoiseMeasurement mixed = RoiAnalysis.MeasureNoise(
            imageA, imageB, 0, raw, BayerPattern.Rggb, 0, CancellationToken.None);

        Assert.Equal(16, channel.SampleCount);
        Assert.Equal(1000, channel.Mean);
        Assert.Equal(0, channel.SigmaTotal);
        Assert.Equal(0, channel.SigmaTemporal);
        Assert.True(mixed.SigmaTemporal > 0);
    }

    [Fact]
    public void ChannelTarget_SingleFrameNoise_UsesOnlyThatChannel()
    {
        using RawImage image = NoisyExceptR(seed: 3);
        RoiAnalysisTarget target = RoiAnalysis.Resolve(
            new RegionOfInterest(0, 0, 4, 4), true, 8, 8, BayerPattern.Rggb);

        NoiseMeasurement measurement = RoiAnalysis.MeasureNoise(
            image, null, 0, target, BayerPattern.Rggb, 0, CancellationToken.None);

        Assert.Equal(16, measurement.SampleCount);
        Assert.Equal(0, measurement.SigmaTotal);
        Assert.True(double.IsNaN(measurement.SigmaTemporal));
    }

    [Theory]
    [InlineData(6, 4, false)]   // 長秒(x 0〜7)と短秒(x 8〜15)をまたぐ
    [InlineData(0, 16, false)]  // 全幅
    [InlineData(0, 8, true)]    // 長秒の段ちょうど
    [InlineData(8, 4, true)]    // 短秒の段の中
    public void HdrSplitView_RoiAcrossSegments_IsRefusedWithReason(int x, int width, bool analyzable)
    {
        // 残課題 2026-10-02 A4。HDR分割ビュー(段の幅8を左右に2段並べた16×4)で、手動のROIが長秒と短秒の段を
        // またいでも、1つの母集団としてヒストグラム・ROI統計・射影を出していた(露光差が σ・平均に乗る)。
        // 段をまたぐROIは理由を示して断る(ノイズ測定は段内のROIを必須にしている。ROIなしの画像全体は従来どおり)
        RoiAnalysisTarget target = RoiAnalysis.Resolve(
            new RegionOfInterest(x, 0, width, 2), channelSplitLayout: false, 16, 4, BayerPattern.Rggb,
            splitSegmentWidth: 8);

        if (analyzable)
        {
            Assert.Equal(new SourceRoiTarget(new RegionOfInterest(x, 0, width, 2)), target);
        }
        else
        {
            Assert.Contains("段", Assert.IsType<UnsupportedRoiTarget>(target).Reason);
        }

        Assert.IsType<WholeImageTarget>(RoiAnalysis.Resolve(
            null, channelSplitLayout: false, 16, 4, BayerPattern.Rggb, splitSegmentWidth: 8));
    }

    [Theory]
    [InlineData(0, 8, false)]  // R の象限の全幅 = 元画像の x 0〜14(両方の段)
    [InlineData(0, 4, true)]   // 元画像の x 0〜6(長秒の段)
    [InlineData(4, 4, true)]   // 元画像の x 8〜14(短秒の段)
    [InlineData(3, 2, false)]  // 元画像の x 6〜8(段をまたぐ)
    public void HdrSplitView_ChannelSplitRoi_IsCheckedInSourceColumns(int x, int width, bool analyzable)
    {
        // 分割ビューをチャネル分割で表示したときは、象限の中のROIでも元画像(並置画像)の列で段を確かめる
        RoiAnalysisTarget target = RoiAnalysis.Resolve(
            new RegionOfInterest(x, 0, width, 2), channelSplitLayout: true, 16, 4, BayerPattern.Rggb,
            splitSegmentWidth: 8);

        if (analyzable)
        {
            Assert.IsType<ChannelRoiTarget>(target);
        }
        else
        {
            Assert.Contains("段", Assert.IsType<UnsupportedRoiTarget>(target).Reason);
        }
    }

    [Fact]
    public void UnsupportedTarget_AnalysisThrowsInsteadOfFallingBack()
    {
        // 黙って別の画素(画像全体など)を集計しない
        using RawImage image = Rggb4x4();
        RoiAnalysisTarget target = RoiAnalysis.Resolve(
            new RegionOfInterest(1, 1, 2, 2), true, 4, 4, BayerPattern.Rggb);

        Assert.Throws<InvalidOperationException>(() => RoiAnalysis.ComputeHistogram(
            image, 0, target, BayerPattern.Rggb, false, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => RoiAnalysis.MeasureNoise(
            image, null, 0, target, BayerPattern.Rggb, 0, CancellationToken.None));
    }

    private static RawImage Rggb4x4()
    {
        ushort[] codes =
        {
            1000, 2000, 1000, 2000,
            2000, 4000, 2000, 4000,
            1000, 2000, 1000, 2000,
            2000, 4000, 2000, 4000,
        };
        return TestImages.FromCodes(codes, 4, 4, 12, BayerPattern.Rggb);
    }

    private static RawImage NoisyExceptR(int seed)
    {
        var random = new Random(seed);
        var codes = new ushort[64];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                bool r = (x & 1) == 0 && (y & 1) == 0;
                codes[y * 8 + x] = (ushort)(r ? 1000 : 2000 + random.Next(-50, 51));
            }
        }

        return TestImages.FromCodes(codes, 8, 8, 12, BayerPattern.Rggb);
    }
}
