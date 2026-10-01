using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 連番判定(F2 で開き直す・HDR表示から Raw 表示へ戻る)と一括書き出しの候補を、左パネルの一覧に出ているフォルダではなく
/// 表示中のファイルのフォルダから選ぶことの検証(MainWindow.CandidateFiles)。
/// </summary>
public class SequenceCandidateSourceTests
{
    private static readonly SequenceFile[] FolderA =
    {
        new(@"C:\a\img_0001.raw", 1000),
        new(@"C:\a\img_0002.raw", 1000),
        new(@"C:\a\img_0003.raw", 1000),
    };

    private static readonly SequenceFile[] FolderB =
    {
        new(@"C:\b\cap_0001.raw", 1000),
        new(@"C:\b\cap_0002.raw", 1000),
    };

    [Fact]
    public void ListOfCurrentFileFolder_IsUsed()
    {
        var source = new SequenceCandidateSource();

        Assert.Equal(FolderA, source.Resolve(@"C:\a\img_0001.raw", @"C:\a", FolderA));
    }

    [Fact]
    public void ListOfOtherFolder_UsesListingOfCurrentFileFolderSeenBefore()
    {
        // A の img_0001 を開いた(一覧は A)後、フォルダツリーで B へ移った。画像は A のまま。
        // 以前は B の一覧を候補にし、F2・HDR表示からの戻りで B の cap_ を連番にし、Ctrl+B で B を書き出していた
        var source = new SequenceCandidateSource();
        source.Resolve(@"C:\a\img_0001.raw", @"C:\a", FolderA);

        IReadOnlyList<SequenceFile> candidates = source.Resolve(@"C:\a\img_0001.raw", @"C:\b", FolderB);

        Assert.Equal(FolderA, candidates);
        Assert.Equal(
            new[] { @"C:\a\img_0001.raw", @"C:\a\img_0002.raw", @"C:\a\img_0003.raw" },
            SequenceScanner.FindStack(@"C:\a\img_0001.raw", 1000, candidates));
    }

    [Fact]
    public void ListOfOtherFolder_WithoutListingOfCurrentFileFolder_IsEmpty()
    {
        // 表示中のファイルのフォルダの一覧を一度も見ていない(一覧の列挙が別のフォルダの読み込みに追い越されたなど)。
        // 別のフォルダのファイルを候補にせず、連番なしとする
        var source = new SequenceCandidateSource();

        Assert.Empty(source.Resolve(@"C:\a\img_0001.raw", @"C:\b", FolderB));
    }

    [Fact]
    public void ListingSeenForAnotherFile_IsNotUsedForFileInOtherFolder()
    {
        var source = new SequenceCandidateSource();
        source.Resolve(@"C:\a\img_0001.raw", @"C:\a", FolderA);

        Assert.Empty(source.Resolve(@"C:\c\x_0001.raw", @"C:\b", FolderB));
    }

    [Fact]
    public void ListingOfCurrentFileFolder_IsReplacedByNewerListing()
    {
        // 同じフォルダを読み直した一覧(ファイルが増えた)を、次からの候補にする
        var source = new SequenceCandidateSource();
        source.Resolve(@"C:\a\img_0001.raw", @"C:\a", FolderA.Take(2));
        source.Resolve(@"C:\a\img_0001.raw", @"C:\a", FolderA);

        Assert.Equal(FolderA, source.Resolve(@"C:\a\img_0001.raw", @"C:\b", FolderB));
    }

    [Fact]
    public void RememberedListing_IsSnapshotOfList()
    {
        // 一覧(ObservableCollection)は後で別のフォルダの内容に差し替わるので、覚えるのはその時点の内容
        var list = new List<SequenceFile>(FolderA);
        var source = new SequenceCandidateSource();
        source.Resolve(@"C:\a\img_0001.raw", @"C:\a", list);
        list.Clear();
        list.AddRange(FolderB);

        Assert.Equal(FolderA, source.Resolve(@"C:\a\img_0001.raw", @"C:\b", list));
    }

    [Fact]
    public void FolderComparison_IgnoresCaseAndTrailingSeparator()
    {
        var source = new SequenceCandidateSource();

        Assert.Equal(FolderA, source.Resolve(@"C:\A\IMG_0001.raw", @"c:\a\", FolderA));
    }

    [Fact]
    public void NoListFolder_IsEmpty()
    {
        var source = new SequenceCandidateSource();

        Assert.Empty(source.Resolve(@"C:\a\img_0001.raw", null, Array.Empty<SequenceFile>()));
    }
}
