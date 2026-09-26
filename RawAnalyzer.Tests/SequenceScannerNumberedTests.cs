using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class SequenceScannerNumberedTests
{
    private static SequenceFile[] Files(params string[] names)
    {
        // サイズはばらばらにする(連番判定がサイズに依存していないことの確認を兼ねる)
        var files = new SequenceFile[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            files[i] = new SequenceFile(@"C:\dir\" + names[i], 1000 + i * 37);
        }

        return files;
    }

    [Fact]
    public void FindNumberedStack_DifferentPrefixOrExtension_IsExcluded()
    {
        IReadOnlyList<string> stack = SequenceScanner.FindNumberedStack(
            @"C:\dir\dark_001.tif",
            Files(
                "dark_001.tif", "dark_002.tif",
                "flat_001.tif",       // 接頭辞が違う
                "dark_001.png",       // 拡張子が違う
                "dark_003_roi.tif")); // 接尾辞が違う

        Assert.Equal(new[] { @"C:\dir\dark_001.tif", @"C:\dir\dark_002.tif" }, stack);
    }

    [Fact]
    public void FindNumberedStack_UsesLastNumberRun()
    {
        // 日付など前方の数字ではなく、末尾側の連番でまとめる
        IReadOnlyList<string> stack = SequenceScanner.FindNumberedStack(
            @"C:\dir\2026-08-04_012.tif",
            Files("2026-08-04_011.tif", "2026-08-04_012.tif", "2026-08-05_011.tif"));

        Assert.Equal(
            new[] { @"C:\dir\2026-08-04_011.tif", @"C:\dir\2026-08-04_012.tif" }, stack);
    }

    [Fact]
    public void FindNumberedStack_SuffixAfterNumber_IsMatched()
    {
        IReadOnlyList<string> stack = SequenceScanner.FindNumberedStack(
            @"C:\dir\frame_001_long.tif",
            Files("frame_001_long.tif", "frame_002_long.tif", "frame_002_short.tif"));

        Assert.Equal(
            new[] { @"C:\dir\frame_001_long.tif", @"C:\dir\frame_002_long.tif" }, stack);
    }

    [Fact]
    public void FindNumberedStack_NoDigitsInName_ReturnsEmpty()
    {
        Assert.Empty(SequenceScanner.FindNumberedStack(
            @"C:\dir\capture.tif", Files("capture.tif", "reference.tif")));
    }

    [Fact]
    public void FindNumberedStack_ExtensionCaseIsIgnored()
    {
        IReadOnlyList<string> stack = SequenceScanner.FindNumberedStack(
            @"C:\dir\a1.TIF", Files("a1.TIF", "a2.tif"));

        Assert.Equal(2, stack.Count);
    }

    [Fact]
    public void FindNumberedStack_UnpaddedNumbers_SortNaturally()
    {
        IReadOnlyList<string> stack = SequenceScanner.FindNumberedStack(
            @"C:\dir\img2.tif", Files("img1.tif", "img2.tif", "img10.tif"));

        Assert.Equal(
            new[] { @"C:\dir\img1.tif", @"C:\dir\img2.tif", @"C:\dir\img10.tif" }, stack);
    }
}
