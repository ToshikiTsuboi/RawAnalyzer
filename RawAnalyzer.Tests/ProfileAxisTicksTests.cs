using System.Globalization;
using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>グラフの軸の目盛り(主・補助の間隔と位置、ラベルの間引き、間隔の入力)。</summary>
public class ProfileAxisTicksTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, 1999, 700)]
    [InlineData(100.25, 150.25, 700)]
    [InlineData(400, 402, 500)]
    [InlineData(500.5, 501.5, 500)]
    [InlineData(2147483600, 2147483647, 400)]
    public void IntegerAxis_TicksAreOrderedIntegerCoordinatesInRange(double minimum, double maximum, double width)
    {
        ProfileAxisTicks ticks = ProfileAxisTicks.Layout(new(minimum, maximum), width, 100, true, null, null);
        Assert.NotEmpty(ticks.Major);
        Assert.Equal(ticks.Major.Distinct().OrderBy(v => v), ticks.Major);
        Assert.True(ticks.MajorStep >= 1);
        foreach (double value in ticks.Major.Concat(ticks.Minor))
        {
            Assert.InRange(value, minimum, maximum);
            Assert.Equal(Math.Round(value), value);
        }
    }

    [Fact]
    public void AutoStep_IsOneTwoFiveTimesPowerOfTenNearTheTargetSpacing()
    {
        // 700px に 100px おきの目安なら 7 分割 → 1999/7 ≈ 286 → 500
        ProfileAxisTicks ticks = ProfileAxisTicks.Layout(new(0, 1999), 700, 100, true, null, null);
        Assert.Equal(500, ticks.MajorStep);
        Assert.Equal(new double[] { 0, 500, 1000, 1500 }, ticks.Major);
        Assert.Equal(0.5, ProfileAxisTicks.AutoStep(1, 400, 100, integerSteps: false));
        Assert.Equal(1, ProfileAxisTicks.AutoStep(1, 400, 100, integerSteps: true));
    }

    [Fact]
    public void ManualStep_PlacesTicksAtMultiplesOfTheStep()
    {
        // 軸の最小値からではなく 0 から数えるので、移動しても同じ値に並ぶ
        ProfileAxisTicks ticks = ProfileAxisTicks.Layout(new(998.2, 1001.7), 300, 40, false, 0.5, null);
        Assert.Equal(0.5, ticks.MajorStep);
        Assert.False(ticks.ManualMajorTooFine);
        Assert.Equal(new[] { 998.5, 999, 999.5, 1000, 1000.5, 1001, 1001.5 }, ticks.Major);
        Assert.Equal(1, ticks.Decimals);
        Assert.Equal(new[] { "998.5", "999.0", "999.5", "1000.0" },
            ticks.Major.Take(4).Select(v => ticks.Format(v, Invariant)));
    }

    [Fact]
    public void ManualStep_TooFineForTheZoomFallsBackToAutoAndSaysSo()
    {
        ProfileAxisTicks zoomedOut = ProfileAxisTicks.Layout(new(0, 1999), 700, 100, true, 1, null);
        Assert.True(zoomedOut.ManualMajorTooFine);
        Assert.Equal(500, zoomedOut.MajorStep);

        // 拡大すれば指定の間隔で描く(1px あたり 4px 以上)
        ProfileAxisTicks zoomedIn = ProfileAxisTicks.Layout(new(100, 200), 700, 100, true, 1, null);
        Assert.False(zoomedIn.ManualMajorTooFine);
        Assert.Equal(1, zoomedIn.MajorStep);
        Assert.Equal(101, zoomedIn.Major.Count);
    }

    [Fact]
    public void DecimalSteps_DoNotAccumulateRoundingErrors()
    {
        ProfileAxisTicks ticks = ProfileAxisTicks.Layout(new(0, 1), 400, 40, false, 0.1, null);
        Assert.Equal(11, ticks.Major.Count);
        Assert.Equal(new[] { "0.0", "0.1", "0.2", "0.3", "0.4", "0.5", "0.6", "0.7", "0.8", "0.9", "1.0" },
            ticks.Major.Select(v => ticks.Format(v, Invariant)));
        Assert.Equal(2, ProfileAxisTicks.DecimalsOf(0.25));
        Assert.Equal(0, ProfileAxisTicks.DecimalsOf(1000));
        Assert.Equal(3, ProfileAxisTicks.DecimalsOf(0.005));
    }

    [Fact]
    public void NegativeValues_FormatWithoutMinusZero()
    {
        ProfileAxisTicks ticks = ProfileAxisTicks.Layout(new(-1, 1), 400, 40, false, 0.5, null);
        Assert.Equal(new[] { "-1.0", "-0.5", "0.0", "0.5", "1.0" }, ticks.Major.Select(v => ticks.Format(v, Invariant)));
        Assert.Equal("0.0", ticks.Format(-1e-12, Invariant));
    }

    [Theory]
    [InlineData(1000, false, 200)]
    [InlineData(2, false, 0.5)]
    [InlineData(5, false, 1)]
    [InlineData(0.5, false, 0.1)]
    [InlineData(1, true, 0)]
    [InlineData(2, true, 1)]
    [InlineData(5, true, 1)]
    [InlineData(10, true, 2)]
    [InlineData(20, true, 5)]
    [InlineData(50, true, 10)]
    [InlineData(4, true, 2)]
    [InlineData(3, true, 0)]
    public void AutoMinorStep_DividesTheMajorStep(double major, bool integer, double expected)
    {
        Assert.Equal(expected, ProfileAxisTicks.AutoMinorStep(major, integer), 12);
    }

    [Fact]
    public void MinorTicks_SkipPositionsOfMajorTicks()
    {
        ProfileAxisTicks ticks = ProfileAxisTicks.Layout(new(0, 1000), 500, 40, false, null, null);
        Assert.Equal(100, ticks.MajorStep);
        Assert.Equal(20, ticks.MinorStep, 12);
        Assert.Equal(40, ticks.Minor.Count);
        Assert.All(ticks.Minor, value => Assert.NotEqual(0, value % 100, 6));
    }

    [Fact]
    public void ManualMinorStep_IsOmittedWhenNotFinerThanMajorOrTooDense()
    {
        ProfileAxisTicks coarse = ProfileAxisTicks.Layout(new(0, 1000), 500, 40, false, 100, 200);
        Assert.True(coarse.ManualMinorOmitted);
        Assert.Empty(coarse.Minor);
        Assert.Equal(0, coarse.MinorStep);

        ProfileAxisTicks dense = ProfileAxisTicks.Layout(new(0, 1000), 500, 40, false, 100, 0.5);
        Assert.True(dense.ManualMinorOmitted);
        Assert.Empty(dense.Minor);

        // 0 は「補助目盛りなし」の指定なので、描かなくても知らせない
        ProfileAxisTicks none = ProfileAxisTicks.Layout(new(0, 1000), 500, 40, false, 100, 0);
        Assert.False(none.ManualMinorOmitted);
        Assert.Empty(none.Minor);

        ProfileAxisTicks manual = ProfileAxisTicks.Layout(new(0, 1000), 500, 40, false, 100, 50);
        Assert.Equal(50, manual.MinorStep);
        Assert.Equal(10, manual.Minor.Count);
    }

    [Theory]
    [InlineData(50, 30, 1)]
    [InlineData(10, 15, 2)]
    [InlineData(10, 25, 5)]
    [InlineData(1, 25, 50)]
    public void LabelEvery_IsTheSmallestOneTwoFiveMultipleThatAvoidsOverlap(double spacing, double extent, long expected)
    {
        Assert.Equal(expected, ProfileAxisTicks.LabelEvery(spacing, extent));
    }

    [Fact]
    public void Labels_StayOnTheSameValuesWhilePanning()
    {
        ProfileAxisTicks ticks = ProfileAxisTicks.Layout(new(-7, 13), 100, 40, true, 1, null);
        Assert.Equal(new double[] { -5, 0, 5, 10 }, ticks.Major.Where(v => ticks.IsLabeled(v, 5)));
    }

    [Fact]
    public void EmptyRangeOrLength_DrawsNoTicks()
    {
        Assert.Empty(ProfileAxisTicks.Layout(new(5, 5), 400, 40, false, null, null).Major);
        Assert.Empty(ProfileAxisTicks.Layout(new(0, 10), 0, 40, false, null, null).Major);
        Assert.Empty(ProfileAxisTicks.Generate(new(0, 1e9), 1, 100));
    }

    [Theory]
    [InlineData("abc", false, false, "有限の数値")]
    [InlineData("1e400", false, false, "有限の数値")]
    [InlineData("0", false, false, "0 より大きい")]
    [InlineData("-1", false, true, "0 以上")]
    [InlineData("1.5", true, false, "1 以上の整数")]
    [InlineData("0.5", true, true, "1 以上の整数")]
    public void ParseStep_RejectsWithReason(string text, bool integer, bool allowZero, string reason)
    {
        Assert.Contains(reason, ProfileAxisTicks.ParseStep(text, Invariant, integer, allowZero, out _));
    }

    [Theory]
    [InlineData("0.5", false, false, 0.5)]
    [InlineData("１００", true, false, 100)]
    [InlineData("10.0", true, false, 10)]
    [InlineData("0", true, true, 0)]
    public void ParseStep_AcceptsPositiveValues(string text, bool integer, bool allowZero, double expected)
    {
        Assert.Null(ProfileAxisTicks.ParseStep(text, Invariant, integer, allowZero, out double step));
        Assert.Equal(expected, step);
    }
}
