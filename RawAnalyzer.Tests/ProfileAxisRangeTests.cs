using System.Globalization;
using System.Windows;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class ProfileAxisRangeTests
{
    [Theory]
    [InlineData(255)]
    [InlineData(1023)]
    [InlineData(4095)]
    [InlineData(16383)]
    [InlineData(65535)]
    public void Full_UsesImageBitDepth(int maxCode)
    {
        Assert.Equal(new ProfileAxisRange(0, maxCode), ProfileAxisRange.Full(maxCode));
    }

    [Fact]
    public void Auto_AddsFivePercentPadding()
    {
        ProfileStatistics stats = ImageAnalysis.ComputeProfileStatistics(new double[] { 100, 150, 200 });
        Assert.Equal(new ProfileAxisRange(95, 205), ProfileAxisRange.Auto(stats, 4095));
    }

    [Theory]
    [InlineData(0, 0, 0.5)]
    [InlineData(100, 99.5, 100.5)]
    [InlineData(255, 254.5, 255)]
    public void Auto_ConstantDataHasNonzeroRange(double value, double min, double max)
    {
        ProfileStatistics stats = ImageAnalysis.ComputeProfileStatistics(new[] { value, value });
        Assert.Equal(new ProfileAxisRange(min, max), ProfileAxisRange.Auto(stats, 255));
    }

    [Fact]
    public void Auto_EmptyDataFallsBackToFullRange()
    {
        Assert.Equal(new ProfileAxisRange(0, 4095), ProfileAxisRange.Auto(default, 4095));
    }

    [Fact]
    public void Auto_PreservesSubCodeProjectionDetail()
    {
        ProfileStatistics stats = ImageAnalysis.ComputeProfileStatistics(new[] { 100.0001, 100.0002 });
        ProfileAxisRange range = ProfileAxisRange.Auto(stats, 4095);
        Assert.True(range.Minimum < stats.Min && range.Maximum > stats.Max);
        Assert.InRange(range.Maximum - range.Minimum, 0.0001099, 0.0001101);
    }

    [Theory]
    [InlineData("", "10")]
    [InlineData("10", "")]
    [InlineData("10", "10")]
    [InlineData("11", "10")]
    [InlineData("NaN", "10")]
    [InlineData("0", "Infinity")]
    [InlineData("-Infinity", "10")]
    [InlineData("0", "1e999")]
    [InlineData("x", "100")]
    [InlineData("-1e308", "1e308")]
    public void Manual_RejectsInvalidRanges(string minimum, string maximum)
    {
        Assert.False(ProfileAxisRange.TryParse(minimum, maximum, CultureInfo.InvariantCulture, out _));
    }

    [Theory]
    [InlineData("en-US", "-1.25", "2.5")]
    [InlineData("ja-JP", "-1.25", "2.5")]
    [InlineData("de-DE", "-1,25", "2,5")]
    [InlineData("de-DE", "-1.25", "2.5")]
    public void Manual_AcceptsDecimalsAndNegativeValues(string culture, string minimum, string maximum)
    {
        Assert.True(ProfileAxisRange.TryParse(minimum, maximum, CultureInfo.GetCultureInfo(culture), out ProfileAxisRange range));
        Assert.Equal(new ProfileAxisRange(-1.25, 2.5), range);
    }

    [Fact]
    public void Mapping_SubtractsAxisMinimum()
    {
        var range = new ProfileAxisRange(1000, 2000);
        Assert.Equal(101, range.ToCanvasY(1000, 102));
        Assert.Equal(51, range.ToCanvasY(1500, 102));
        Assert.Equal(1, range.ToCanvasY(2000, 102));
    }

    [Theory]
    [InlineData(0, 20, 5, 15)]
    [InlineData(20, 0, 15, 5)]
    public void Clipping_IntersectsBoundsRatherThanClampingEndpoints(double a, double b, double clippedA, double clippedB)
    {
        var range = new ProfileAxisRange(5, 15);
        Assert.True(range.TryClipSegment(new Point(0, a), new Point(100, b), out Point start, out Point end));
        Assert.Equal(new Point(25, clippedA), start);
        Assert.Equal(new Point(75, clippedB), end);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(20, 30)]
    [InlineData(double.NaN, 10)]
    [InlineData(10, double.PositiveInfinity)]
    public void Clipping_OmitsOutOfRangeSegments(double a, double b)
    {
        var range = new ProfileAxisRange(5, 15);
        Assert.False(range.TryClipSegment(new Point(0, a), new Point(100, b), out _, out _));
    }

    [Fact]
    public void VeryNarrowManualScaleProducesFiniteCanvasGeometry()
    {
        Assert.True(ProfileAxisRange.TryParse("-1e-300", "1e-300", CultureInfo.InvariantCulture, out ProfileAxisRange range));
        var points = new[] { new Point(0, -10), new Point(100, 10), new Point(200, -10) };
        var geometry = range.BuildGeometry(points, 300);
        Rect bounds = geometry.Bounds;
        Assert.True(double.IsFinite(bounds.X) && double.IsFinite(bounds.Y));
        Assert.InRange(bounds.Top, 0, 300);
        Assert.InRange(bounds.Bottom, 0, 300);
    }
}
