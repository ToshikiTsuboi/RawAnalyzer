using System.Windows.Controls;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 一括書き出しダイアログの「参照…」で始めるフォルダ(残課題 2026-10-02 I1)。
/// </summary>
[Collection("WPF UI")]
public class BatchExportBrowseFolderTests
{
    private const string SourceFolder = @"C:\data\shots";

    [Theory]
    [InlineData("export2", @"C:\data\shots\export2")]
    [InlineData(@"..\out", @"C:\data\out")]
    [InlineData(@"D:\exports", @"D:\exports")]
    [InlineData("", SourceFolder)]
    public Task RelativeOutputFolder_IsResolvedAgainstTheSourceFolder(string input, string expected)
    {
        // 以前は入力をそのまま(UI スレッドの)Directory.Exists に渡し、相対パスをプロセスのカレントディレクトリ
        // (exe の場所など)基準で探していた。「実行」と同じく元画像のフォルダを基準に解決する
        return WpfTestHost.Run(() =>
        {
            var dialog = new BatchExportDialog(3, Path.Combine(SourceFolder, "export"), sourceFolder: SourceFolder);
            ((TextBox)dialog.FindName("OutputFolderBox")).Text = input;

            Assert.Equal(expected, dialog.BrowseStartFolder());
            dialog.Close();
        });
    }

    [Fact]
    public Task UnparsableOutputFolder_StartsWithoutAFolder()
    {
        return WpfTestHost.Run(() =>
        {
            var dialog = new BatchExportDialog(3, Path.Combine(SourceFolder, "export"), sourceFolder: SourceFolder);
            ((TextBox)dialog.FindName("OutputFolderBox")).Text = "out\0put";

            Assert.Null(dialog.BrowseStartFolder());
            dialog.Close();
        });
    }
}
