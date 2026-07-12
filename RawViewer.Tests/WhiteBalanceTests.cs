using RawViewer.Core;
using Xunit;

namespace RawViewer.Tests;

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
    [InlineData(BayerPattern.Grbg)]
    [InlineData(BayerPattern.Gbrg)]
    public void ComputeGrayWorld_ConstantChannels_ComputesInverseGains(BayerPattern pattern)
    {
        using RawImage image = LoadMosaic(pattern, r: 1000, g: 2000, b: 4000);

        WhiteBalanceGains gains = WhiteBalance.ComputeGrayWorld(image, 0, pattern);

        Assert.Equal(2.0, gains.GainR, 10);
        Assert.Equal(0.5, gains.GainB, 10);
    }

    [Fact]
    public void ComputeSpotGains_UsesBlockContainingPixel()
    {
        using RawImage image = LoadMosaic(BayerPattern.Rggb, r: 500, g: 1000, b: 2000);

        WhiteBalanceGains gains = WhiteBalance.ComputeSpotGains(image, 0, BayerPattern.Rggb, 3, 3);

        Assert.Equal(2.0, gains.GainR, 10);
        Assert.Equal(0.5, gains.GainB, 10);
    }

    [Fact]
    public void ComputeGrayWorld_NoBayer_ReturnsUnity()
    {
        using RawImage image = LoadMosaic(BayerPattern.None, 100, 100, 100);
        WhiteBalanceGains gains = WhiteBalance.ComputeGrayWorld(image, 0, BayerPattern.None);
        Assert.Equal(1.0, gains.GainR);
        Assert.Equal(1.0, gains.GainB);
    }
}
