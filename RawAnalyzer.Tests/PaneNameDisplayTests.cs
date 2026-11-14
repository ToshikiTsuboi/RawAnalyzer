using System.Globalization;
using RawAnalyzer.App.Compare;
using Xunit;

namespace RawAnalyzer.Tests;

public class PaneNameDisplayTests
{
    internal static IReadOnlyList<string> MixedNames { get; } =
    [
        "demo_scene_a_1920x1080_12bit_rggb.raw",
        "demo_scene_b_1920x1080_12bit_rggb.raw",
        "dark_a_1920x1080_12bit.raw",
    ];

    internal static IReadOnlyList<string> PairedNames { get; } =
        [.. MixedNames, "dark_b_1920x1080_12bit.raw"];

    [Fact]
    public void ForFiles_ChoosesClosestNameEvenWithAnUnrelatedPane()
    {
        PaneNameDisplay[] display = PaneNameDisplay.ForFiles(MixedNames);
        Assert.Equal(MixedNames, display.Select(name => name.FullName));
        Assert.Equal(new PaneNameDisplay("demo_scene_", "a", "_1920x1080_12bit_rggb.raw"), display[0]);
        Assert.Equal(new PaneNameDisplay("demo_scene_", "b", "_1920x1080_12bit_rggb.raw"), display[1]);
        Assert.Equal("…_a_…", Fit(display[0], 5));
        Assert.Equal("…_b_…", Fit(display[1], 5));
        Assert.Equal(new PaneNameDisplay("d", "ark_a_1920x1080_12bit", ".raw"), display[2]);
    }

    [Fact]
    public void ForFiles_MarksOnlyTheOmittedEnds()
    {
        PaneNameDisplay[] sharedPrefix = PaneNameDisplay.ForFiles(["demo_a", "demo_b"]);
        Assert.Equal("…a", Fit(sharedPrefix[0], 2));
        PaneNameDisplay[] sharedSuffix = PaneNameDisplay.ForFiles(["a.raw", "b.raw"]);
        Assert.Equal("a…", Fit(sharedSuffix[0], 2));
    }

    [Fact]
    public void ForFiles_ShortensSharedEndsAndPreservesDifferences()
    {
        string[] names = ["demo_scene_a_capture.raw", "demo_scene_b_capture.raw", "demo_scene_c_capture.raw"];
        PaneNameDisplay[] display = PaneNameDisplay.ForFiles(names);
        Assert.Equal(names, display.Select(name => Fit(name, 40)));
        Assert.Equal(new[] { "a", "b", "c" }, display.Select(name => name.Difference));
        Assert.Equal(new[] { "…ne_a_c…", "…ne_b_c…", "…ne_c_c…" }, display.Select(name => Fit(name, 8)));
    }

    [Fact]
    public void ForFiles_HandlesSingleIdenticalAndUnicodeNames()
    {
        Assert.Equal("demo.raw", Fit(Assert.Single(PaneNameDisplay.ForFiles(["demo.raw"])), 1));
        Assert.All(PaneNameDisplay.ForFiles(["demo.raw", "demo.raw"]), name => Assert.Empty(name.Difference));
        Assert.Equal(new[] { "🐈", "🐇" },
            PaneNameDisplay.ForFiles(["demo🐈.raw", "demo🐇.raw"]).Select(name => name.Difference));
    }

    [Fact]
    public void ForWidth_KeepsFourNamesDistinctWithPairedDifferences()
    {
        PaneNameDisplay[] displays = PaneNameDisplay.ForFiles(PairedNames);
        string[] shown = displays.Select(name => Fit(name, 13)).ToArray();
        Assert.Equal(new[] { "a", "b", "a", "b" }, displays.Select(name => name.Difference));
        Assert.Equal(4, shown.Distinct().Count());
        Assert.All(shown, name =>
        {
            Assert.Equal(13, name.Length);
            Assert.EndsWith("…", name);
        });
    }

    [Fact]
    public void ForWidth_ExpandsByWholeGraphemes()
    {
        var display = new PaneNameDisplay("demo👩‍🔬", "a", "e\u0301.raw");
        Assert.Equal("…👩‍🔬ae\u0301…", Fit(display, 5));
    }

    private static string Fit(PaneNameDisplay display, int width) =>
        display.ForWidth(width, Measure(display.FullName), Measure).FullName;

    private static double Measure(string text) => new StringInfo(text).LengthInTextElements;
}
