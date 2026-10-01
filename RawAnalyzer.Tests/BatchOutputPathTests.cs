using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 一括書き出しの静止画の出力パスと、元ファイルを置き換えないための確認。
/// </summary>
public class BatchOutputPathTests
{
    private const string Folder = @"C:\data\seq";

    [Theory]
    [InlineData("img_001.png", ".png")] // PNG 連番 → PNG
    [InlineData("img_001.jpg", ".jpg")] // JPEG 連番 → JPEG
    [InlineData("img_001.tif", ".tif")] // 1ページの TIFF 連番 → TIFF16
    [InlineData("IMG_001.PNG", ".png")] // 大文字小文字は区別しない(Windows のファイル名と同じ)
    public void SourceFolderAsOutput_SingleImageWouldReplaceSource_IsRefused(string name, string extension)
    {
        // 全体レビュー 2026-10-01 B19。出力先に元のフォルダを選ぶと、1枚ものの出力名は元の名前に
        // 拡張子を付け直すだけなので、同じ拡張子の元画像が確認なしで焼き込み結果(8bit・輝度だけの Gray16)に
        // 置き換わり、センサデータを失っていた。元ファイルと同じパスへは書かずに中止する
        string source = Path.Combine(Folder, name);
        var guard = new BatchSourceGuard(new[] { source, Path.Combine(Folder, "img_002" + extension) });

        string output = OutputPaths.BatchImagePath(Folder, source, 0, 1, false, extension);

        IOException refused = Assert.Throws<IOException>(() => guard.EnsureNotSource(output));
        Assert.Contains(Path.GetFileName(output), refused.Message);
        Assert.Contains("元ファイル", refused.Message);
    }

    [Fact]
    public void SourceFolderWrittenDifferently_IsStillRefused()
    {
        // 末尾の区切りや「.」を含む書き方でも同じフォルダとして扱う
        string source = Path.Combine(Folder, "img_001.png");
        var guard = new BatchSourceGuard(new[] { source });

        string output = OutputPaths.BatchImagePath(Folder + @"\.\", source, 0, 1, false, ".png");

        Assert.Throws<IOException>(() => guard.EnsureNotSource(output));
    }

    [Theory]
    [InlineData("export2", @"C:\data\seq\export2")]
    [InlineData(@"out\run1", @"C:\data\seq\out\run1")]
    [InlineData(@"..\out", @"C:\data\out")]
    [InlineData(@".\", @"C:\data\seq\")]
    [InlineData(@"\out", @"C:\out")] // ドライブの根からの相対は元画像のドライブ
    [InlineData(@"E:\export", @"E:\export")] // 絶対パスはそのまま
    [InlineData(@"\\nas01\share\export", @"\\nas01\share\export")]
    public void RelativeOutputFolder_IsResolvedAgainstImageFolder(string input, string expected)
    {
        // 全体レビュー 2026-10-01 B83。出力先に相対パス(例 export2)を入れると、元画像のフォルダではなく
        // プロセスのカレントディレクトリ(exe の場所など)の下へ書き出し、完了表示も相対パスのままで
        // 出力の場所が分からなかった。元画像のフォルダを基準に絶対パスにする
        Assert.True(OutputPaths.TryResolveOutputFolder(input, Folder, out string folder));

        Assert.Equal(expected, folder);
        Assert.True(Path.IsPathFullyQualified(folder));
    }

    [Fact]
    public void OutputFolderThatIsNotAPath_IsRejected()
    {
        Assert.False(OutputPaths.TryResolveOutputFolder("out\0put", Folder, out _));
    }

    [Theory]
    [InlineData(@"C:\data\seq\export", "img_001.png", 0, 1, false, ".png", @"C:\data\seq\export\img_001.png")]
    [InlineData(Folder, "img_001.tiff", 0, 1, false, ".tif", @"C:\data\seq\img_001.tif")] // .tiff とは別名
    [InlineData(Folder, "stack.tif", 0, 3, true, ".tif", @"C:\data\seq\stack_p0001.tif")] // 複数ページは _p
    [InlineData(Folder, "burst.raw", 2, 4, false, ".png", @"C:\data\seq\burst_f002.png")] // 複数フレームは _f
    [InlineData(Folder, "img_001.png", 0, 1, false, ".jpg", @"C:\data\seq\img_001.jpg")] // 拡張子が違う
    public void OutputThatDoesNotReplaceSource_IsWritten(
        string folder, string name, int index, int count, bool isTiffPage, string extension, string expected)
    {
        string source = Path.Combine(Folder, name);
        var guard = new BatchSourceGuard(new[] { source });

        string output = OutputPaths.BatchImagePath(folder, source, index, count, isTiffPage, extension);

        Assert.Equal(expected, output);
        guard.EnsureNotSource(output);
    }
}
