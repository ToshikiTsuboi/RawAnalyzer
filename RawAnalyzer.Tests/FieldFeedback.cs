using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 入力欄の「不正」の見せ方(ファイル一覧の絞り込み欄と同じ、赤枠とツールチップの理由)を確かめる。
/// </summary>
/// <remarks>
/// テーマ(DarkTheme.xaml)の TextBox・ComboBox のテンプレートの枠(Bd)の色と、欄のツールチップを見る。
/// </remarks>
internal static class FieldFeedback
{
    /// <summary>不正な欄の枠の色(絞り込み欄で使っていた赤)。</summary>
    internal static readonly Color ErrorColor = Color.FromRgb(0xD9, 0x60, 0x5B);

    /// <summary>欄が不正の見せ方になっていることを確かめ、ツールチップの理由を返す。</summary>
    internal static string AssertInvalid(Control field)
    {
        Assert.Equal(ErrorColor, BorderColor(field));
        return Assert.IsType<string>(field.ToolTip);
    }

    /// <summary>欄が不正の見せ方になっていないことを確かめる。</summary>
    internal static void AssertValid(Control field)
    {
        Assert.NotEqual(ErrorColor, BorderColor(field));
    }

    private static Color BorderColor(Control field)
    {
        if (!field.IsInitialized)
        {
            // コードで作った欄は初期化(BeginInit/EndInit)まで暗黙のスタイル(テーマ)が当たらない
            field.BeginInit();
            field.EndInit();
        }

        field.ApplyTemplate();
        var border = Assert.IsType<Border>(field.Template.FindName("Bd", field));
        return Assert.IsType<SolidColorBrush>(border.BorderBrush).Color;
    }
}
