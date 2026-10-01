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
    [InlineData("*.raw, *.tif", "a.raw", true)]            // , 区切り(区切らないと "*.raw," になって一致しない)
    [InlineData(".tif .tiff", "scan.tiff", true)]          // 空白区切りの拡張子
    [InlineData("dark*", "dark_001.raw", true)]            // 前方一致
    [InlineData("dark*", "flat_dark.raw", false)]          // ワイルドカードは名前全体に掛かる
    [InlineData("img_00?.raw", "img_007.raw", true)]       // ? は 1 文字
    [InlineData("img_00?.raw", "img_0071.raw", false)]
    [InlineData("*.raw", "a.raw.bak", false)]              // ワイルドカードは名前の末尾まで
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
        // 先読みを含むパターンは線形時間の照合を使えず、バックトラックで照合するので時間切れがありうる
        FileNameFilter parsed = FileNameFilter.Parse(@"/^(?=(\w+)+$)/");
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

    /// <summary>
    /// 入れ子の量指定子のように、バックトラックでは名前の長さに対して指数的に時間の掛かるパターンも、
    /// 後方参照・先読み・後読みを含まなければ名前の長さに比例する時間で照合し、正しく絞り込む。
    /// 以前はバックトラックで照合したので、1 件ごとには上限(100ms)未満の短い名前でも件数ぶん積み上がって
    /// UI スレッドが止まり(22 文字の名前で 1 件約 20ms、200 件で約 4 秒)、長い名前では時間切れで絞り込みをやめていた。
    /// </summary>
    [Theory]
    [InlineData(22)]
    [InlineData(250)]
    public void IsMatch_NestedQuantifierPattern_TakesTimeProportionalToNameLength(int nameLength)
    {
        FileNameFilter parsed = FileNameFilter.Parse(@"/^(\w+)+$/");
        string digits = "D" + (nameLength - 8).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string[] names = Enumerable.Range(0, 200)
            .Select(i => $"img_{i.ToString(digits, System.Globalization.CultureInfo.InvariantCulture)}.raw")
            .ToArray();
        Assert.All(names, name => Assert.Equal(nameLength, name.Length));

        var stopwatch = Stopwatch.StartNew();
        bool[] matched = names.Select(parsed.IsMatch).ToArray();
        stopwatch.Stop();

        Assert.Null(parsed.Error);
        Assert.All(matched, Assert.False); // "." は \w ではないので一致しない(時間切れで全件表示に倒さない)
        Assert.True(parsed.IsMatch(new string('a', nameLength)));
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"{stopwatch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// 後方参照・先読み・後読みは線形時間の照合では扱えないので、従来どおりバックトラック(時間切れ付き)で絞り込む。
    /// </summary>
    [Theory]
    [InlineData(@"/(\d)\1/", "img_0011.raw", true)]     // 後方参照
    [InlineData(@"/(\d)\1/", "img_0123.raw", false)]
    [InlineData(@"/^(?!dark)/", "dark_001.raw", false)] // 否定先読み
    [InlineData(@"/^(?!dark)/", "flat_001.raw", true)]
    [InlineData(@"/(?<=_)0/", "img_012.raw", true)]     // 後読み
    [InlineData(@"/(?<=_)0/", "img0_12.raw", false)]
    public void IsMatch_BacktrackingOnlyConstructs_StillFilter(string filter, string fileName, bool expected)
    {
        FileNameFilter parsed = FileNameFilter.Parse(filter);

        Assert.True(parsed.IsRegex);
        Assert.Equal(expected, parsed.IsMatch(fileName));
        Assert.Null(parsed.Error);
    }
}
