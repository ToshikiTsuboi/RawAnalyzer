using System.Windows;
using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

public class ProfilePlotNavigationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void Zoom_KeepsCursorAnchorFixed(double fraction)
    {
        var original = new ProfileAxisRange(10, 110);
        ProfileAxisRange zoomed = ProfilePlotNavigation.Zoom(original, fraction, 0.5, 1, 200);
        Assert.Equal(50, zoomed.Maximum - zoomed.Minimum, 8);
        Assert.Equal(10 + 100 * fraction, zoomed.Minimum + 50 * fraction, 8);
    }

    [Fact]
    public void ZoomOutAndPan_StayInsideImage()
    {
        var full = new ProfileAxisRange(0, 1999);
        var current = new ProfileAxisRange(100, 200);
        Assert.Equal(full, ProfilePlotNavigation.Zoom(current, 0.2, 100, 1, 1999, full));
        Assert.Equal(new ProfileAxisRange(0, 100), ProfilePlotNavigation.Pan(current, -100, full));
        Assert.Equal(new ProfileAxisRange(1899, 1999), ProfilePlotNavigation.Pan(current, 100, full));
    }

    [Fact]
    public void RepeatedZoom_NeverCollapsesToZeroWidth()
    {
        var range = new ProfileAxisRange(0, 65535);
        for (int i = 0; i < 1000; i++) range = ProfilePlotNavigation.Zoom(range, 0.5, 0.1, 1e-9, 1e6);
        Assert.True(range.Maximum > range.Minimum);
        Assert.True(double.IsFinite(range.Minimum) && double.IsFinite(range.Maximum));
    }

    [Fact]
    public void InvalidMouseValues_DoNotCorruptRanges()
    {
        var original = new ProfileAxisRange(0, 100);
        Assert.Equal(original, ProfilePlotNavigation.Zoom(original, double.NaN, 0.5, 1, 200));
        Assert.Equal(original, ProfilePlotNavigation.Zoom(original, 0.5, double.PositiveInfinity, 1, 200));
        Assert.Equal(original, ProfilePlotNavigation.Pan(original, double.NaN));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EmptyOrSinglePoint_UsesNonzeroCenteredHorizontalRange(int count)
    {
        Assert.Equal(new ProfileAxisRange(-0.5, 0.5), ProfilePlotNavigation.FullHorizontal(count));
    }

    [Theory]
    [InlineData(0, 1999, 700)]
    [InlineData(100.25, 150.25, 700)]
    [InlineData(400, 402, 500)]
    [InlineData(500.5, 501.5, 500)]
    [InlineData(2147483600, 2147483647, 400)]
    public void Ticks_AreOrderedIntegerImageCoordinates(double minimum, double maximum, double width)
    {
        IReadOnlyList<double> ticks = ProfilePlotNavigation.Ticks(new(minimum, maximum), width);
        Assert.NotEmpty(ticks);
        Assert.Equal(ticks.Distinct().OrderBy(v => v), ticks);
        foreach (double value in ticks)
        {
            Assert.InRange(value, minimum, maximum);
            Assert.Equal(Math.Round(value), value);
        }
    }

    [Fact]
    public void Samples_UseVisibleIntervalAndAdjacentBoundaryPoints()
    {
        double[] values = Enumerable.Range(0, 10000).Select(v => (double)v).ToArray();
        IReadOnlyList<Point> points = ProfilePlotNavigation.SampleVisible(values, new(100.25, 110.25), 700);
        Assert.Equal(12, points.Count);
        Assert.Equal(100, points[0].Y);
        Assert.Equal(111, points[^1].Y);
        Assert.True(points[0].X < 0);
        Assert.True(points[^1].X > 700);
        Assert.Equal((101 - 100.25) / 10 * 700, points[1].X, 9);
    }

    [Fact]
    public void Downsampling_KeepsSpikesAndTheirCoordinateOrder()
    {
        var values = new double[10000];
        values[2345] = 60000;
        values[2346] = -200;
        IReadOnlyList<Point> points = ProfilePlotNavigation.SampleVisible(values, new(2000, 3000), 100);
        Assert.Equal(34.5, Assert.Single(points, p => p.Y == 60000).X, 9);
        Assert.Equal(34.6, Assert.Single(points, p => p.Y == -200).X, 9);
        Assert.True(points.Count <= 202);
        for (int i = 1; i < points.Count; i++) Assert.True(points[i].X >= points[i - 1].X);
    }
}
