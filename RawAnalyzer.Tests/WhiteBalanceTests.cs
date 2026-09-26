using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class WhiteBalanceTests
{
    private static RawImage MakeMosaic(BayerPattern pattern, ushort r, ushort g, ushort b)
    {
        const int size = 8;
        ushort[] mosaic = ColorPipelineTests.BuildConstantMosaic(size, size, pattern, r, g, b);
        return TestImages.FromCodes(mosaic, size, size, bayer: pattern);
    }

    [Theory]
    [InlineData(BayerPattern.Rggb)]
    [InlineData(BayerPattern.Bggr)]
    public void ComputeGrayWorld_ConstantChannels_ComputesInverseGains(BayerPattern pattern)
    {
        using RawImage image = MakeMosaic(pattern, r: 1000, g: 2000, b: 4000);

        WhiteBalanceGains gains = WhiteBalance.ComputeGrayWorld(image, 0, pattern);

        Assert.Equal(2.0, gains.GainR, 10);
        Assert.Equal(0.5, gains.GainB, 10);
    }

    [Fact]
    public void ComputeSpotGains_OddSizedImage_KeepsBayerPhaseAtLastColumn()
    {
        // クランプ上限が奇数のままだとブロックが1画素ずれ、
        // 最終列/最終行で R/B が緑画素から算出される
        const int width = 7;
        const int height = 7;
        ushort[] mosaic = ColorPipelineTests.BuildConstantMosaic(
            width, height, BayerPattern.Rggb, r: 500, g: 1000, b: 2000);
        using RawImage image = TestImages.FromCodes(
            mosaic, width, height, bayer: BayerPattern.Rggb);

        WhiteBalanceGains gains = WhiteBalance.ComputeSpotGains(
            image, 0, BayerPattern.Rggb, width - 1, height - 1);

        Assert.Equal(2.0, gains.GainR, 10);
        Assert.Equal(0.5, gains.GainB, 10);
    }

    [Fact]
    public void ComputeSpotGains_TinyImage_ReturnsUnity()
    {
        // 1画素幅では2x2ブロックが取れず GetPixel(1, ...) が範囲外例外になっていた
        using RawImage image = TestImages.FromCodes(
            new ushort[] { 1234 }, 1, 1, bayer: BayerPattern.Rggb);

        WhiteBalanceGains gains = WhiteBalance.ComputeSpotGains(
            image, 0, BayerPattern.Rggb, 0, 0);

        Assert.Equal(1.0, gains.GainR);
        Assert.Equal(1.0, gains.GainB);
    }
}
