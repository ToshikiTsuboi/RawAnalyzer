using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class BayerHelperTests
{
    [Theory]
    [InlineData(BayerPattern.Rggb, 0, 0, BayerChannel.R)]
    [InlineData(BayerPattern.Rggb, 1, 0, BayerChannel.Gr)]
    [InlineData(BayerPattern.Rggb, 0, 1, BayerChannel.Gb)]
    [InlineData(BayerPattern.Rggb, 1, 1, BayerChannel.B)]
    [InlineData(BayerPattern.Bggr, 0, 0, BayerChannel.B)]
    [InlineData(BayerPattern.Bggr, 1, 0, BayerChannel.Gb)]
    [InlineData(BayerPattern.Bggr, 0, 1, BayerChannel.Gr)]
    [InlineData(BayerPattern.Bggr, 1, 1, BayerChannel.R)]
    [InlineData(BayerPattern.Grbg, 0, 0, BayerChannel.Gr)]
    [InlineData(BayerPattern.Grbg, 1, 0, BayerChannel.R)]
    [InlineData(BayerPattern.Grbg, 0, 1, BayerChannel.B)]
    [InlineData(BayerPattern.Grbg, 1, 1, BayerChannel.Gb)]
    [InlineData(BayerPattern.Gbrg, 0, 0, BayerChannel.Gb)]
    [InlineData(BayerPattern.Gbrg, 1, 0, BayerChannel.B)]
    [InlineData(BayerPattern.Gbrg, 0, 1, BayerChannel.R)]
    [InlineData(BayerPattern.Gbrg, 1, 1, BayerChannel.Gr)]
    [InlineData(BayerPattern.None, 0, 0, BayerChannel.None)]
    public void GetChannel_ReturnsCorrectChannel(
        BayerPattern pattern, int x, int y, BayerChannel expected)
    {
        Assert.Equal(expected, BayerHelper.GetChannel(pattern, x, y));
    }

    [Fact]
    public void GetChannel_PeriodicIn2x2()
    {
        Assert.Equal(
            BayerHelper.GetChannel(BayerPattern.Rggb, 0, 0),
            BayerHelper.GetChannel(BayerPattern.Rggb, 100, 246));
        Assert.Equal(
            BayerHelper.GetChannel(BayerPattern.Rggb, 1, 0),
            BayerHelper.GetChannel(BayerPattern.Rggb, 101, 246));
    }

    [Theory]
    [InlineData(BayerChannel.R, "R")]
    [InlineData(BayerChannel.Gr, "Gr")]
    [InlineData(BayerChannel.Gb, "Gb")]
    [InlineData(BayerChannel.B, "B")]
    [InlineData(BayerChannel.None, "-")]
    public void GetLabel_ReturnsDisplayLabel(BayerChannel channel, string expected)
    {
        Assert.Equal(expected, BayerHelper.GetLabel(channel));
    }
}
