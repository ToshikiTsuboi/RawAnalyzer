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

    /// <summary>
    /// 行交互HDR(RGGB・12bit・ライン単位は既定の2)の画像。段 s の行はすべて code 100×(s+1)。
    /// 行 y の段は (y mod (段数×2)) / 2(HdrSplitter の行の振り分けと同じ)。
    /// </summary>
    internal static RawImage MakeLineInterleavedHdr(int size, int stages)
    {
        var codes = new ushort[size * size];
        for (int y = 0; y < size; y++)
        {
            int stage = y % (stages * 2) / 2;
            Array.Fill(codes, (ushort)(100 * (stage + 1)), y * size, size);
        }

        return TestImages.FromCodes(codes, new RawFormat
        {
            Width = size, Height = size, BitDepth = 12, Bayer = BayerPattern.Rggb,
            Hdr = HdrMode.LineInterleaved, HdrStages = stages, ExposureRatio = 16,
        });
    }

    [Fact]
    public void ComputeHistogram_SampledLineInterleavedHdr_IncludesEveryStage()
    {
        // 回帰テスト: 刻みは Bayer の偶奇だけを見て奇数にしていたので、3段・ライン単位2(周期6行)で
        // 刻み3になると行 0,3,6,9… の段は 0,1,0,1… となり、最短秒の段2が1行も入らなかった。
        // 60x60 / maxSamples=400 で素の刻みは3。周期と互いに素な5へ上げ、12行が各段4行ずつ入る
        using RawImage image = MakeLineInterleavedHdr(60, stages: 3);

        HistogramResult result = ImageAnalysis.ComputeHistogram(image, 0, maxSamples: 400);

        Assert.True(result.IsSampled);
        Assert.True(result.Bins[100] > 0);
        Assert.True(result.Bins[200] > 0);
        Assert.True(result.Bins[300] > 0);
        Assert.Equal(result.Bins[100], result.Bins[300]);
        Assert.Equal(200, result.Statistics.Mean, 10);
    }

    [Fact]
    public void ComputeStatistics_SampledLineInterleavedHdr_IncludesEveryStage()
    {
        // ROI 統計の間引きもヒストグラムと同じ基準(同じ回帰)
        using RawImage image = MakeLineInterleavedHdr(60, stages: 3);

        RegionStatistics stats = ImageAnalysis.ComputeStatistics(
            image, 0, new RegionOfInterest(0, 0, 60, 60), maxSamples: 400);

        Assert.True(stats.SampleCount < 3600);
        Assert.Equal(100, stats.Min);
        Assert.Equal(300, stats.Max);
        Assert.Equal(200, stats.Mean, 10);
    }

    [Theory]
    [InlineData(2, 40)] // 周期4行・素の刻み2ブロック → 行 4k,4k+1 は長秒だけだった
    [InlineData(3, 60)] // 周期6行・素の刻み3ブロック → 行 6k,6k+1 は長秒だけだった
    public void ComputeChannelAnalysis_SampledLineInterleavedHdr_IncludesEveryStage(int stages, int size)
    {
        // 回帰テスト: チャネル別の間引きは2x2ブロック単位で、刻み(ブロック数)が行交互の周期と公約数を持つと
        // 読む行(ブロックの上下2行)が特定の段に固定され、ヒストグラム・統計・飽和率が1つの露光だけの値になった
        using RawImage image = MakeLineInterleavedHdr(size, stages);

        ChannelAnalysisResult result = ImageAnalysis.ComputeChannelAnalysis(
            image, 0, BayerPattern.Rggb, maxSamples: 400);

        Assert.True(result.Total.IsSampled);
        for (int stage = 0; stage < stages; stage++)
        {
            Assert.True(result.Total.Bins[100 * (stage + 1)] > 0, $"段{stage}の行が集計されていない");
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
