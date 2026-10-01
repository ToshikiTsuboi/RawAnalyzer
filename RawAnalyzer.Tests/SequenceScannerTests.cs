using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class SequenceScannerTests
{
    private static readonly SequenceFile[] Folder =
    {
        new(@"C:\d\img10.raw", 1000),
        new(@"C:\d\img2.raw", 1000),
        new(@"C:\d\img1.raw", 1000),
        new(@"C:\d\other.raw", 2000),      // サイズ違い
        new(@"C:\d\img3.bin", 1000),       // 拡張子違い
        new(@"C:\d\broken.raw", -1),       // サイズ不明
    };

    [Fact]
    public void FindStack_SameExtensionAndSize_ReturnsNaturalOrder()
    {
        IReadOnlyList<string> stack = SequenceScanner.FindStack(
            @"C:\d\img1.raw", 1000, Folder);

        Assert.Equal(
            new[] { @"C:\d\img1.raw", @"C:\d\img2.raw", @"C:\d\img10.raw" },
            stack);
    }

    [Fact]
    public void FindStack_ExtensionComparisonIsCaseInsensitive()
    {
        var folder = new[]
        {
            new SequenceFile(@"C:\d\a.RAW", 500),
            new SequenceFile(@"C:\d\b.raw", 500),
        };

        IReadOnlyList<string> stack = SequenceScanner.FindStack(@"C:\d\b.raw", 500, folder);

        Assert.Equal(2, stack.Count);
    }

    [Theory]
    [InlineData(@"C:\d\empty.raw", 0)]    // 中身のないファイル(サイズ 0)
    [InlineData(@"C:\d\broken.raw", -1)]  // サイズ不明
    public void FindStack_UnknownReferenceLength_ReturnsEmpty(string reference, long length)
    {
        // 基準サイズが不明・0 のとき、サイズ不明ファイル同士・空ファイル同士が一致してしまうのを防ぐ
        // (どちらも同じ長さのファイルが自分のほかにもある)
        SequenceFile[] folder = Folder.Concat(new SequenceFile[]
        {
            new(@"C:\d\empty.raw", 0),
            new(@"C:\d\empty2.raw", 0),
            new(@"C:\d\broken2.raw", -1),
        }).ToArray();

        IReadOnlyList<string> stack = SequenceScanner.FindStack(reference, length, folder);

        Assert.Empty(stack);
    }

    [Fact]
    public void FindStack_CandidatesInOtherFolder_AreExcluded()
    {
        // 一覧が別フォルダを含んでいても、基準ファイルと同じフォルダのものだけを連番にする
        var candidates = new[]
        {
            new SequenceFile(@"C:\a\img_0001.raw", 1000),
            new SequenceFile(@"C:\a\img_0002.raw", 1000),
            new SequenceFile(@"C:\b\cap_0001.raw", 1000),
            new SequenceFile(@"C:\a\sub\img_0003.raw", 1000),
        };

        IReadOnlyList<string> stack = SequenceScanner.FindStack(@"C:\a\img_0001.raw", 1000, candidates);

        Assert.Equal(new[] { @"C:\a\img_0001.raw", @"C:\a\img_0002.raw" }, stack);
    }

    [Fact]
    public void FindStack_ReferenceNotAmongCandidates_ReturnsEmpty()
    {
        // フォルダツリーで別フォルダへ移った後の一覧(基準ファイルのフォルダではない)。
        // 以前は別フォルダの同じサイズの .raw を連番として返し、表示中のファイルが無いのに「1 / N」になっていた
        var otherFolder = new[]
        {
            new SequenceFile(@"C:\b\cap_0001.raw", 1000),
            new SequenceFile(@"C:\b\cap_0002.raw", 1000),
        };

        Assert.Empty(SequenceScanner.FindStack(@"C:\a\img_0001.raw", 1000, otherFolder));
    }

    [Fact]
    public void FindStack_ReferenceListedWithOtherSize_ReturnsEmpty()
    {
        // 基準ファイル自身が連番に入らない(一覧のサイズが基準と違う)なら、位置が決まらないので連番にしない
        var candidates = new[]
        {
            new SequenceFile(@"C:\a\img_0001.raw", 999),
            new SequenceFile(@"C:\a\img_0002.raw", 1000),
            new SequenceFile(@"C:\a\img_0003.raw", 1000),
        };

        Assert.Empty(SequenceScanner.FindStack(@"C:\a\img_0001.raw", 1000, candidates));
    }

    [Fact]
    public void FindStack_FolderComparisonIgnoresCaseAndNormalizesPath()
    {
        var candidates = new[]
        {
            new SequenceFile(@"C:\Data\img1.raw", 1000),
            new SequenceFile(@"C:\Data\img2.raw", 1000),
        };

        IReadOnlyList<string> stack = SequenceScanner.FindStack(@"c:\data\.\IMG2.raw", 1000, candidates);

        Assert.Equal(new[] { @"C:\Data\img1.raw", @"C:\Data\img2.raw" }, stack);
        Assert.Equal(1, SequenceScanner.IndexOf(stack, @"c:\data\.\IMG2.raw"));
    }

    [Theory]
    [InlineData(@"C:\a\img.raw", @"C:\a", true)]
    [InlineData(@"C:\a\img.raw", @"c:\A\", true)]
    [InlineData(@"C:\a\img.raw", @"C:\b", false)]
    [InlineData(@"C:\a\sub\img.raw", @"C:\a", false)]
    [InlineData(@"C:\img.raw", @"C:\", true)]
    [InlineData(@"\\nas\share\cap\img.raw", @"\\NAS\share\cap", true)]
    public void IsInFolder_ComparesParentFolder(string path, string folder, bool expected)
    {
        Assert.Equal(expected, SequenceScanner.IsInFolder(path, folder));
    }

    [Fact]
    public void IsInFolder_NullFolder_IsFalse()
    {
        Assert.False(SequenceScanner.IsInFolder(@"C:\a\img.raw", null));
    }

    [Fact]
    public void IndexOf_FindsReferenceCaseInsensitively()
    {
        IReadOnlyList<string> stack = SequenceScanner.FindStack(
            @"C:\d\img1.raw", 1000, Folder);

        Assert.Equal(1, SequenceScanner.IndexOf(stack, @"C:\D\IMG2.RAW"));
        Assert.Equal(0, SequenceScanner.IndexOf(stack, @"C:\d\missing.raw"));
    }
}
