using System.Windows.Controls;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 一括書き出しダイアログのフレームレート欄(編集可能コンボ)の入力の知らせ方。
/// </summary>
[Collection("WPF UI")]
public class BatchExportDialogTests
{
    [Theory]
    [InlineData("abc", "既定の 15 fps")]  // 読めない入力
    [InlineData("-5", "既定の 15 fps")]   // 負の値
    [InlineData("0", "既定の 15 fps")]
    [InlineData("500", "240 fps")]       // 上限を超える値
    [InlineData("0.2", "1 fps")]         // 書き出せる下限(整数で 1 fps)を下回る値
    public Task FpsCombo_UnusableInput_ShowsWhatFrameRateIsUsed(string text, string expected) => WpfTestHost.Run(() =>
    {
        // 読めない・0 以下の入力は既定値へ、範囲外は範囲へ落として書き出す(挙動は従来どおり)。
        // 以前は黙って落としたので、打った値と違うフレームレートの動画になったことが見えなかった。
        // ファイル一覧の絞り込み欄と同じく、赤枠とツールチップの理由で知らせる
        var dialog = new BatchExportDialog(3, @"C:\capture\out", frameWidth: 640, frameHeight: 480);
        try
        {
            var format = (ComboBox)dialog.FindName("FormatCombo");
            var fps = (ComboBox)dialog.FindName("FpsCombo");
            format.SelectedIndex = 4; // MP4(フレームレート欄が出る)
            fps.ApplyTemplate();
            object help = fps.ToolTip;
            FieldFeedback.AssertValid(fps);

            fps.Text = text;
            Assert.Contains(expected, FieldFeedback.AssertInvalid(fps));

            fps.Text = "24 fps"; // 使える値へ直せば消え、元の説明へ戻る
            FieldFeedback.AssertValid(fps);
            Assert.Equal(help, fps.ToolTip);
        }
        finally
        {
            dialog.Close();
        }
    });
}
