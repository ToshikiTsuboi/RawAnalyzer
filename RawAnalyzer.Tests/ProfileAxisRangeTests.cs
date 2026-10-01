using System.Globalization;
using System.Windows;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class ProfileAxisRangeTests
{
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
    [InlineData("", "10")]           // 最小側が解析できない
    [InlineData("10", "")]           // 最大側が解析できない
    [InlineData("10", "10")]         // 等しい
    [InlineData("11", "10")]         // 逆転
    [InlineData("NaN", "10")]        // 非有限
    [InlineData("-1e308", "1e308")]  // 差が桁あふれ
    public void Manual_RejectsInvalidRanges(string minimum, string maximum)
    {
        Assert.False(ProfileAxisRange.TryParse(minimum, maximum, CultureInfo.InvariantCulture, out _));
    }

    [Theory]
    [InlineData("ja-JP", "-1.25", "2.5")]
    [InlineData("de-DE", "-1,25", "2,5")]
    [InlineData("de-DE", "-1.25", "2.5")]
    public void Manual_AcceptsDecimalsAndNegativeValues(string culture, string minimum, string maximum)
    {
        Assert.True(ProfileAxisRange.TryParse(minimum, maximum, CultureInfo.GetCultureInfo(culture), out ProfileAxisRange range));
        Assert.Equal(new ProfileAxisRange(-1.25, 2.5), range);
    }

    [Theory]
    [InlineData("－１．２５", "２．５", -1.25, 2.5)] // IME がオンのまま打った全角(以前は読めずに断った)
    [InlineData("ー1。25", "2.5", -1.25, 2.5)]    // かな入力の「ー」「。」
    [InlineData("1,000", "４，０９５", 1000, 4095)] // 3桁区切り
    public void Manual_AcceptsFullWidthAndGroupedInput(string minimum, string maximum, double min, double max)
    {
        Assert.True(ProfileAxisRange.TryParse(minimum, maximum, CultureInfo.GetCultureInfo("ja-JP"), out ProfileAxisRange range));
        Assert.Equal(new ProfileAxisRange(min, max), range);
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
