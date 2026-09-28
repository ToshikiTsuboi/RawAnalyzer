using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RawAnalyzer.App.Mvvm;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.App.Views;

/// <summary>
/// すべての操作を検索して実行するコマンドパレット(VSCode風)。
/// マウスを使わずに全機能へ到達できるようにするための入口。
/// </summary>
/// <remarks>
/// 実行できないコマンド(<see cref="AppCommand.CanExecute"/> が false)も一覧に出すが、薄く表示し、
/// 選ぶと理由を下に示す。Enter でも実行せず、パレットを開いたまま理由を注意の色で示す
/// (以前は見た目が同じで、Enter を押しても何も起きなかった)。
/// </remarks>
public partial class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<AppCommand> _commands;
    private readonly Brush _hintBrush;

    /// <summary>パレットを生成する。</summary>
    /// <param name="commands">対象コマンド。</param>
    internal CommandPaletteWindow(IReadOnlyList<AppCommand> commands)
    {
        InitializeComponent();
        _commands = commands;
        _hintBrush = HintText.Foreground;
        ApplyFilter("");
        Loaded += (_, _) => FilterBox.Focus();

        // パレットを開いたまま本体を操作できる(画像を開くなど)。戻ったときに実行できるかを表示し直す
        Activated += (_, _) => RefreshAvailability();
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
        // 実行可能なものを先に並べる。実行できないものは薄く表示し、選ぶと理由を下に示す
        List<PaletteRow> matched = _commands
            .Where(c => Matches(query, c.SearchText))
            .Select(c => new PaletteRow(c))
            .OrderByDescending(r => r.IsAvailable)
            .ToList();

        CommandList.ItemsSource = matched;
        if (matched.Count > 0)
        {
            CommandList.SelectedIndex = 0;
        }

        UpdateHint();
    }

    /// <summary>一覧の各コマンドを今実行できるか確かめ直す(並び順は変えない)。</summary>
    private void RefreshAvailability()
    {
        foreach (PaletteRow row in CommandList.Items.OfType<PaletteRow>())
        {
            row.Refresh();
        }

        UpdateHint();
    }

    /// <summary>下の案内を更新する。選択中のコマンドを実行できなければ、その理由を示す。</summary>
    /// <param name="rejected">Enter で実行しようとして断ったか(理由を注意の色で示す)。</param>
    private void UpdateHint(bool rejected = false)
    {
        HintText.Foreground = _hintBrush;
        int count = CommandList.Items.Count;
        if (count == 0)
        {
            HintText.Text = "一致するコマンドがありません";
        }
        else if (CommandList.SelectedItem is PaletteRow { DisabledReason: { } reason })
        {
            HintText.Text = reason;
            if (rejected)
            {
                HintText.Foreground = (Brush)FindResource("RejectBrush");
            }
        }
        else
        {
            HintText.Text = $"{count} 件  ↑↓ で選択 / Enter で実行 / Esc で閉じる";
        }
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter(FilterBox.Text.Trim());
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateHint();
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
        if (CommandList.SelectedItem is not PaletteRow row)
        {
            return;
        }

        // 一覧を作った後に本体の状態が変わることがあるので、Enter の時点で確かめ直す
        row.Refresh();
        if (!row.IsAvailable)
        {
            // 実行できないコマンドは閉じずに、理由を注意の色で示す(以前は何も起きなかった)。
            // ショートカットキーからは従来どおり黙って無視する(AppCommand.DisabledReason を参照)
            UpdateHint(rejected: true);
            return;
        }

        // 実行はパレットを閉じてから(モーダルを開くコマンドと二重表示にしない)
        SelectedCommand = row.Command;
        Close();
    }

    /// <summary>一覧の1行。コマンドと、最後に確かめた時点で実行できるか。</summary>
    internal sealed class PaletteRow : ObservableObject
    {
        private string? _disabledReason;

        /// <summary>行を作り、今実行できるかを確かめる。</summary>
        /// <param name="command">コマンド。</param>
        public PaletteRow(AppCommand command)
        {
            Command = command;
            _disabledReason = command.GetDisabledReason();
        }

        /// <summary>コマンド。</summary>
        public AppCommand Command { get; }

        /// <summary>実行できない理由(実行できるときはnull)。行のツールチップにも使う。</summary>
        public string? DisabledReason
        {
            get => _disabledReason;
            private set
            {
                if (SetProperty(ref _disabledReason, value))
                {
                    OnPropertyChanged(nameof(IsAvailable));
                }
            }
        }

        /// <summary>実行できるか(できない行は薄く表示する)。</summary>
        public bool IsAvailable => _disabledReason is null;

        /// <summary>今の状態で実行できるかを確かめ直す。</summary>
        public void Refresh()
        {
            DisabledReason = Command.GetDisabledReason();
        }
    }
}
