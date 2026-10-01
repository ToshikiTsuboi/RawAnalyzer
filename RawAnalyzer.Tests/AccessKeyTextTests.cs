using System.Windows.Controls;
using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 「最近使ったファイル」の項目名など、アクセスキーを解釈するメニューの見出しへファイル名を文字どおりに出すことの検証。
/// </summary>
[Collection("WPF UI")]
public class AccessKeyTextTests
{
    [Theory]
    [InlineData("dark_10ms_001.raw", "dark__10ms__001.raw")]
    [InlineData("img_a.raw", "img__a.raw")]
    [InlineData("imga.raw", "imga.raw")]
    [InlineData("__x_", "____x__")]
    [InlineData("", "")]
    public void Escape_DoublesUnderscores(string text, string expected)
    {
        Assert.Equal(expected, AccessKeyText.Escape(text));
    }

    [Fact]
    public Task EscapedFileName_HasNoAccessKey() => WpfTestHost.Run(() =>
    {
        // メニュー項目の見出しは AccessText として表示される。以前はファイル名をそのまま渡していたので、
        // 最初の「_」が消えて次の文字がアクセスキーになり、dark_10ms_001.raw が「dark10ms_001.raw」と出ていた
        var plain = new AccessText { Text = "dark_10ms_001.raw" };
        var escaped = new AccessText { Text = AccessKeyText.Escape("dark_10ms_001.raw") };

        Assert.Equal('1', plain.AccessKey);
        Assert.Equal('\0', escaped.AccessKey);
    });
}
