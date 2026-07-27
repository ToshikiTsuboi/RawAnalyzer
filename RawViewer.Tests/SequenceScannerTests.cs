using RawViewer.Core;
using Xunit;

namespace RawViewer.Tests;

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
    [InlineData(0)]
    [InlineData(-1)]
    public void FindStack_UnknownReferenceLength_ReturnsEmpty(long length)
    {
        // 基準サイズが不明なとき、サイズ不明ファイル同士が一致してしまうのを防ぐ
        IReadOnlyList<string> stack = SequenceScanner.FindStack(
            @"C:\d\broken.raw", length, Folder);

        Assert.Empty(stack);
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
