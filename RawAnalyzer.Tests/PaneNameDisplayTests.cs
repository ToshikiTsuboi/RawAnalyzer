using RawAnalyzer.App.Compare;
using Xunit;

namespace RawAnalyzer.Tests;

public class PaneNameDisplayTests
{
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
