using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class ImageAnalysisTests
{
    [Fact]
    public void ComputeStatistics_KnownData_MatchesExpected()
    {
        // 0..15 の4x4: mean=7.5, σ=sqrt(1240/16 - 56.25)=sqrt(21.25)
        // 12bit入力でも統計は raw code 域(16bit正規化値 <<4 ではない)で返ること
        ushort[] codes = Enumerable.Range(0, 16).Select(i => (ushort)i).ToArray();
        using RawImage image = TestImages.FromCodes(codes, 4, 4, bitDepth: 12);

        RegionStatistics stats = ImageAnalysis.ComputeStatistics(
            image, 0, new RegionOfInterest(0, 0, 4, 4));

        Assert.Equal(7.5, stats.Mean, 10);
        Assert.Equal(Math.Sqrt(21.25), stats.Sigma, 10);
        Assert.Equal(0, stats.Min);
        Assert.Equal(15, stats.Max);
        Assert.Equal(16, stats.SampleCount);
    }

    [Fact]
    public void ComputeStatistics_SubRegion_OnlyCountsRoi()
    {
        ushort[] codes = Enumerable.Range(0, 16).Select(i => (ushort)i).ToArray();
        using RawImage image = TestImages.FromCodes(codes, 4, 4);

        // 右下2x2 = {10,11,14,15}: mean=12.5
        RegionStatistics stats = ImageAnalysis.ComputeStatistics(
            image, 0, new RegionOfInterest(2, 2, 2, 2));

        Assert.Equal(12.5, stats.Mean, 10);
        Assert.Equal(10, stats.Min);
        Assert.Equal(15, stats.Max);
        Assert.Equal(4, stats.SampleCount);
    }

    [Fact]
    public void ComputeHistogram_BinsInRawCodeDomain()
    {
        ushort[] codes = { 0, 0, 100, 4095 };
        using RawImage image = TestImages.FromCodes(codes, 2, 2, bitDepth: 12);

        HistogramResult result = ImageAnalysis.ComputeHistogram(image, 0);

        Assert.Equal(1 << 12, result.Bins.Length);
        Assert.False(result.IsSampled);
        Assert.Equal(4, result.SampleCount);
        Assert.Equal(2u, result.Bins[0]);
        Assert.Equal(1u, result.Bins[100]);
        Assert.Equal(1u, result.Bins[4095]);
    }

    [Fact]
    public void ComputeHistogram_RoiRestrictsSamples()
    {
        ushort[] codes = Enumerable.Range(0, 16).Select(i => (ushort)i).ToArray();
        using RawImage image = TestImages.FromCodes(codes, 4, 4);

        HistogramResult result = ImageAnalysis.ComputeHistogram(
            image, 0, new RegionOfInterest(2, 2, 2, 2));

        Assert.Equal(4, result.SampleCount);
        Assert.Equal(1u, result.Bins[10]);
        Assert.Equal(0u, result.Bins[0]);
    }

    [Fact]
    public void ComputeHistogram_SampledBayerMosaic_CoversAllFourChannels()
    {
        // 回帰テスト: strideが偶数だと走査位置のx/y偶奇が固定され、
        // RGGBモザイクではRだけを拾って mean=500 / σ=0 という別物の統計になっていた。
        // 70x70 / maxSamples=400 のとき素のstrideは4(偶数) → 補正後5。
        const int size = 70;
        var codes = new ushort[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // RGGB: R=500, Gr=1000, Gb=1000, B=800 → 全体平均 825
                codes[y * size + x] = (y & 1) == 0
                    ? (ushort)((x & 1) == 0 ? 500 : 1000)
                    : (ushort)((x & 1) == 0 ? 1000 : 800);
            }
        }

        using RawImage image = TestImages.FromCodes(codes, size, size, bitDepth: 12);

        HistogramResult result = ImageAnalysis.ComputeHistogram(image, 0, maxSamples: 400);

        Assert.True(result.IsSampled);

        // stride=5 で 14行×14列 → 4チャネルが49サンプルずつ均等に含まれる
        Assert.Equal(196, result.SampleCount);
        Assert.Equal(49u, result.Bins[500]);
        Assert.Equal(98u, result.Bins[1000]);
        Assert.Equal(49u, result.Bins[800]);
        Assert.Equal(825, result.Statistics.Mean, 10);
        Assert.Equal(Math.Sqrt(41875), result.Statistics.Sigma, 10);
        Assert.Equal(500, result.Statistics.Min);
        Assert.Equal(1000, result.Statistics.Max);
    }

    [Fact]
    public void ComputeStatistics_CancelledToken_ThrowsOperationCanceled()
    {
        // ParallelOptions.CancellationToken を渡していないと
        // AggregateException に包まれ catch(OperationCanceledException) をすり抜ける
        ushort[] codes = new ushort[64 * 64];
        using RawImage image = TestImages.FromCodes(codes, 64, 64, bitDepth: 12);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => ImageAnalysis.ComputeStatistics(
                image, 0, new RegionOfInterest(0, 0, 64, 64), cts.Token));
    }

    [Fact]
    public void ExtractRowProfile_MatchesPixels()
    {
        ushort[] codes = TestData.MakePattern(8 * 4, 12);
        using RawImage image = TestImages.FromCodes(codes, 8, 4, bitDepth: 12);

        ushort[] profile = ImageAnalysis.ExtractRowProfile(image, 0, 2);

        Assert.Equal(8, profile.Length);
        for (int x = 0; x < 8; x++)
        {
            Assert.Equal(codes[2 * 8 + x], profile[x]);
        }
    }

    [Fact]
    public void ExtractColumnProfile_MatchesPixels()
    {
        ushort[] codes = TestData.MakePattern(8 * 4, 12);
        using RawImage image = TestImages.FromCodes(codes, 8, 4, bitDepth: 12);

        ushort[] profile = ImageAnalysis.ExtractColumnProfile(image, 0, 3);

        Assert.Equal(4, profile.Length);
        for (int y = 0; y < 4; y++)
        {
            Assert.Equal(codes[y * 8 + 3], profile[y]);
        }
    }

    [Fact]
    public void RegionOfInterest_Clamp_LimitsToImageBounds()
    {
        var roi = new RegionOfInterest(-5, -5, 100, 100).Clamp(10, 8);
        Assert.Equal(new RegionOfInterest(0, 0, 10, 8), roi);

        var outside = new RegionOfInterest(20, 20, 5, 5).Clamp(10, 8);
        Assert.Equal(0, outside.PixelCount);
    }
}
