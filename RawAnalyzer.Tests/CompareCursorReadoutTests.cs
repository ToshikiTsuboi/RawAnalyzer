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
            CompareCursorReadout.Format(812, 718, [("A", a), ("B", b)]));
    }

    [Fact]
    public void Format_ThreePanesOmitDelta()
    {
        Assert.Equal("x 812 · y 718 · A 1432 · B 1398 · C 1250",
            CompareCursorReadout.Format(812, 718, [("A", 1432), ("B", 1398), ("C", 1250)]));
    }

    [Fact]
    public void Format_MissingCorrespondenceUsesDashAndOmitsDelta()
    {
        Assert.Equal("x 0 · y 0 · A 1432 · B —",
            CompareCursorReadout.Format(0, 0, [("A", 1432), ("B", null)]));
    }

    [Fact]
    public void CorrespondingPixel_UsesNearestCenterWithinTargetExtent()
    {
        var edge = CompareSync.MapCursor(0, 0, (40, 40), (20, 20));
        Assert.Null(CompareCursorReadout.CorrespondingPixel(edge.X, edge.Y, 20, 20));
        var interior = CompareSync.MapCursor(19, 20, (40, 40), (20, 20));
        Assert.Equal((9, 10), CompareCursorReadout.CorrespondingPixel(interior.X, interior.Y, 20, 20));
        var sameSize = CompareSync.MapCursor(0, 30, (52, 31), (52, 31));
        Assert.Equal((0, 30), CompareCursorReadout.CorrespondingPixel(sameSize.X, sameSize.Y, 52, 31));
    }
}
