using System.Windows.Input;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

public class KeyboardCommandTests
{
    [Theory]
    [InlineData(Key.F1, ModifierKeys.None, "F1")]          // 既定分岐(キー名そのまま)
    [InlineData(Key.D3, ModifierKeys.Control, "Ctrl+3")]   // D0〜D9 は数字へ
    [InlineData(Key.Prior, ModifierKeys.None, "PageUp")]   // 変換表
    [InlineData(Key.A, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift,
        "Ctrl+Alt+Shift+A")]                                // 修飾キーの順序
    public void FormatGesture_ProducesReadableText(Key key, ModifierKeys modifiers, string expected)
    {
        Assert.Equal(expected, AppCommand.FormatGesture(key, modifiers));
    }

    [Fact]
    public void GetDisabledReason_IsNullWhileExecutable()
    {
        // 実行できるコマンドには理由を出さない(理由の関数は呼ばない)
        var always = new AppCommand { Id = "a", Category = "c", Title = "t", Execute = () => { } };
        var enabled = new AppCommand
        {
            Id = "b",
            Category = "c",
            Title = "t",
            Execute = () => { },
            CanExecute = () => true,
            DisabledReason = () => throw new InvalidOperationException("実行できるときに理由を作った"),
        };

        Assert.Null(always.GetDisabledReason());
        Assert.Null(enabled.GetDisabledReason());
    }

    [Theory]
    [InlineData("画像を開いていないため実行できません。", "画像を開いていないため実行できません。")]
    [InlineData(null, AppCommand.DefaultDisabledReason)] // 理由を指定していなければ既定の文言
    [InlineData(" ", AppCommand.DefaultDisabledReason)]  // 空の理由では何も分からないので既定の文言
    public void GetDisabledReason_ExplainsWhyItCannotRun(string? reason, string expected)
    {
        // 以前はコマンドパレットで実行できないコマンドを選んで Enter を押しても何も起きなかった
        var command = new AppCommand
        {
            Id = "save",
            Category = "ファイル",
            Title = "保存…",
            Execute = () => { },
            CanExecute = () => false,
            DisabledReason = reason is null ? null : () => reason,
        };

        Assert.False(command.IsEnabled());
        Assert.Equal(expected, command.GetDisabledReason());
    }

    [Theory]
    [InlineData("", "解析 ノイズ測定", true)]
    [InlineData("測定 解析", "解析 ノイズ測定", true)]   // 語順は問わない
    [InlineData("ノイズ 欠陥", "解析 ノイズ測定", false)] // 全語一致が必要
    [InlineData("hist", "解析 Histogram", true)]        // 大小文字を区別しない
    public void Matches_RequiresAllTerms(string query, string target, bool expected)
    {
        Assert.Equal(expected, CommandPaletteWindow.Matches(query, target));
    }

    /// <summary>
    /// IME をオンのまま打った検索語(語の間の全角スペース、全角英字)でも一致する。
    /// 以前は半角スペースでしか区切らず、「ヒストグラム　コピー」は1語として探して何にも一致しなかった。
    /// </summary>
    [Theory]
    [InlineData("ヒストグラム　コピー", "解析 ヒストグラムをクリップボードへコピー", true)]
    [InlineData("ヒストグラム　欠陥", "解析 ヒストグラムをクリップボードへコピー", false)] // 全語一致は従来どおり
    [InlineData("ヒストグラム\tコピー", "解析 ヒストグラムをクリップボードへコピー", true)]
    [InlineData("ＲＯＩ", "ROI ROIを解除", true)]  // 全角英字
    [InlineData("ｒｏｉ", "ROI ROIを解除", true)]  // 全角英字(大小文字も区別しない)
    public void Matches_SplitsOnAnyWhitespace_AndIgnoresWidth(string query, string target, bool expected)
    {
        Assert.Equal(expected, CommandPaletteWindow.Matches(query, target));
    }
}
