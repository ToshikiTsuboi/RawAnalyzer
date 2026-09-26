using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class WhiteBalanceTests
{
    private static RawImage LoadMosaic(BayerPattern pattern, ushort r, ushort g, ushort b)
    {
        const int size = 8;
        ushort[] mosaic = ColorPipelineTests.BuildConstantMosaic(size, size, pattern, r, g, b);
        var format = new RawFormat { Width = size, Height = size, BitDepth = 16, Bayer = pattern };
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(mosaic, format));
        try
        {
            return RawLoader.Load(path, format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(BayerPattern.Rggb)]
    [InlineData(BayerPattern.Bggr)]
    public void ComputeGrayWorld_ConstantChannels_ComputesInverseGains(BayerPattern pattern)
    {
        using RawImage image = LoadMosaic(pattern, r: 1000, g: 2000, b: 4000);

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
        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16, Bayer = BayerPattern.Rggb,
        };
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(mosaic, format));
        try
        {
            using RawImage image = RawLoader.Load(path, format);

            WhiteBalanceGains gains = WhiteBalance.ComputeSpotGains(
                image, 0, BayerPattern.Rggb, width - 1, height - 1);

            Assert.Equal(2.0, gains.GainR, 10);
            Assert.Equal(0.5, gains.GainB, 10);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ComputeSpotGains_TinyImage_ReturnsUnity()
    {
        // 1画素幅では2x2ブロックが取れず GetPixel(1, ...) が範囲外例外になっていた
        var format = new RawFormat
        {
            Width = 1, Height = 1, BitDepth = 16, Bayer = BayerPattern.Rggb,
        };
        string path = TestData.WriteTempFile(
            TestData.EncodeRawFile(new ushort[] { 1234 }, format));
        try
        {
            using RawImage image = RawLoader.Load(path, format);

            WhiteBalanceGains gains = WhiteBalance.ComputeSpotGains(
                image, 0, BayerPattern.Rggb, 0, 0);

            Assert.Equal(1.0, gains.GainR);
            Assert.Equal(1.0, gains.GainB);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
