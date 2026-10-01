using System.Windows;
using System.Windows.Controls;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 画像演算ダイアログの参照パス欄のサイズ照合。
/// </summary>
[Collection("WPF UI")]
public class ImageCalculatorDialogTests : IDisposable
{
    private readonly string _directory;

    public ImageCalculatorDialogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public Task TypingReferencePath_ChecksSizeOffTheUiThreadAfterInputPauses()
    {
        // 全体レビュー 2026-10-01 B87。参照パス欄は1打鍵ごとに UI スレッドで File.Exists と FileInfo を呼び、
        // 到達できない NAS の UNC パスを打つ間、名前解決・SMB のタイムアウトのたびにダイアログが固まっていた。
        // 入力の変更では同期で確かめず、入力が止まってから裏で確かめて結果を示す
        string mismatched = CreateFile("dark.raw", 100);
        return WpfTestHost.Run(async () =>
        {
            var dialog = new ImageCalculatorDialog("A", _directory, expectedSize: 200);
            try
            {
                TextBlock note = (TextBlock)dialog.FindName("NoteText");
                ((TextBox)dialog.FindName("ReferenceBox")).Text = mismatched;

                Assert.Equal(Visibility.Collapsed, note.Visibility);

                await WaitUntil(() => note.Visibility == Visibility.Visible);
                Assert.Contains("100", note.Text);
                Assert.Contains("200", note.Text);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public Task EditingAfterMismatch_DiscardsTheStaleResult()
    {
        // 前の入力の照合結果が後から届いても、今の入力(サイズが一致するファイル)の表示を上書きしない
        string mismatched = CreateFile("dark.raw", 100);
        string matched = CreateFile("dark2.raw", 200);
        return WpfTestHost.Run(async () =>
        {
            var dialog = new ImageCalculatorDialog("A", _directory, expectedSize: 200);
            try
            {
                TextBlock note = (TextBlock)dialog.FindName("NoteText");
                TextBox box = (TextBox)dialog.FindName("ReferenceBox");
                box.Text = mismatched;
                await WaitUntil(() => note.Visibility == Visibility.Visible);

                box.Text = matched;

                // 入力を変えたら前の警告は消し、一致するファイルでは警告を出さない
                Assert.Equal(Visibility.Collapsed, note.Visibility);
                await Task.Delay(1000);
                Assert.Equal(Visibility.Collapsed, note.Visibility);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private string CreateFile(string name, long size)
    {
        string path = Path.Combine(_directory, name);
        using FileStream stream = File.Create(path);
        stream.SetLength(size);
        return path;
    }
}
