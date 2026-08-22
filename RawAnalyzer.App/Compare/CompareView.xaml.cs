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
    private CompareSyncMode _syncMode = CompareSyncMode.FieldOfView;
    private bool _syncing;

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
        view.ViewChanged += OnPaneViewChanged;
        view.CursorMoved += OnPaneCursorMoved;
        view.CursorLeft += OnPaneCursorLeft;
        _panes.Add(view);
        Relayout();
        await view.AttachAsync(pane);
        SetActive(view);

        // 既存ペインがあれば、その表示範囲に合わせて開始する
        ComparePaneView? reference = _panes.FirstOrDefault(
            p => !ReferenceEquals(p, view) && p.Pane is not null);
        if (reference is not null)
        {
            SyncFrom(reference);
        }
    }

    // ---- ペイン間同期 ----

    private void OnSyncModeChanged(object sender, SelectionChangedEventArgs e)
    {
        _syncMode = SyncCombo.SelectedIndex switch
        {
            1 => CompareSyncMode.PixelZoom,
            2 => CompareSyncMode.Off,
            _ => CompareSyncMode.FieldOfView,
        };

        // モードを入れたら、アクティブ(なければ先頭)ペイン基準で即座に揃える
        if (_syncMode != CompareSyncMode.Off)
        {
            ComparePaneView? source = _active ?? _panes.FirstOrDefault();
            if (source?.Pane is not null)
            {
                SyncFrom(source);
            }
        }
    }

    private void OnPaneViewChanged(ComparePaneView source)
    {
        if (_syncMode != CompareSyncMode.Off)
        {
            SyncFrom(source);
        }
    }

    /// <summary>指定ペインのビュー状態を、他の全ペインへ写像して適用する。</summary>
    private void SyncFrom(ComparePaneView source)
    {
        if (_syncing || source.Pane is null)
        {
            return;
        }

        // ApplyViewはViewChangedを発火しない設計だが、将来の変更に備えて再入も遮断する
        _syncing = true;
        try
        {
            PaneViewState sourceState = StateOf(source);
            if (sourceState.ViewWidth < 1 || sourceState.ViewHeight < 1)
            {
                return;
            }

            foreach (ComparePaneView pane in _panes)
            {
                if (ReferenceEquals(pane, source) || pane.Pane is null)
                {
                    continue;
                }

                PaneViewState targetState = StateOf(pane);
                if (targetState.ViewWidth < 1 || targetState.ViewHeight < 1)
                {
                    continue;
                }

                ViewTransform mapped = CompareSync.MapView(_syncMode, sourceState, targetState);
                pane.ApplyView(mapped.Zoom, mapped.OriginX, mapped.OriginY);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private static PaneViewState StateOf(ComparePaneView pane)
    {
        Controls.ImageViewport viewport = pane.ViewportControl;
        return new PaneViewState(
            viewport.Zoom, viewport.OriginX, viewport.OriginY,
            viewport.ActualWidth, viewport.ActualHeight,
            pane.Pane!.Image.Width, pane.Pane.Image.Height);
    }

    private void OnPaneCursorMoved(ComparePaneView source, int x, int y)
    {
        if (_syncMode == CompareSyncMode.Off || source.Pane is null)
        {
            return;
        }

        (int Width, int Height) sourceSize = (source.Pane.Image.Width, source.Pane.Image.Height);
        foreach (ComparePaneView pane in _panes)
        {
            if (ReferenceEquals(pane, source) || pane.Pane is null)
            {
                continue;
            }

            (double gx, double gy) = CompareSync.MapCursor(
                x, y, sourceSize, (pane.Pane.Image.Width, pane.Pane.Image.Height));
            pane.ShowGhostCursor(gx, gy);
        }
    }

    private void OnPaneCursorLeft(ComparePaneView source)
    {
        foreach (ComparePaneView pane in _panes)
        {
            if (!ReferenceEquals(pane, source))
            {
                pane.HideGhostCursor();
            }
        }
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
