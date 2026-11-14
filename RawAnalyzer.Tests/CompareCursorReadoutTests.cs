using RawAnalyzer.App.Compare;
using Xunit;

namespace RawAnalyzer.Tests;

public class CompareCursorReadoutTests
{
    [Theory]
    [InlineData(1432, 1398, "−34")]
    [InlineData(1398, 1432, "+34")]
    [InlineData(1432, 1432, "0")]
    public void Format_TwoPanesShowDeltaInPaneOrder(int a, int b, string delta)
    {
        Assert.Equal($"x 812 · y 718 · A {a} · B {b} · Δ {delta}",
            CompareCursorReadout.Format(812, 718, [("A", a, 12), ("B", b, 12)]));
    }

    [Fact]
    public void Format_ThreePanesOmitDelta()
    {
        Assert.Equal("x 812 · y 718 · A 1432 · B 1398 · C 1250",
            CompareCursorReadout.Format(812, 718, [("A", 1432, 12), ("B", 1398, 12), ("C", 1250, 12)]));
    }

    [Fact]
    public void Format_MissingCorrespondenceUsesDashAndOmitsDelta()
    {
        Assert.Equal("x 0 · y 0 · A 1432 · B —",
            CompareCursorReadout.Format(0, 0, [("A", 1432, 12), ("B", null, 12)]));
    }

    [Fact]
    public void Format_DifferentBitDepthsShowCodesWithoutDelta()
    {
        Assert.Equal("x 0 · y 0 · A 358 · B 1398",
            CompareCursorReadout.Format(0, 0, [("A", 358, 10), ("B", 1398, 12)]));
    }

    [Fact]
    public void CorrespondingPixel_UsesNearestCenterWithinTargetExtent()
    {
        var first = CompareSync.MapCursor(0, 0, (40, 40), (20, 20));
        Assert.Equal((0, 0), CompareCursorReadout.CorrespondingPixel(first.X, first.Y, 20, 20));
        var last = CompareSync.MapCursor(39, 39, (40, 40), (20, 20));
        Assert.Equal((19, 19), CompareCursorReadout.CorrespondingPixel(last.X, last.Y, 20, 20));
        var interior = CompareSync.MapCursor(19, 20, (40, 40), (20, 20));
        Assert.Equal((9, 10), CompareCursorReadout.CorrespondingPixel(interior.X, interior.Y, 20, 20));
        var sameSize = CompareSync.MapCursor(0, 30, (52, 31), (52, 31));
        Assert.Equal((0, 30), CompareCursorReadout.CorrespondingPixel(sameSize.X, sameSize.Y, 52, 31));
        Assert.Equal((0, 0), CompareCursorReadout.CorrespondingPixel(-0.5, -0.5, 20, 20));
        Assert.Equal((19, 19), CompareCursorReadout.CorrespondingPixel(Math.BitDecrement(19.5), 19.49, 20, 20));
    }

    [Theory]
    [InlineData(-0.6, 0)]
    [InlineData(19.5, 0)]
    [InlineData(0, -0.6)]
    [InlineData(0, 19.5)]
    public void CorrespondingPixel_RejectsPositionsOutsideTheImage(double x, double y)
    {
        Assert.Null(CompareCursorReadout.CorrespondingPixel(x, y, 20, 20));
    }
}
