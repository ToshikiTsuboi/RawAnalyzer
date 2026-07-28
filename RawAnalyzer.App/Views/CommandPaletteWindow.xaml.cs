using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.App.Views;

/// <summary>
/// すべての操作を検索して実行するコマンドパレット(VSCode風)。
/// マウスを使わずに全機能へ到達できるようにするための入口。
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<AppCommand> _commands;

    /// <summary>パレットを生成する。</summary>
    /// <param name="commands">対象コマンド。</param>
    internal CommandPaletteWindow(IReadOnlyList<AppCommand> commands)
    {
        InitializeComponent();
        _commands = commands;
        ApplyFilter("");
        Loaded += (_, _) => FilterBox.Focus();
    }

    /// <summary>Enterで選ばれたコマンド(閉じただけならnull)。</summary>
    internal AppCommand? SelectedCommand { get; private set; }

    /// <summary>
    /// 空白区切りの語をすべて含むものを順に残す(語順は問わない)。
    /// </summary>
    /// <param name="query">検索文字列。</param>
    /// <param name="target">検索対象。</param>
    /// <returns>一致するならtrue。</returns>
    internal static bool Matches(string query, string target)
    {
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string term in terms)
        {
            if (target.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyFilter(string query)
    {
        // 実行できないコマンドも一覧には出す(なぜ使えないかを探せるように)が、
        // 実行可能なものを先に並べる
        List<AppCommand> matched = _commands
            .Where(c => Matches(query, c.SearchText))
            .OrderByDescending(c => c.IsEnabled())
            .ToList();

        CommandList.ItemsSource = matched;
        if (matched.Count > 0)
        {
            CommandList.SelectedIndex = 0;
        }

        HintText.Text = matched.Count == 0
            ? "一致するコマンドがありません"
            : $"{matched.Count} 件  ↑↓ で選択 / Enter で実行 / Esc で閉じる";
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter(FilterBox.Text.Trim());
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.Enter:
                e.Handled = true;
                Commit();
                break;
            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
            case Key.PageDown:
                e.Handled = true;
                MoveSelection(10);
                break;
            case Key.PageUp:
                e.Handled = true;
                MoveSelection(-10);
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        int count = CommandList.Items.Count;
        if (count == 0)
        {
            return;
        }

        int next = Math.Clamp(CommandList.SelectedIndex + delta, 0, count - 1);
        CommandList.SelectedIndex = next;
        CommandList.ScrollIntoView(CommandList.SelectedItem);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        Commit();
    }

    private void Commit()
    {
        if (CommandList.SelectedItem is AppCommand command && command.IsEnabled())
        {
            // 実行はパレットを閉じてから(モーダルを開くコマンドと二重表示にしない)
            SelectedCommand = command;
            Close();
        }
    }
}
