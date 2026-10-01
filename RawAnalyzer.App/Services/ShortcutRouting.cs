using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace RawAnalyzer.App.Services;

/// <summary>
/// キーボードフォーカスのある要素の種類。キーをショートカットとフォーカス中のコントロールのどちらに渡すかを決める。
/// </summary>
internal enum ShortcutFocus
{
    /// <summary>ビューポート・ボタン・一覧など。ショートカットを優先する。</summary>
    Other,

    /// <summary>文字入力欄(TextBox・編集可能な ComboBox の入力欄)。</summary>
    TextEntry,

    /// <summary>編集できない ComboBox(閉じた状態)と、開いたドロップダウンの項目。</summary>
    Selector,

    /// <summary>メニュー(Alt を押して離したメニューモード・開いたメニュー・右クリックメニュー)。</summary>
    Menu,
}

/// <summary>
/// ウィンドウ全体のキー入力(PreviewKeyDown)を、コマンド表(<see cref="AppCommand"/>)のどのコマンドで実行するか、
/// またはフォーカス中のコントロールへ渡すかを決める。
/// </summary>
internal static class ShortcutRouting
{
    /// <summary>フォーカス中の要素の種類を判定する。</summary>
    /// <param name="focused">キーボードフォーカスのある要素(<see cref="Keyboard.FocusedElement"/>)。</param>
    /// <returns>要素の種類。</returns>
    internal static ShortcutFocus Classify(object? focused) => focused switch
    {
        // 編集可能な ComboBox は内部の TextBox がフォーカスを持つので TextBoxBase で拾える
        TextBoxBase or ComboBox { IsEditable: true } => ShortcutFocus.TextEntry,

        // 編集できない ComboBox は文字入力欄ではない。閉じた状態ではコンボ自身が、開いた状態では
        // ドロップダウンの項目がフォーカスを持つ
        ComboBox or ComboBoxItem => ShortcutFocus.Selector,

        // メニューモードではメニューの項目がフォーカスを持つ(右クリックメニューも同じ)
        MenuItem or MenuBase => ShortcutFocus.Menu,
        _ => ShortcutFocus.Other,
    };

    /// <summary>キーで実行するコマンドを決める。</summary>
    /// <param name="commands">コマンド表。</param>
    /// <param name="key">押されたキー(Alt 併用時は SystemKey)。</param>
    /// <param name="modifiers">修飾キー。</param>
    /// <param name="focus">フォーカス中の要素の種類。</param>
    /// <returns>
    /// 実行するコマンド。null ならキーを処理済みにせず、フォーカス中のコントロールへ渡す
    /// (割り当てがない・実行できない・フォーカス中のコントロールに譲るキー)。
    /// </returns>
    internal static AppCommand? Resolve(
        IEnumerable<AppCommand> commands, Key key, ModifierKeys modifiers, ShortcutFocus focus)
    {
        if (focus == ShortcutFocus.TextEntry && IsTextEditingGesture(key, modifiers))
        {
            // Ctrl+C(コピー)やCtrl+A(全選択)などの標準編集操作は
            // テキストボックスに渡す。ここで横取りすると選択テキストの
            // コピーのつもりが「画素値コピー」「自動コントラスト」になる
            return null;
        }

        foreach (AppCommand command in commands)
        {
            if (!command.HasGesture || command.Key != key || command.Modifiers != modifiers)
            {
                continue;
            }

            if (YieldsToFocusedControl(focus, key, modifiers))
            {
                return null;
            }

            return command.IsEnabled() ? command : null;
        }

        return null;
    }

    /// <summary>テキスト編集の標準ショートカット(入力中はコマンドに横取りさせない)。</summary>
    /// <param name="key">キー。</param>
    /// <param name="modifiers">修飾キー。</param>
    /// <returns>テキスト編集の標準ショートカットなら true。</returns>
    internal static bool IsTextEditingGesture(Key key, ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.Control)
        {
            return key is Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.Insert;
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            return key is Key.Z; // やり直し(Redo)
        }

        return modifiers == ModifierKeys.Shift && key is Key.Insert or Key.Delete;
    }

    /// <summary>修飾キー(Ctrl/Alt)なしのショートカットのキーを、フォーカス中のコントロールへ譲るか。</summary>
    private static bool YieldsToFocusedControl(ShortcutFocus focus, Key key, ModifierKeys modifiers)
    {
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return false;
        }

        return focus switch
        {
            // 入力中は修飾キーなし/Shiftのみのショートカットを無効にする
            ShortcutFocus.TextEntry => true,

            // 編集できない ComboBox は文字検索・Home/End・矢印・PageUp/PageDown などで項目を選ぶので、
            // それらのキーは譲る。ファンクションキー(F1・F5・F11 など)は使わないのでコマンドを実行する
            // (以前は ComboBox も入力中とみなし、表示モードを選んだ直後は F1・F11 などが黙って効かなかった)
            ShortcutFocus.Selector => !IsFunctionKey(key),

            // メニューのアクセスキー(「処理(_P)」の P など)・項目の移動・決定はメニューへ渡す
            // (以前は Alt を離してから P を押すとラインプロファイルモードの切替に取られ、開いたメニューの中でも
            // Home/End/Space をフレーム送り・再生に取られた)
            ShortcutFocus.Menu => true,
            _ => false,
        };
    }

    private static bool IsFunctionKey(Key key) => key is >= Key.F1 and <= Key.F24;
}
