using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.App.Views;

/// <summary>
/// ショートカット一覧。コマンド定義から自動生成するため割り当てとずれない。
/// </summary>
public partial class ShortcutHelpWindow : Window
{
    private IReadOnlyList<ShortcutGroup> _groups = [];

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
        groups.Add(new ShortcutGroup("ビューポート (画像にフォーカスがあるとき)", new List<ShortcutEntry>
        {
            new("↑ ↓ ← →", "パン(1/8画面)"),
            new("Shift + ↑↓←→", "パン(1画面)"),
            new("Ctrl + ↑↓←→", "画素カーソルを1画素移動(値をステータスバーに表示)"),
            new("Ctrl+Shift + ↑↓←→", "画素カーソルを10画素移動"),
            new("Ctrl+Alt + ↑↓←→", "画素カーソルをBayer同色で2画素移動"),
            new("Esc", "開いているメニュー・ダイアログ、画素カーソル、ツールモード、フルスクリーンの順に解除"),
            new("ホイール", "ズーム(カーソル位置基準)"),
            new("ダブルクリック", "全体表示"),
        }));

        _groups = groups;
        ApplyFilter("");
    }

    /// <summary>一覧の分類。</summary>
    /// <param name="Category">分類名。</param>
    /// <param name="Entries">項目。</param>
    public sealed record ShortcutGroup(string Category, IReadOnlyList<ShortcutEntry> Entries);

    /// <summary>一覧の1項目。</summary>
    /// <param name="Gesture">ショートカット表記。</param>
    /// <param name="Title">操作名。</param>
    public sealed record ShortcutEntry(string Gesture, string Title)
    {
        public IReadOnlyList<string> Keys => GestureKeys(Gesture);
    }

    internal static IReadOnlyList<ShortcutGroup> FilterGroups(IEnumerable<ShortcutGroup> groups, string query) =>
        groups.Select(group => new ShortcutGroup(group.Category,
                group.Entries.Where(entry => CommandPaletteWindow.Matches(query,
                    $"{group.Category} {entry.Gesture} {string.Join(" ", entry.Keys)} {entry.Title}")).ToArray()))
            .Where(group => group.Entries.Count > 0).ToArray();

    internal static IReadOnlyList<string> GestureKeys(string gesture)
    {
        var keys = new List<string>();
        while (gesture.IndexOf('+') is > 0 and var split)
        {
            string modifier = gesture[..split].Trim();
            if (modifier is not ("Ctrl" or "Shift" or "Alt" or "Win"))
                break;
            keys.Add(modifier);
            gesture = gesture[(split + 1)..].Trim();
        }

        if (gesture is "↑ ↓ ← →" or "↑↓←→")
            keys.AddRange(gesture.Where(ch => ch != ' ').Select(ch => ch.ToString()));
        else if (gesture.Length > 0)
            keys.Add(gesture);
        return keys;
    }

    internal static int ColumnsForWidth(double width) => width >= 1000 ? 3 : width >= 650 ? 2 : 1;
    internal int ColumnCount => ColumnsForWidth(GroupList.ActualWidth);

    private void OnGroupWidthChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            GroupList.Tag = Math.Floor(e.NewSize.Width / ColumnsForWidth(e.NewSize.Width));
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (GroupList is not null)
            ApplyFilter(FilterBox.Text);
    }

    private void ApplyFilter(string query)
    {
        var groups = FilterGroups(_groups, query);
        GroupList.ItemsSource = groups;
        EmptyText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroll.ScrollToTop();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F1)
        {
            e.Handled = true;
            Close();
        }
    }
}
