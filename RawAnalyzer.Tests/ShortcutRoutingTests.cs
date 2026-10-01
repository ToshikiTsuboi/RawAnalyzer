using System.Windows.Controls;
using System.Windows.Input;
using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// ウィンドウ全体のキー入力を、コマンドとフォーカス中のコントロールのどちらへ渡すか(ShortcutRouting)の検証。
/// </summary>
[Collection("WPF UI")]
public class ShortcutRoutingTests
{
    [Fact]
    public Task Classify_TellsTextEntryFromSelector() => WpfTestHost.Run(() =>
    {
        Assert.Equal(ShortcutFocus.TextEntry, ShortcutRouting.Classify(new TextBox()));
        Assert.Equal(ShortcutFocus.TextEntry, ShortcutRouting.Classify(new ComboBox { IsEditable = true }));

        // 編集できない ComboBox(表示モード・Bayer・HDR の調整対象)は文字入力欄ではない。
        // 以前は「入力中」とみなし、F1・F5・F11 まで黙って無視していた
        Assert.Equal(ShortcutFocus.Selector, ShortcutRouting.Classify(new ComboBox()));
        Assert.Equal(ShortcutFocus.Selector, ShortcutRouting.Classify(new ComboBoxItem())); // 開いたドロップダウン

        Assert.Equal(ShortcutFocus.Other, ShortcutRouting.Classify(new Button()));
        Assert.Equal(ShortcutFocus.Other, ShortcutRouting.Classify(null));
    });

    [Theory]
    [InlineData(Key.F1)]
    [InlineData(Key.F5)]
    [InlineData(Key.F11)]
    public Task NonEditableCombo_LetsFunctionKeysRunCommands(Key key) => WpfTestHost.Run(() =>
    {
        // ファンクションキーは ComboBox が使わないので、フォーカスがあってもコマンドを実行する
        // (以前は表示モードをコンボで選んだ直後に F1・F5・F11 が黙って効かなかった)
        AppCommand command = Command("f", key);

        Assert.Same(command, ShortcutRouting.Resolve(
            new[] { command }, key, ModifierKeys.None, ShortcutRouting.Classify(new ComboBox())));
        Assert.Same(command, ShortcutRouting.Resolve(
            new[] { command }, key, ModifierKeys.None, ShortcutRouting.Classify(new ComboBoxItem())));
    });

    [Theory]
    [InlineData(Key.R)]     // 文字検索(Bayer の RGGB など、頭文字が一致する項目を選ぶ)
    [InlineData(Key.D1)]
    [InlineData(Key.Home)]  // 先頭・末尾の項目
    [InlineData(Key.End)]
    [InlineData(Key.Space)] // 開いたドロップダウンの操作
    [InlineData(Key.Next)]
    public Task NonEditableCombo_KeepsKeysTheComboUses(Key key) => WpfTestHost.Run(() =>
    {
        // キーボードで選択中の ComboBox(閉じた状態・開いたドロップダウン)が使うキーは奪わない。
        // マウスで選んだ後はフォーカスをビューポートへ戻すので、ここへは来ない
        AppCommand command = Command("c", key);
        AppCommand ctrl = Command("ctrl", key, ModifierKeys.Control);
        AppCommand[] commands = { command, ctrl };

        foreach (ShortcutFocus focus in new[]
                 { ShortcutRouting.Classify(new ComboBox()), ShortcutRouting.Classify(new ComboBoxItem()) })
        {
            Assert.Null(ShortcutRouting.Resolve(commands, key, ModifierKeys.None, focus));

            // Ctrl 付きは従来どおりコマンド
            Assert.Same(ctrl, ShortcutRouting.Resolve(commands, key, ModifierKeys.Control, focus));
        }
    });

    [Fact]
    public Task MenuMode_LetsAccessKeysAndNavigationReachTheMenu() => WpfTestHost.Run(() =>
    {
        // Alt を押して離すとメニューモードになり、メニューの項目がフォーカスを持つ。続けて P を押せば
        // 「処理(_P)」が開くはずが、以前はラインプロファイルモードの切替(P)に横取りされた。
        // 開いたメニュー・右クリックメニューの中でも、Home/End/Space をフレーム送り・再生に取られていた
        AppCommand profile = Command("profile-mode", Key.P);
        AppCommand first = Command("seq-first", Key.Home);
        AppCommand play = Command("seq-play", Key.Space);
        AppCommand open = Command("open", Key.O, ModifierKeys.Control);
        AppCommand[] commands = { profile, first, play, open };
        var contextMenu = new ContextMenu();
        var contextItem = new MenuItem();
        contextMenu.Items.Add(contextItem);

        foreach (object focused in new object[] { new MenuItem(), new Menu(), contextItem, contextMenu })
        {
            ShortcutFocus focus = ShortcutRouting.Classify(focused);
            Assert.Null(ShortcutRouting.Resolve(commands, Key.P, ModifierKeys.None, focus));
            Assert.Null(ShortcutRouting.Resolve(commands, Key.Home, ModifierKeys.None, focus));
            Assert.Null(ShortcutRouting.Resolve(commands, Key.Space, ModifierKeys.None, focus));

            // Ctrl 付きのショートカットは従来どおり
            Assert.Same(open, ShortcutRouting.Resolve(commands, Key.O, ModifierKeys.Control, focus));
        }
    });

