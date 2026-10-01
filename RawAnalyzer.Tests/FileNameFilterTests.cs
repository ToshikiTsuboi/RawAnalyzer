using System.Diagnostics;
using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>ファイル一覧の絞り込み条件の書式。</summary>
public class FileNameFilterTests
{
    [Theory]
    [InlineData(".raw", "dark_001.tif", false)]            // 拡張子
    [InlineData(".RAW", "dark_001.raw", true)]             // 大文字小文字を区別しない
    [InlineData("*.raw;*.tif", "scene.TIF", true)]         // 複数条件の OR(; 区切り)
    [InlineData("*.raw, *.tif", "scene.png", false)]       // , 区切り
    [InlineData(".tif .tiff", "scan.tiff", true)]          // 空白区切りの拡張子
    [InlineData("dark*", "dark_001.raw", true)]            // 前方一致
    [InlineData("dark*", "flat_dark.raw", false)]          // ワイルドカードは名前全体に掛かる
    [InlineData("img_00?.raw", "img_007.raw", true)]       // ? は 1 文字
    [InlineData("img_00?.raw", "img_0071.raw", false)]
    [InlineData("dark", "flat_dark.raw", true)]            // 素の語は部分一致
    [InlineData("a.b", "a+b.raw", false)]                  // . を含む語もワイルドカードでなければ部分一致(正規表現ではない)
    [InlineData(@"/^img_\d{3}\.raw$/", "img_12.raw", false)]  // 正規表現
    [InlineData("/RAW$/", "a.raw", true)]                  // 正規表現も大文字小文字を区別しない
    public void IsMatch_FollowsDocumentedSyntax(string filter, string fileName, bool expected)
    {
        FileNameFilter parsed = FileNameFilter.Parse(filter);

        Assert.Null(parsed.Error);
        Assert.Equal(expected, parsed.IsMatch(fileName));
    }

    /// <summary>
    /// IME をオンのまま区切った複数条件(全角スペース・全角の ；，)も、いずれかに一致すれば表示する。
    /// 以前は語の間の全角スペースで区切らず、「暗室　フラット」を1語の部分一致として探して何にも一致しなかった。
    /// </summary>
    [Theory]
    [InlineData("暗室　フラット", "暗室_001.raw", true)]
    [InlineData("暗室　フラット", "フラット_001.raw", true)]
    [InlineData("暗室　フラット", "白_001.raw", false)]
    [InlineData("暗室；フラット", "フラット_001.raw", true)]
    [InlineData("暗室，フラット", "フラット_001.raw", true)]
    [InlineData("暗室　　.tif", "scan.tif", true)] // 続いた区切りは1つとみなす
    public void IsMatch_SplitsOnFullWidthSeparators(string filter, string fileName, bool expected)
    {
        FileNameFilter parsed = FileNameFilter.Parse(filter);

        Assert.Null(parsed.Error);
        Assert.Equal(expected, parsed.IsMatch(fileName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("//")]
    public void Parse_BlankInput_IsEmpty(string? text)
    {
        FileNameFilter parsed = FileNameFilter.Parse(text);

        Assert.True(parsed.IsEmpty);
        Assert.False(parsed.IsRegex);
        Assert.True(parsed.IsMatch("x.raw"));
    }

    [Fact]
    public void Parse_TrimsAndKeepsText()
    {
        FileNameFilter parsed = FileNameFilter.Parse("  *.raw  ");

        Assert.Equal("*.raw", parsed.Text);
        Assert.False(parsed.IsRegex);
        Assert.True(FileNameFilter.Parse("/a/").IsRegex);
    }

    /// <summary>
    /// 破滅的なバックトラックを起こす正規表現は、最初の時間切れで条件を不正とし、残りのファイルは照合しない。
    /// 以前は1件ごとに時間切れ(100ms)まで回ったので、長い名前のファイルが多いフォルダでは
    /// ファイル数×100ms の間 UI スレッドが止まった。
    /// </summary>
    [Fact]
    public void IsMatch_RegexTimeout_MarksFilterInvalidAndStopsMatching()
    {
        FileNameFilter parsed = FileNameFilter.Parse(@"/^(\w+)+$/");
        Assert.Null(parsed.Error);
        string[] names = Enumerable.Range(0, 20)
            .Select(i => $"capture_20260930_{i:D6}_long_exposure_frame.raw")
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        Assert.All(names, name => Assert.True(parsed.IsMatch(name))); // 不正な条件はすべて一致(従来どおり)
        stopwatch.Stop();

        Assert.NotNull(parsed.Error);
        Assert.False(parsed.IsEmpty);
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"{stopwatch.ElapsedMilliseconds} ms"); // 以前は 2 秒以上
    }
}
