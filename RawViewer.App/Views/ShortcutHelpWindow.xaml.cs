using System.Windows;
using System.Windows.Input;
using RawViewer.App.Services;

namespace RawViewer.App.Views;

/// <summary>
/// ショートカット一覧。コマンド定義から自動生成するため割り当てとずれない。
/// </summary>
public partial class ShortcutHelpWindow : Window
{
    /// <summary>ウィンドウを生成する。</summary>
    /// <param name="commands">対象コマンド。</param>
    internal ShortcutHelpWindow(IReadOnlyList<AppCommand> commands)
    {
        InitializeComponent();

        List<ShortcutGroup> groups = commands
            .Where(c => c.HasGesture)
            .GroupBy(c => c.Category)
            .Select(g => new ShortcutGroup(
                g.Key,
                g.Select(c => new ShortcutEntry(c.GestureText, c.Title)).ToList()))
            .ToList();

        // ビューポート側(コマンド表に載らない直接操作)も同じ画面で案内する
        groups.Add(new ShortcutGroup("ビューポート (画像に focus があるとき)", new List<ShortcutEntry>
        {
            new("↑ ↓ ← →", "パン(1/8画面)"),
            new("Shift + ↑↓←→", "パン(1画面)"),
            new("Ctrl + ↑↓←→", "画素カーソルを1画素移動(値をステータスバーに表示)"),
            new("Ctrl+Shift + ↑↓←→", "画素カーソルを10画素移動"),
            new("Ctrl+Alt + ↑↓←→", "画素カーソルをBayer同色で2画素移動"),
            new("Esc", "画素カーソルを消す"),
            new("ホイール", "ズーム(カーソル位置基準)"),
            new("ダブルクリック", "全体表示"),
        }));

        GroupList.ItemsSource = groups;
    }

    /// <summary>一覧の分類。</summary>
    /// <param name="Category">分類名。</param>
    /// <param name="Entries">項目。</param>
    public sealed record ShortcutGroup(string Category, IReadOnlyList<ShortcutEntry> Entries);

    /// <summary>一覧の1項目。</summary>
    /// <param name="Gesture">ショートカット表記。</param>
    /// <param name="Title">操作名。</param>
    public sealed record ShortcutEntry(string Gesture, string Title);

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F1)
        {
            e.Handled = true;
            Close();
        }
    }
}
