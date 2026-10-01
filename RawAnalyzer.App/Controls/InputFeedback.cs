using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace RawAnalyzer.App.Controls;

/// <summary>
/// 入力欄の「不正」の見せ方(赤枠と、ツールチップに理由)。
/// </summary>
/// <remarks>
/// ファイル一覧の絞り込み欄と数値入力欄で同じ見せ方にするため、ここ1か所で扱う。
/// <see cref="ErrorProperty"/> に理由を入れると <see cref="HasErrorProperty"/> が true になり、テーマ
/// (Themes/DarkTheme.xaml)の TextBox・ComboBox のテンプレートが枠を赤くする(フォーカス中・ポイント中も)。
/// ツールチップは理由に差し替え、null・空に戻すと元のツールチップへ戻す。
/// ツールチップは要素に直接書かれていること(XAML の <c>ToolTip="…"</c>)が多く、直接の値はスタイルのトリガーより
/// 優先されて差し替わらないので、トリガーではなくここで差し替えて元の値を覚えておく。
/// 不正の間に呼び出し側がツールチップを書き換えたときは、その値を残す(戻すのは理由を出したままのときだけ)。
/// </remarks>
public static class InputFeedback
{
    /// <summary>入力が不正な理由。null・空なら正常。</summary>
    public static readonly DependencyProperty ErrorProperty = DependencyProperty.RegisterAttached(
        "Error", typeof(string), typeof(InputFeedback), new PropertyMetadata(null, OnErrorChanged));

    private static readonly DependencyPropertyKey HasErrorPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "HasError", typeof(bool), typeof(InputFeedback), new PropertyMetadata(false));

    /// <summary>入力が不正か(<see cref="ErrorProperty"/> が空でないか)。テーマの赤枠のトリガーに使う。</summary>
    public static readonly DependencyProperty HasErrorProperty = HasErrorPropertyKey.DependencyProperty;

    // 理由へ差し替える前のツールチップ(直接の値)と、差し替えた理由
    private static readonly DependencyProperty ReplacedToolTipProperty = DependencyProperty.RegisterAttached(
        "ReplacedToolTip", typeof(ReplacedToolTip), typeof(InputFeedback), new PropertyMetadata(null));

    /// <summary>入力が不正な理由を取得する。</summary>
    /// <param name="element">入力欄。</param>
    /// <returns>理由。正常なら null。</returns>
    public static string? GetError(DependencyObject element) => (string?)element.GetValue(ErrorProperty);

    /// <summary>入力が不正な理由を設定する。null・空で正常へ戻す。</summary>
    /// <param name="element">入力欄。</param>
    /// <param name="value">理由。</param>
    public static void SetError(DependencyObject element, string? value) => element.SetValue(ErrorProperty, value);

    /// <summary>入力が不正かを取得する。</summary>
    /// <param name="element">入力欄。</param>
    /// <returns>不正なら true。</returns>
    public static bool GetHasError(DependencyObject element) => (bool)element.GetValue(HasErrorProperty);

    /// <summary>
    /// 入力が正しいかに応じて、欄の不正の表示を付ける・外す。
    /// </summary>
    /// <param name="element">入力欄。</param>
    /// <param name="valid">入力が正しいか。</param>
    /// <param name="reason">正しくないときの理由。</param>
    /// <returns><paramref name="valid"/> をそのまま返す。</returns>
    internal static bool Check(DependencyObject element, bool valid, string reason)
    {
        SetError(element, valid ? null : reason);
        return valid;
    }

    private static void OnErrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        string? error = e.NewValue as string;
        var replaced = (ReplacedToolTip?)d.GetValue(ReplacedToolTipProperty);
        if (!string.IsNullOrEmpty(error))
        {
            if (replaced is null)
            {
                replaced = new ReplacedToolTip(d.ReadLocalValue(ToolTipService.ToolTipProperty));
                d.SetValue(ReplacedToolTipProperty, replaced);
            }

            replaced.Shown = error;
            d.SetValue(ToolTipService.ToolTipProperty, error);
            d.SetValue(HasErrorPropertyKey, true);
            return;
        }

        d.ClearValue(HasErrorPropertyKey);
        if (replaced is null)
        {
            return;
        }

        d.ClearValue(ReplacedToolTipProperty);
        if (!ReferenceEquals(d.ReadLocalValue(ToolTipService.ToolTipProperty), replaced.Shown))
        {
            return; // 不正の間に呼び出し側が書き換えた。その値を残す
        }

        switch (replaced.Original)
        {
            case BindingExpressionBase binding:
                BindingOperations.SetBinding(d, ToolTipService.ToolTipProperty, binding.ParentBindingBase);
                break;
            case var original when original == DependencyProperty.UnsetValue:
                d.ClearValue(ToolTipService.ToolTipProperty); // スタイルなどが与えていた値へ戻る
                break;
            case var original:
                d.SetValue(ToolTipService.ToolTipProperty, original);
                break;
        }
    }

    private sealed class ReplacedToolTip(object original)
    {
        public object Original { get; } = original;

        public string Shown { get; set; } = "";
    }
}
