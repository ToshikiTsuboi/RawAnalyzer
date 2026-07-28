using System.Text;
using System.Windows.Input;

namespace RawAnalyzer.App.Services;

/// <summary>
/// キーボードから実行できる操作の定義。
/// </summary>
/// <remarks>
/// ショートカット・コマンドパレット・ショートカット一覧の3つが同じ定義を参照するため、
/// 割り当ての食い違いが起きない。
/// </remarks>
internal sealed class AppCommand
{
    /// <summary>コマンドの識別子(重複検査用)。</summary>
    public required string Id { get; init; }

    /// <summary>分類(コマンドパレットと一覧の見出し)。</summary>
    public required string Category { get; init; }

    /// <summary>表示名。</summary>
    public required string Title { get; init; }

    /// <summary>実行内容。</summary>
    public required Action Execute { get; init; }

    /// <summary>割り当てキー(Key.None ならショートカットなし)。</summary>
    public Key Key { get; init; } = Key.None;

    /// <summary>修飾キー。</summary>
    public ModifierKeys Modifiers { get; init; } = ModifierKeys.None;

    /// <summary>実行可能かどうか(省略時は常に実行可能)。</summary>
    public Func<bool>? CanExecute { get; init; }

    /// <summary>補足説明(コマンドパレットの2行目)。</summary>
    public string Description { get; init; } = "";

    /// <summary>ショートカットを持つか。</summary>
    public bool HasGesture => Key != Key.None;

    /// <summary>補足説明を持つか(バインドの表示切替に使う)。</summary>
    public bool HasDescription => Description.Length > 0;

    /// <summary>検索対象の文字列(分類 + 表示名 + 説明)。</summary>
    public string SearchText => $"{Category} {Title} {Description}";

    /// <summary>「Ctrl+Shift+P」形式のショートカット表記。</summary>
    public string GestureText => HasGesture ? FormatGesture(Key, Modifiers) : "";

    /// <summary>実行可能か判定する。</summary>
    /// <returns>実行可能ならtrue。</returns>
    public bool IsEnabled() => CanExecute?.Invoke() ?? true;

    /// <summary>キー組み合わせを表示用文字列にする。</summary>
    /// <param name="key">キー。</param>
    /// <param name="modifiers">修飾キー。</param>
    /// <returns>「Ctrl+Shift+P」形式の文字列。</returns>
    public static string FormatGesture(Key key, ModifierKeys modifiers)
    {
        var builder = new StringBuilder();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            builder.Append("Ctrl+");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            builder.Append("Alt+");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            builder.Append("Shift+");
        }

        builder.Append(FormatKey(key));
        return builder.ToString();
    }

    private static string FormatKey(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemPlus => "+",
        Key.OemMinus => "-",
        Key.OemQuestion => "/",
        Key.OemOpenBrackets => "[",
        Key.Oem6 => "]",
        Key.Prior => "PageUp",
        Key.Next => "PageDown",
        Key.Escape => "Esc",
        Key.Return => "Enter",
        _ => key.ToString(),
    };
}