    [Fact]
    public void TextEntry_KeepsTypingAndEditingGestures()
    {
        AppCommand roi = Command("roi-mode", Key.R);
        AppCommand copy = Command("copy-pixel", Key.C, ModifierKeys.Control);
        AppCommand open = Command("open", Key.O, ModifierKeys.Control);
        AppCommand[] commands = { roi, copy, open };

        // 文字の入力・標準の編集操作は入力欄へ。それ以外の Ctrl 付きはコマンド
        Assert.Null(ShortcutRouting.Resolve(commands, Key.R, ModifierKeys.None, ShortcutFocus.TextEntry));
        Assert.Null(ShortcutRouting.Resolve(commands, Key.C, ModifierKeys.Control, ShortcutFocus.TextEntry));
        Assert.Same(open, ShortcutRouting.Resolve(commands, Key.O, ModifierKeys.Control, ShortcutFocus.TextEntry));

        // 入力欄の外では1文字キーもコマンド
        Assert.Same(roi, ShortcutRouting.Resolve(commands, Key.R, ModifierKeys.None, ShortcutFocus.Other));
        Assert.Same(copy, ShortcutRouting.Resolve(commands, Key.C, ModifierKeys.Control, ShortcutFocus.Other));
    }

    [Fact]
    public void Resolve_SkipsDisabledAndUnassignedKeys()
    {
        AppCommand disabled = Command("save", Key.S, ModifierKeys.Control, canExecute: () => false);

        // 実行できないコマンドのキーは処理済みにせず、フォーカス中のコントロールへ渡す
        Assert.Null(ShortcutRouting.Resolve(new[] { disabled }, Key.S, ModifierKeys.Control, ShortcutFocus.Other));
        Assert.Null(ShortcutRouting.Resolve(new[] { disabled }, Key.Q, ModifierKeys.Control, ShortcutFocus.Other));

        // 修飾キーは完全一致で比べる
        AppCommand plain = Command("zoom-actual", Key.D1);
        Assert.Null(ShortcutRouting.Resolve(new[] { plain }, Key.D1, ModifierKeys.Control, ShortcutFocus.Other));
    }

    [Fact]
    public void AlternateGestures_RunTheSameCommand()
    {
        // ズームインは一覧に「+」と出るが、「+」は Shift+;(JIS)・Shift+=(US)で打つので、以前は Shift 付きで
        // 一致せず効かなかった。テンキーの +/- にも割り当てがなかった
        var zoomIn = new AppCommand
        {
            Id = "zoom-in",
            Category = "表示",
            Title = "ズームイン",
            Key = Key.OemPlus,
            AlternateGestures = new ShortcutKey[] { new(Key.OemPlus, ModifierKeys.Shift), new(Key.Add, ModifierKeys.None) },
            Execute = () => { },
        };
        var zoomOut = new AppCommand
        {
            Id = "zoom-out",
            Category = "表示",
            Title = "ズームアウト",
            Key = Key.OemMinus,
            AlternateGestures = new ShortcutKey[] { new(Key.Subtract, ModifierKeys.None) },
            Execute = () => { },
        };
        AppCommand[] commands = { zoomIn, zoomOut };

        Assert.Same(zoomIn, ShortcutRouting.Resolve(commands, Key.OemPlus, ModifierKeys.None, ShortcutFocus.Other));
        Assert.Same(zoomIn, ShortcutRouting.Resolve(commands, Key.OemPlus, ModifierKeys.Shift, ShortcutFocus.Other));
        Assert.Same(zoomIn, ShortcutRouting.Resolve(commands, Key.Add, ModifierKeys.None, ShortcutFocus.Other));
        Assert.Same(zoomOut, ShortcutRouting.Resolve(commands, Key.Subtract, ModifierKeys.None, ShortcutFocus.Other));
        Assert.Null(ShortcutRouting.Resolve(commands, Key.OemPlus, ModifierKeys.Control, ShortcutFocus.Other));

        // 入力欄では「+」の入力を奪わない(別のキーも1文字キーと同じ扱い)
        Assert.Null(ShortcutRouting.Resolve(commands, Key.OemPlus, ModifierKeys.Shift, ShortcutFocus.TextEntry));
        Assert.Null(ShortcutRouting.Resolve(commands, Key.Add, ModifierKeys.None, ShortcutFocus.TextEntry));

        // 表記は主のキーだけ
        Assert.Equal("+", zoomIn.GestureText);
    }

    [Fact]
    public void VerifyNoDuplicateGestures_ChecksAlternateGesturesToo()
    {
        AppCommand zoomIn = Command("zoom-in", Key.OemPlus, alternates: new ShortcutKey(Key.Add, ModifierKeys.None));
        ShortcutRouting.VerifyNoDuplicateGestures(new[] { zoomIn, Command("zoom-out", Key.OemMinus) });

        // 別のキーが他のコマンドの割り当てと重なっても、起動時に気付けるようにする
        var error = Assert.Throws<InvalidOperationException>(() =>
            ShortcutRouting.VerifyNoDuplicateGestures(new[] { zoomIn, Command("other", Key.Add) }));
        Assert.Contains("zoom-in", error.Message);
        Assert.Contains("other", error.Message);
    }

    private static AppCommand Command(
        string id, Key key, ModifierKeys modifiers = ModifierKeys.None, Func<bool>? canExecute = null,
        params ShortcutKey[] alternates) => new()
    {
        Id = id,
        Category = "test",
        Title = id,
        Key = key,
        Modifiers = modifiers,
        AlternateGestures = alternates,
        CanExecute = canExecute,
        Execute = () => { },
    };
}
