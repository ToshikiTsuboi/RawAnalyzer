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

    /// <summary>
    /// 同じコマンドを実行する別のキー(一覧・パレットの表記は <see cref="Key"/> と <see cref="Modifiers"/> だけ)。
    /// </summary>
    public IReadOnlyList<ShortcutKey> AlternateGestures { get; init; } = Array.Empty<ShortcutKey>();

    /// <summary>実行可能かどうか(省略時は常に実行可能)。</summary>
    /// <remarks>
    /// 前提となる状態(画像・比較モードでないこと・送れるフレーム・ROI)の有無だけを判定する。
    /// 実行してみないと分からない条件や、状態を取り違えやすい条件(実行中の処理がある・raw 以外での
    /// フォーマット変更)はここに入れず、実行時に理由をダイアログで示す(パレット・キーとも同じ)。
    /// </remarks>
    public Func<bool>? CanExecute { get; init; }

    /// <summary>
    /// 実行できないとき(<see cref="CanExecute"/> が false)に利用者へ示す理由(省略可)。
    /// </summary>
    /// <remarks>
    /// コマンドパレットは実行できないコマンドを薄く表示し、この理由を示す(Enter でも実行せず理由を示す)。
    /// ショートカットキーからは従来どおり黙って無視し、キーはフォーカス中のコントロールへ渡す
    /// (1文字キーの打鍵のたびに知らせない。メニュー・ボタンは同じ条件で無効表示にしている)。
    /// 省略時は <see cref="DefaultDisabledReason"/> を示す。
    /// </remarks>
    public Func<string>? DisabledReason { get; init; }

    /// <summary>理由を指定していないコマンドが実行できないときに示す文言。</summary>
    public const string DefaultDisabledReason = "今の状態では実行できません。";

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

    /// <summary>割り当てたキー(主のキーと <see cref="AlternateGestures"/>)。</summary>
    public IEnumerable<ShortcutKey> Gestures => HasGesture
        ? AlternateGestures.Prepend(new ShortcutKey(Key, Modifiers))
        : AlternateGestures;

    /// <summary>キーがこのコマンドに割り当てたもの(別のキーを含む)か。修飾キーは完全一致で比べる。</summary>
    /// <param name="key">キー。</param>
    /// <param name="modifiers">修飾キー。</param>
    /// <returns>割り当てたキーなら true。</returns>
    public bool Matches(Key key, ModifierKeys modifiers) =>
        key != Key.None && Gestures.Contains(new ShortcutKey(key, modifiers));

    /// <summary>実行可能か判定する。</summary>
    /// <returns>実行可能ならtrue。</returns>
    public bool IsEnabled() => CanExecute?.Invoke() ?? true;

    /// <summary>今実行できない理由を返す。</summary>
    /// <returns>実行できるときはnull。実行できないときは理由(指定がなければ既定の文言)。</returns>
    public string? GetDisabledReason()
    {
        if (IsEnabled())
        {
            return null;
        }

        string? reason = DisabledReason?.Invoke();
        return string.IsNullOrWhiteSpace(reason) ? DefaultDisabledReason : reason;
    }

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

/// <summary>ショートカットのキーと修飾キーの組。</summary>
/// <param name="Key">キー。</param>
/// <param name="Modifiers">修飾キー。</param>
internal readonly record struct ShortcutKey(Key Key, ModifierKeys Modifiers);
