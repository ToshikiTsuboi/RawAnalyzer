using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// コマンドパレットで実行できないコマンド(CanExecute が false)の見た目と Enter の扱いの検証。
/// </summary>
/// <remarks>
/// 以前は実行できないコマンドも実行できるものと同じ見た目で並び、選んで Enter を押しても何も起きなかった。
/// </remarks>
[Collection("WPF UI")]
public class CommandPaletteWindowTests
{
    [Fact]
    public Task DisabledCommand_LooksDisabled_AndEnterExplainsInsteadOfDoingNothing() => WpfTestHost.Run(() =>
    {
        bool executed = false;
        AppCommand open = Command("open", "開く…", canExecute: null);
        AppCommand save = Command("save", "保存…", canExecute: () => false, execute: () => executed = true);
        var window = new CommandPaletteWindow(new[] { save, open });
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        var list = (ListBox)window.FindName("CommandList");
        var hint = (TextBlock)window.FindName("HintText");
        Layout(window);

        // 実行できるものを先に並べ(従来どおり)、実行できないものはテーマの無効表示と同じ薄さにする
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(1.0, RowRoot(list, 0).Opacity, 3);
        Assert.Equal(0.4, RowRoot(list, 1).Opacity, 3);

        list.SelectedIndex = 1;
        PressEnter(window);

        // 閉じずに(実行もせずに)、実行できないことを知らせる
        Assert.False(closed);
        Assert.False(executed);
        Assert.Null(window.SelectedCommand);
        Assert.Contains("実行できません", hint.Text);

        // 理由を指定していないコマンドは既定の文言
        Assert.Equal(AppCommand.DefaultDisabledReason, hint.Text);
        window.Close();
    });

    [Fact]
    public Task DisabledCommand_ShowsItsReasonWhenSelected_AndEmphasizesItOnEnter() => WpfTestHost.Run(() =>
    {
        AppCommand open = Command("open", "開く…", canExecute: null);
        AppCommand save = Command("save", "保存…", canExecute: () => false, reason: NoImage);
        var window = new CommandPaletteWindow(new[] { open, save });
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        var list = (ListBox)window.FindName("CommandList");
        var hint = (TextBlock)window.FindName("HintText");
        Layout(window);
        Brush normal = hint.Foreground;
        string normalHint = hint.Text;
        Assert.Contains("2 件", normalHint);

        // 選んだだけで理由を示し(行のツールチップにも出す)、実行できる行にはツールチップを出さない
        list.SelectedIndex = 1;
        Assert.Equal(NoImage, hint.Text);
        Assert.Same(normal, hint.Foreground);
        Assert.Equal(NoImage, RowRoot(list, 1).ToolTip);
        Assert.Null(RowRoot(list, 0).ToolTip);

        // Enter では閉じずに、同じ理由を注意の色(ダイアログの注意書きと同じ)で示す
        PressEnter(window);
        Assert.False(closed);
        Assert.Null(window.SelectedCommand);
        Assert.Equal(NoImage, hint.Text);
        Assert.Equal(Color.FromRgb(0xD9, 0x9B, 0x5B), ((SolidColorBrush)hint.Foreground).Color);

        // 実行できる行へ移れば通常の案内へ戻る
        list.SelectedIndex = 0;
        Assert.Equal(normalHint, hint.Text);
        Assert.Same(normal, hint.Foreground);
        window.Close();
    });

    [Fact]
    public Task EnabledCommand_EnterClosesAndHandsItToOwner() => WpfTestHost.Run(() =>
    {
        // 実行できるコマンドは従来どおり、閉じてから本体が実行する(モーダルとの二重表示を避ける)
        bool executed = false;
        AppCommand open = Command("open", "開く…", canExecute: () => true, execute: () => executed = true);
        AppCommand save = Command("save", "保存…", canExecute: () => false, reason: NoImage);
        var window = new CommandPaletteWindow(new[] { save, open });
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        var list = (ListBox)window.FindName("CommandList");
        Layout(window);

        Assert.Same(open, ((CommandPaletteWindow.PaletteRow)list.Items[0]).Command);
        list.SelectedIndex = 0;
        PressEnter(window);

        Assert.True(closed);
        Assert.Same(open, window.SelectedCommand);
        Assert.False(executed);
    });

    [Fact]
    public Task Availability_IsCheckedAgainOnEnter() => WpfTestHost.Run(() =>
    {
        // パレットは開いたまま本体を操作できる。一覧を作った後に状態が変わったら Enter の時点の状態に従う
        bool canSave = false;
        AppCommand save = Command("save", "保存…", canExecute: () => canSave, reason: NoImage);
        var window = new CommandPaletteWindow(new[] { save });
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        var list = (ListBox)window.FindName("CommandList");
        Layout(window);
        Assert.Equal(0.4, RowRoot(list, 0).Opacity, 3);

        canSave = true; // 例: 本体で画像を開いた
        PressEnter(window);
        Assert.True(closed);
        Assert.Same(save, window.SelectedCommand);

        // 逆に、実行できた状態から実行できなくなったら、断って理由を示し、行を薄くする
        var second = new CommandPaletteWindow(new[] { save });
        bool secondClosed = false;
        second.Closed += (_, _) => secondClosed = true;
        var secondList = (ListBox)second.FindName("CommandList");
        var secondHint = (TextBlock)second.FindName("HintText");
        Layout(second);
        Assert.Equal(1.0, RowRoot(secondList, 0).Opacity, 3);

        canSave = false;
        PressEnter(second);
        Assert.False(secondClosed);
        Assert.Null(second.SelectedCommand);
        Assert.Equal(NoImage, secondHint.Text);
        Assert.Equal(0.4, RowRoot(secondList, 0).Opacity, 3);
        second.Close();
    });

    private const string NoImage = "画像を開いていないため実行できません。";

    private static AppCommand Command(
        string id, string title, Func<bool>? canExecute, Action? execute = null, string? reason = null) => new()
    {
        Id = id,
        Category = "ファイル",
        Title = title,
        CanExecute = canExecute,
        DisabledReason = reason is null ? null : () => reason,
        Execute = execute ?? (() => { }),
    };

    /// <summary>ウィンドウを表示せずに中身を配置し、一覧の行を作らせる。</summary>
    private static void Layout(Window window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height));
        content.Arrange(new Rect(0, 0, window.Width, window.Height));
        content.UpdateLayout();
    }

    /// <summary>一覧の行のテンプレートの根(行の見た目を持つ要素)。</summary>
    private static FrameworkElement RowRoot(ListBox list, int index)
    {
        list.UpdateLayout();
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
        return FindDescendant<DockPanel>(container)
            ?? throw new InvalidOperationException($"{index} 行目のテンプレートが見つかりません。");
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>パレットで Enter を押す(パレットはウィンドウの PreviewKeyDown でキーを捌く)。</summary>
    private static void PressEnter(Window window)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new TestInputSource(), Environment.TickCount, Key.Enter)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        window.RaiseEvent(args);
    }

    /// <summary>ウィンドウを表示せずにキー入力イベントを作るための入力元。</summary>
    private sealed class TestInputSource : PresentationSource
    {
        private Visual? _root;

        public override Visual RootVisual
        {
            get => _root!;
            set => _root = value;
        }

        public override bool IsDisposed => false;

        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
