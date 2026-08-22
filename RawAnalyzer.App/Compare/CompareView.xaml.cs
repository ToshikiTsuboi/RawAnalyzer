using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace RawAnalyzer.App.Compare;

/// <summary>
/// 比較モードの中央領域。最大4ペインをグリッドに並べ、
/// 追加(ファイル選択・ドロップ)/クローズ/アクティブ切替を管理する。
/// </summary>
/// <remarks>
/// ファイル→<see cref="ComparePane"/> の解決(rawのフォーマット確認ダイアログや
/// 記憶フォーマットの参照)はMainWindow側の責務で、デリゲートで注入される。
/// </remarks>
public partial class CompareView : UserControl
{
    /// <summary>同時に表示できる最大ペイン数。</summary>
    public const int MaxPanes = 4;

    private readonly List<ComparePaneView> _panes = new();
    private Button? _addTile;
    private ComparePaneView? _active;
    private bool _loading;

    /// <summary>ビューを生成する。</summary>
    public CompareView()
    {
        InitializeComponent();
        Relayout();
    }

    /// <summary>「比較モードを終了」が押されたときに発火する。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>ファイル選択ダイアログ込みでペイン資源を用意する(キャンセルはnull)。</summary>
    internal Func<Task<ComparePane?>>? PanePicker { get; set; }

    /// <summary>パス指定でペイン資源を用意する(ドロップ用。失敗はnull)。</summary>
    internal Func<string, Task<ComparePane?>>? PaneLoader { get; set; }

    /// <summary>現在のペイン数。</summary>
    public int PaneCount => _panes.Count;

    /// <summary>
    /// 指定パスの画像をペインとして追加する。
    /// </summary>
    /// <param name="path">追加するファイル。</param>
    /// <returns>追加できたらtrue。</returns>
    public async Task<bool> AddPaneFromPathAsync(string path)
    {
        if (PaneLoader is null || _panes.Count >= MaxPanes || _loading)
        {
            return false;
        }

        _loading = true;
        try
        {
            ComparePane? pane = await PaneLoader(path);
            if (pane is null)
            {
                return false;
            }

            await AddPaneAsync(pane);
            return true;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>全ペインを閉じて資源を破棄する。</summary>
    /// <returns>破棄完了を表すタスク。</returns>
    public async Task CloseAllAsync()
    {
        foreach (ComparePaneView pane in _panes.ToList())
        {
            await pane.DetachAndDisposeAsync();
        }

        _panes.Clear();
        _active = null;
        Relayout();
    }

    /// <summary>
    /// 要素数(ペイン+追加タイル)に応じたグリッド列数。
    /// 3枚までは横一列、4枚(以上)は2×2にする。
    /// </summary>
    /// <param name="elements">並べる要素数。</param>
    /// <returns>UniformGridの列数。</returns>
    internal static int ColumnsFor(int elements)
    {
        return elements <= 3 ? Math.Max(1, elements) : 2;
    }

    private async void OnAddTileClick(object sender, RoutedEventArgs e)
    {
        if (PanePicker is null || _panes.Count >= MaxPanes || _loading)
        {
            return;
        }

        _loading = true;
        try
        {
            ComparePane? pane = await PanePicker();
            if (pane is not null)
            {
                await AddPaneAsync(pane);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task AddPaneAsync(ComparePane pane)
    {
        var view = new ComparePaneView();
        view.CloseRequested += OnPaneCloseRequested;
        view.ActivateRequested += SetActive;
        _panes.Add(view);
        Relayout();
        await view.AttachAsync(pane);
        SetActive(view);
    }

    private async void OnPaneCloseRequested(ComparePaneView view)
    {
        _panes.Remove(view);
        if (ReferenceEquals(_active, view))
        {
            SetActive(_panes.LastOrDefault());
        }

        Relayout();
        await view.DetachAndDisposeAsync();
    }

    private void SetActive(ComparePaneView? view)
    {
        _active = view;
        foreach (ComparePaneView pane in _panes)
        {
            pane.IsActive = ReferenceEquals(pane, view);
        }
    }

    /// <summary>ペインとプレースホルダをグリッドへ並べ直し、ラベルを振り直す。</summary>
    private void Relayout()
    {
        PaneGrid.Children.Clear();
        for (int i = 0; i < _panes.Count; i++)
        {
            _panes[i].SetLabel(((char)('A' + i)).ToString());
            PaneGrid.Children.Add(_panes[i]);
        }

        bool hasRoom = _panes.Count < MaxPanes;
        if (hasRoom)
        {
            _addTile ??= CreateAddTile();
            PaneGrid.Children.Add(_addTile);
        }

        int elements = _panes.Count + (hasRoom ? 1 : 0);
        PaneGrid.Columns = ColumnsFor(elements);
        HintText.Visibility = _panes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button CreateAddTile()
    {
        var button = new Button
        {
            Content = "＋ 画像を追加\n(ここへドロップも可)",
            FontSize = 13,
            Foreground = (Brush)FindResource("TextDim"),
            Background = Brushes.Transparent,
            BorderBrush = (Brush)FindResource("BorderBrushDark"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(8),
        };
        button.Click += OnAddTileClick;
        return button;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _panes.Count < MaxPanes
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true; // MainWindowのドロップ処理(通常オープン)に流さない
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        foreach (string file in files)
        {
            if (_panes.Count >= MaxPanes)
            {
                break;
            }

            await AddPaneFromPathAsync(file);
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
