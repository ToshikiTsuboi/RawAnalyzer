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

    [Fact]
    public void ForFiles_ChoosesClosestNameEvenWithAnUnrelatedPane()
    {
        PaneNameDisplay[] display = PaneNameDisplay.ForFiles(MixedNames);
        Assert.Equal(MixedNames, display.Select(name => name.FullName));
        Assert.Equal(new PaneNameDisplay("demo_scene_", "a", "_1920x1080_12bit_rggb.raw"), display[0]);
        Assert.Equal(new PaneNameDisplay("demo_scene_", "b", "_1920x1080_12bit_rggb.raw"), display[1]);
        Assert.Equal("…a…", display[0].ForWidth(50, 220).FullName);
        Assert.Equal("…b…", display[1].ForWidth(50, 220).FullName);
        Assert.Equal(new PaneNameDisplay("d", "ark_a_1920x1080_12bit", ".raw"), display[2]);
    }

    [Fact]
    public void ForFiles_MarksOnlyTheOmittedEnds()
    {
        PaneNameDisplay[] sharedPrefix = PaneNameDisplay.ForFiles(["demo_a", "demo_b"]);
        Assert.Equal("…a", sharedPrefix[0].ForWidth(20, 60).FullName);
        PaneNameDisplay[] sharedSuffix = PaneNameDisplay.ForFiles(["a.raw", "b.raw"]);
        Assert.Equal("a…", sharedSuffix[0].ForWidth(20, 60).FullName);
    }

    [Fact]
    public void ForFiles_ShortensSharedEndsAndPreservesDifferences()
    {
        string[] names = ["demo_scene_a_capture.raw", "demo_scene_b_capture.raw", "demo_scene_c_capture.raw"];
        PaneNameDisplay[] display = PaneNameDisplay.ForFiles(names);
        Assert.Equal(names, display.Select(name => name.ForWidth(220, 200).FullName));
        Assert.Equal(new[] { "a", "b", "c" }, display.Select(name => name.Difference));
        Assert.Equal(new[] { "…a…", "…b…", "…c…" }, display.Select(name => name.ForWidth(60, 200).FullName));
    }

    [Fact]
    public void ForFiles_HandlesSingleIdenticalAndUnicodeNames()
    {
        Assert.Equal("demo.raw", Assert.Single(PaneNameDisplay.ForFiles(["demo.raw"])).ForWidth(10, 80).FullName);
        Assert.All(PaneNameDisplay.ForFiles(["demo.raw", "demo.raw"]), name => Assert.Empty(name.Difference));
        Assert.Equal(new[] { "🐈", "🐇" },
            PaneNameDisplay.ForFiles(["demo🐈.raw", "demo🐇.raw"]).Select(name => name.Difference));
    }
}
