using RawAnalyzer.App.Compare;
using Xunit;

namespace RawAnalyzer.Tests;

public class CompareLayoutTests
{
    [Theory]
    [InlineData((int)CompareLayoutMode.Horizontal, 1, 4)]
    [InlineData((int)CompareLayoutMode.Vertical, 4, 1)]
    [InlineData((int)CompareLayoutMode.Grid, 2, 2)]
    public void Choose_ManualModeOverridesFitAndCurrentLayout(int mode, int rows, int columns)
    {
        Assert.Equal((rows, columns), CompareLayout.Choose(1200, 800, [1, 1, 1, 1],
            8, 60, (1, 4), (CompareLayoutMode)mode));
        Assert.Equal((rows, columns), CompareLayout.Choose(300, 1800, [1, 1, 1, 1],
            8, 60, (4, 1), (CompareLayoutMode)mode));
    }

    [Fact]
    public void Choose_GridUsesOneRowForTwoPanesAndTwoRowsForThree()
    {
        Assert.Equal((1, 2), CompareLayout.Choose(300, 1000, [1, 1], 8, 60, (2, 1), CompareLayoutMode.Grid));
        Assert.Equal((2, 2), CompareLayout.Choose(300, 1000, [1, 1, 1], 8, 60, (3, 1), CompareLayoutMode.Grid));
    }

    [Theory]
    [InlineData(3, 16.0 / 9, 2, 2)]
    [InlineData(3, 9.0 / 16, 1, 3)]
    [InlineData(1, 16.0 / 9, 1, 1)]
    [InlineData(4, 16.0 / 9, 2, 2)]
    public void Choose_MaximizesFittedImageArea(int count, double aspect, int rows, int columns)
    {
        double[] aspects = Enumerable.Repeat(aspect, count).ToArray();
        Assert.Equal((rows, columns), CompareLayout.Choose(1280, 680, aspects, 8, 60, (1, 1)));
    }

    [Fact]
    public void Choose_UsesSmallestImageAreaAndKeepsCurrentLayoutOnTie()
    {
        Assert.Equal((1, 3), CompareLayout.Choose(1280, 680, [2, 2, 0.25], 8, 60, (2, 2)));
        Assert.Equal((1, 2), CompareLayout.Choose(600, 600, [1, 1], 0, 0, (1, 2)));
        Assert.Equal((2, 1), CompareLayout.Choose(600, 600, [1, 1], 0, 0, (2, 1)));
    }
}
