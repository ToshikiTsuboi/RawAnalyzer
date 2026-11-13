using System.Windows;
using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

public class SplitLabelPlacementTests
{
    [Fact]
    public void Find_AnchorsToVisibleCornerAndKeepsLabelInsideSection()
    {
        var viewport = new Rect(0, 0, 200, 100);
        var label = new Size(30, 20);
        Point? Place(Rect section) => SplitLabelPlacement.Find(Rect.Intersect(section, viewport), label);

        Assert.Equal(new Point(54, 24), Place(new Rect(50, 20, 75, 75)));
        Assert.Equal(new Point(4, 4), Place(new Rect(-50, -50, 100, 100)));
        Assert.Equal(new Point(170, 80), Place(new Rect(169, 79, 50, 50)));
        Assert.Null(Place(new Rect(201, 0, 30, 30)));
        Assert.Equal(new Point(180, 4), Place(new Rect(180, 0, 30, 30)));
    }
}
