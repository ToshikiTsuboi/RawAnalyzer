using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 全画素が同じ値(飽和した 65535 など)の領域の σ が、領域の寸法によらず厳密に 0 になること。
/// </summary>
/// <remarks>
/// 回帰テスト: 分散を (double)ΣX²/n − ((double)ΣX/n)² で求めており、ΣX² = n·v² が 2^53 を超えると double への変換で
/// 丸まって σ ≈ 6.9e-4 が残った。σ=0 を「DR なし」とする判定をすり抜け、ROI 2419×3951 ではヒストグラム欄の DR が
/// 159.5 dB、ノイズ測定の DR が 165.57 dB と出ていた(ROI の寸法しだいで『—』/NaN になったりならなかったりした)。
/// </remarks>
public sealed class ConstantRegionVarianceTests : IClassFixture<ConstantRegionVarianceTests.SaturatedImage>
{
    private readonly RawImage _image;

    public ConstantRegionVarianceTests(SaturatedImage fixture)
    {
        _image = fixture.Image;
    }

    [Fact]
    public void ComputeHistogram_SaturatedRoi_SigmaAndDynamicRangeAreZero()
    {
        HistogramResult result = ImageAnalysis.ComputeHistogram(_image, 0);

        Assert.False(result.IsSampled);
        Assert.Equal(65535, result.Statistics.Mean);
        Assert.Equal(0, result.Statistics.Sigma);
        Assert.Equal(0, ImageAnalysis.ComputeHistogramMetrics(result).DynamicRangeDb);
    }

    [Fact]
    public void ComputeStatistics_SaturatedRoi_SigmaIsZero()
    {
        RegionStatistics stats = ImageAnalysis.ComputeStatistics(
            _image, 0, new RegionOfInterest(0, 0, _image.Width, _image.Height));

        Assert.Equal(65535, stats.Mean);
        Assert.Equal(0, stats.Sigma);
    }

    [Fact]
    public void ComputeChannelAnalysis_SaturatedRoi_SigmasAreZero()
    {
        ChannelAnalysisResult result = ImageAnalysis.ComputeChannelAnalysis(_image, 0, BayerPattern.Rggb);

        Assert.Equal(0, result.Total.Statistics.Sigma);
        Assert.All(result.Channels, channel => Assert.Equal(0, channel.Statistics.Sigma));
    }

    [Theory]
    [InlineData(BayerPattern.Rggb)]
    [InlineData(BayerPattern.None)]
    public void MeasureSingle_SaturatedRoi_SigmaIsZeroAndDynamicRangeIsNaN(BayerPattern pattern)
    {
        NoiseMeasurement noise = NoiseAnalysis.MeasureSingle(_image, pattern: pattern);

        Assert.Equal(0, noise.SigmaTotal);
        Assert.True(double.IsNaN(noise.DynamicRangeTotalDb));
    }

    [Fact]
    public void ChannelRegion_SaturatedLattice_SigmaIsZero()
    {
        var region = new ChannelRegion(0, 0, 1210, 1976); // R の格子の全体

        HistogramResult histogram = ChannelRegionAnalysis.ComputeHistogram(_image, 0, region);
        NoiseMeasurement noise = ChannelRegionAnalysis.MeasureSingle(_image, 0, region);

        Assert.Equal(0, histogram.Statistics.Sigma);
        Assert.Equal(0, noise.SigmaTotal);
    }

    [Theory]
    [InlineData(1L, 65535L)]
    [InlineData(9_557_469L, 65535L)] // 2419×3951
    [InlineData(13_827_874L, 65535L)] // 3637×3802
    [InlineData(1_000_000_007L, 65535L)] // 10億画素を超える奇数
    [InlineData(4_000_000L, -65535L)] // 2枚の差分(負)
    public void PopulationVariance_ConstantValues_IsExactlyZero(long count, long value)
    {
        UInt128 sumOfSquares = (UInt128)(ulong)count * (ulong)(value * value);

        Assert.Equal(0, Moments.PopulationVariance(count, count * value, sumOfSquares));
    }

    [Fact]
    public void PopulationVariance_KnownValues()
    {
        // 0..15: 母分散 = 1240/16 − 7.5² = 21.25。画素0個は0
        Assert.Equal(21.25, Moments.PopulationVariance(16, 120, 1240), 12);
        Assert.Equal(0, Moments.PopulationVariance(0, 0, 0));

        // 差分 {−3, 1}: 平均 −1、母分散 4
        Assert.Equal(4, Moments.PopulationVariance(2, -2, 10), 12);
    }

    /// <summary>16bit・RGGB・2419×3951 で全画素 65535 の画像(テストクラスで共有する)。</summary>
    public sealed class SaturatedImage : IDisposable
    {
        public SaturatedImage()
        {
            var format = new RawFormat
            {
                Width = 2419, Height = 3951, BitDepth = 16, Bayer = BayerPattern.Rggb,
            };
            var pixels = new ushort[format.Width * format.Height];
            Array.Fill(pixels, ushort.MaxValue);
            Image = RawImage.FromPixels(format, pixels);
        }

        public RawImage Image { get; }

        public void Dispose() => Image.Dispose();
    }
}
