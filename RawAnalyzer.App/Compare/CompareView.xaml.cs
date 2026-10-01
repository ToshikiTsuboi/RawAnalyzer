using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

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

    /// <summary>4枚表示中で追加できない理由(追加ボタンのツールチップと、追加を断るときの案内)。</summary>
    internal const string FullReason = "4枚表示中です。追加するにはいずれかの画像を閉じてください。";

    /// <summary>読み込み中・終了処理中で追加できない理由(追加ボタンのツールチップと、追加を断るときの案内)。</summary>
    internal const string BusyReason = "処理が完了するまでお待ちください。";

    /// <summary>ホストの全画面状態と双方向連携する依存関係プロパティ。</summary>
    public static readonly DependencyProperty IsFullscreenProperty = DependencyProperty.Register(
        nameof(IsFullscreen), typeof(bool), typeof(CompareView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>全画面表示中か。F11やホスト側の変更もボタンに反映する。</summary>
    public bool IsFullscreen
    {
        get => (bool)GetValue(IsFullscreenProperty);
        set => SetValue(IsFullscreenProperty, value);
    }

    private readonly List<ComparePaneView> _panes = new();
    private ComparePaneView? _active;
    private bool _loading;
    private bool _closing;
    private CompareSyncMode _syncMode = CompareSyncMode.FieldOfView;
    private bool _syncing;

    // 等倍同期で、未操作のまま全体表示に追従している基準(FitBaseを参照)と、
    // その再フィット後の揃え直しをレイアウト確定待ちで予約済みか
    private ComparePaneView? _fitBase;
    private bool _fitBaseResyncPending;

    // 読み込み中に比較モードを抜けた場合の判定。CloseAllAsyncで進めることで、
    // 完了した資源を「もう要らないもの」として破棄できる
    private int _generation;
    private CancellationTokenSource? _lifetime;

    /// <summary>ビューを生成する。</summary>
    public CompareView()
    {
        InitializeComponent();
        Relayout();
    }

    /// <summary>「比較モードを終了」が押されたときに発火する。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// ペインを追加できなかった(上限・読み込み中)ときに、理由と追加しなかったファイルを知らせる(ホストが利用者へ示す)。
    /// </summary>
    internal event Action<string>? AddRefused;

    /// <summary>ファイル選択ダイアログ込みでペイン資源を用意する(キャンセルはnull)。</summary>
    internal Func<CancellationToken, Task<ComparePane?>>? PanePicker { get; set; }

    /// <summary>パス指定でペイン資源を用意する(ドロップ用。失敗はnull)。</summary>
    internal Func<string, CancellationToken, Task<ComparePane?>>? PaneLoader { get; set; }

    /// <summary>読み込み中の処理を比較モード終了で打ち切るためのトークン源。</summary>
    private CancellationTokenSource Lifetime => _lifetime ??= new CancellationTokenSource();

    /// <summary>現在のペイン数。</summary>
    public int PaneCount => _panes.Count;

    /// <summary>いまペインを追加できない理由(<see cref="FullReason"/> / <see cref="BusyReason"/>)。追加できるなら null。</summary>
    internal string? AddRefusal =>
        _panes.Count >= MaxPanes ? FullReason
        : _loading || _closing ? BusyReason
        : null;

    private bool CanAddPane => AddRefusal is null;

    /// <summary>
    /// 指定パスの画像をペインとして追加する。
    /// </summary>
    /// <param name="path">追加するファイル。</param>
    /// <returns>追加できたらtrue。</returns>
    public async Task<bool> AddPaneFromPathAsync(string path)
    {
        if (PaneLoader is null || !CanAddPane)
        {
            return false;
        }

        _loading = true;
        UpdateAddControls();
        int generation = _generation;
        CancellationToken token = Lifetime.Token;
        try
        {
            ComparePane? pane = await PaneLoader(path, token);
            if (pane is null)
            {
                return false;
            }

            if (generation != _generation)
            {
                pane.Dispose(); // 読み込み中に比較モードを抜けた
                return false;
            }

            AddPane(pane, token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (generation == _generation)
            {
                _loading = false;
                UpdateAddControls();
            }
        }
    }

    /// <summary>全ペインを閉じて資源を破棄する。</summary>
    /// <returns>破棄完了を表すタスク。</returns>
    public async Task CloseAllAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _loading = false;
        UpdateAddControls();
        // 進行中の読み込みを打ち切り、完了しても追加されないようにする
        _generation++;
        CancellationTokenSource? lifetime = _lifetime;
        _lifetime = null;
        if (lifetime is not null)
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }

        try
        {
            foreach (ComparePaneView pane in _panes.ToList())
            {
                await pane.DetachAndDisposeAsync();
            }
        }
        finally
        {
            _panes.Clear();
            _active = null;
            _fitBase = null;
            _closing = false;
            Relayout();
        }
    }

    /// <summary>
    /// 開いている画像数に応じたグリッド列数。
    /// 1〜3枚は横一列、4枚は2×2。追加用の空き枠は数えない。
    /// </summary>
    /// <param name="paneCount">表示する画像数。</param>
    /// <returns>UniformGridの列数。</returns>
    private static int ColumnsFor(int paneCount)
    {
        return paneCount <= 3 ? Math.Max(1, paneCount) : 2;
    }

    /// <summary>
    /// 指定ファイルを順にペインとして追加する(比較領域へのドロップと、比較モード中の「開く」系の操作)。
    /// </summary>
    /// <remarks>
    /// 読み込みは呼び出し元へ戻ってから始める。最初のファイルが raw でフォーマットの記憶がないと、最初の await より前に
    /// 確認ダイアログが同期で開く。Drop の中でモーダルループに入ると、閉じるまでドラッグ元(エクスプローラー)が
    /// 応答しなくなる。上限(4枚)・読み込み中で追加できなくなったら残りは追加せず、理由と追加しなかったファイルを
    /// <see cref="AddRefused"/> で知らせる(以前のドロップは上限を超えた残りを黙って捨てた)。途中で比較を終了したら、
    /// 残りは新しい比較へ追加しない。
    /// </remarks>
    /// <param name="paths">追加するファイル(先頭から順に追加する)。</param>
    /// <returns>追加し終える(または打ち切る)までのタスク。</returns>
    internal async Task AddPanesFromPathsAsync(IReadOnlyList<string> paths)
    {
        int generation = _generation;
        await Dispatcher.Yield(DispatcherPriority.Background);
        for (int i = 0; i < paths.Count; i++)
        {
            if (generation != _generation)
            {
                return; // 途中で比較を終了した
            }

            if (AddRefusal is { } refusal)
            {
                AddRefused?.Invoke(DescribeRefusal(refusal, paths.Skip(i).ToList()));
                return;
            }

            await AddPaneFromPathAsync(paths[i]);
        }
    }

    /// <summary>追加を断るときの案内(理由と、追加しなかったファイル名)。</summary>
    /// <param name="reason">追加できない理由(<see cref="AddRefusal"/>)。</param>
    /// <param name="notAdded">追加しなかったファイル(ファイルを選ぶ前に断ったときは空)。</param>
    /// <returns>利用者に示す文。</returns>
    internal static string DescribeRefusal(string reason, IReadOnlyList<string> notAdded)
    {
        string text = "比較に画像を追加できません。" + reason;
        if (notAdded.Count == 0)
        {
            return text;
        }

        const int MaxNames = 5;
        string names = string.Join("、", notAdded.Take(MaxNames).Select(System.IO.Path.GetFileName));
        if (notAdded.Count > MaxNames)
        {
            names += $" ほか {notAdded.Count - MaxNames} 件";
        }

        return text + "\n追加しなかったファイル: " + names;
    }

    private async void OnAddImageClick(object sender, RoutedEventArgs e)
    {
        await AddPaneFromPickerAsync();
    }

    /// <summary>
    /// ファイル選択ダイアログで選んだ画像をペインとして追加する(「＋ 画像を追加」と、比較モード中の「開く…」)。
    /// </summary>
    /// <remarks>
    /// 上限(4枚)・読み込み中で追加できないときは、ダイアログを出さずに理由を <see cref="AddRefused"/> で知らせる
    /// (追加ボタンはそのとき無効なので、知らせるのはボタン以外から呼んだとき)。
    /// </remarks>
    /// <returns>追加し終える(または取り消す)までのタスク。</returns>
    internal async Task AddPaneFromPickerAsync()
    {
        if (PanePicker is null)
        {
            return;
        }

        if (AddRefusal is { } refusal)
        {
            AddRefused?.Invoke(DescribeRefusal(refusal, Array.Empty<string>()));
            return;
        }

        _loading = true;
        UpdateAddControls();
        int generation = _generation;
        CancellationToken token = Lifetime.Token;
        try
        {
            ComparePane? pane = await PanePicker(token);
            if (pane is null)
            {
                return;
            }

            if (generation != _generation)
            {
                pane.Dispose(); // 読み込み中に比較モードを抜けた
                return;
            }

            AddPane(pane, token);
        }
        catch (OperationCanceledException)
        {
            // 比較モード終了で打ち切られた
        }
        finally
        {
            if (generation == _generation)
            {
                _loading = false;
                UpdateAddControls();
            }
        }
    }

    private void AddPane(ComparePane pane, CancellationToken cancellationToken)
    {
        var view = new ComparePaneView();
        view.CloseRequested += OnPaneCloseRequested;
        view.ActivateRequested += SetActive;
        view.ViewChanged += OnPaneViewChanged;
        view.CursorMoved += OnPaneCursorMoved;
        view.CursorLeft += OnPaneCursorLeft;
        view.DisplayChanged += OnPaneDisplayChanged;
        view.ViewportControl.SizeChanged += (_, _) => OnPaneViewportResized(view);
        _panes.Add(view);
        Relayout();
        view.Attach(pane, cancellationToken);
        SetActive(view);
        RefreshChips();

        // 新規ペインはまだレイアウト前でサイズが0のため、ここでは既存ペインの
        // 表示範囲を写像できない(SyncFromが対象を飛ばして全体表示のまま残る)。
        // 最初のレイアウトが確定してから合わせる
        SyncNewPaneWhenLaidOut(view);
    }

    /// <summary>
    /// 追加したペインの最初のレイアウト確定後に、既存ペインの表示範囲へ合わせる。
    /// </summary>
    /// <remarks>
    /// LayoutUpdatedはレイアウト処理の最後、全要素のSizeChanged(既存ペインの
    /// 表示中心の維持・新規ペインの全体表示)が済んだ後に来るため、
    /// 双方のサイズが確定した状態で写像できる。
    /// </remarks>
    /// <param name="view">追加したペイン。</param>
    private void SyncNewPaneWhenLaidOut(ComparePaneView view)
    {
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (_closing || !_panes.Contains(view) || view.Pane is null)
            {
                view.LayoutUpdated -= handler; // 表示される前に閉じられた(比較モード終了を含む)
                return;
            }

            Controls.ImageViewport viewport = view.ViewportControl;
            if (viewport.ActualWidth < 1 || viewport.ActualHeight < 1)
            {
                return; // まだ表示されていない
            }

            view.LayoutUpdated -= handler;
            SyncNewPane(view);
        };
        view.LayoutUpdated += handler;
    }

    /// <summary>
    /// 同期中なら、新規ペインだけを既存ペインの表示範囲に合わせる(既存ペインは動かさない)。
    /// </summary>
    /// <param name="view">追加したペイン。</param>
    private void SyncNewPane(ComparePaneView view)
    {
        // 同期オフでは各ペイン独立なので、新規ペインは全体表示のまま置く
        if (_syncMode == CompareSyncMode.Off)
        {
            return;
        }

        // 等倍で未操作の基準があればそれに、なければ既存ペインに合わせる。基準が未操作
        // (全体表示に追従中)なら、視野では新規ペインも全体表示のまま追従し、等倍では
        // 基準の倍率に揃う(SyncFrom)。新規ペイン自身が基準なら合わせる相手はいない
        ComparePaneView? reference = FitBase ?? _panes.FirstOrDefault(
            p => !ReferenceEquals(p, view) && p.Pane is not null);
        if (reference is not null && !ReferenceEquals(reference, view))
        {
            SyncFrom(reference, only: view);
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

        // モードを入れたら即座に揃える。基準は等倍で未操作の基準が残っていればそれ
        // (その倍率に揃えただけの他ペインを基準にすると未操作の扱いが失われる)、
        // なければアクティブ(なければ先頭)ペイン。オフへの切替では何も動かさない
        if (_syncMode != CompareSyncMode.Off)
        {
            ComparePaneView? source = FitBase ?? _active ?? _panes.FirstOrDefault();
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
        else if (!source.IsAutoFit)
        {
            // オフ中の操作で未操作ではなくなったので、等倍の基準は引き継がない
            // (次に同期を入れるときはアクティブのペインを基準にする)
            _fitBase = null;
        }
    }

    /// <summary>
    /// 等倍同期の基準のうち、未操作で全体表示に追従しているペイン(なければnull)。
    /// 他ペインはこの倍率に揃えてあり、基準が再フィットしたら揃え直す。
    /// </summary>
    private ComparePaneView? FitBase =>
        _fitBase is { Pane: not null, IsAutoFit: true } fitBase && _panes.Contains(fitBase)
            ? fitBase
            : null;

    /// <summary>
    /// ペインのビューポートのサイズ変化(ペインの増減・ビューのリサイズ)。
    /// 等倍で未操作の基準が全体表示へ再フィットしたら、他ペインの倍率を揃え直す。
    /// </summary>
    /// <param name="view">サイズが変わったペイン。</param>
    private void OnPaneViewportResized(ComparePaneView view)
    {
        if (_syncMode == CompareSyncMode.PixelZoom && ReferenceEquals(view, FitBase))
        {
            ResyncFromFitBaseWhenLaidOut();
        }
    }

    /// <summary>
    /// レイアウト確定後に、等倍の未操作の基準から他ペインへ倍率を写し直す。
    /// </summary>
    /// <remarks>
    /// 基準のSizeChangedの時点では他ペインのSizeChanged(表示中心の維持)が済んで
    /// いないことがあり、先に写すと後からの中心維持で位置がずれる。
    /// 新規ペインの初期合わせと同じくLayoutUpdatedまで待つ。
    /// </remarks>
    private void ResyncFromFitBaseWhenLaidOut()
    {
        if (_fitBaseResyncPending)
        {
            return;
        }

        _fitBaseResyncPending = true;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            LayoutUpdated -= handler;
            _fitBaseResyncPending = false;
            if (!_closing && _syncMode == CompareSyncMode.PixelZoom && FitBase is { } fitBase)
            {
                SyncFrom(fitBase);
            }
        };
        LayoutUpdated += handler;
    }

    /// <summary>
    /// 指定ペインのビュー状態を、他のペインへ写像して適用する。
    /// 基準が未操作で全体表示に追従中なら、視野では他ペインも全体表示の追従へ戻し、
    /// 等倍では倍率を写したうえで基準だけが追従を続ける(<see cref="FitBase"/>)。
    /// </summary>
    /// <param name="source">基準にするペイン。</param>
    /// <param name="only">指定するとこのペインだけに適用する(ペイン追加時の初期合わせ用)。</param>
    private void SyncFrom(ComparePaneView source, ComparePaneView? only = null)
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

            // 基準が全体表示に追従中(未操作)のとき、視野では変換を写さず追従ごと揃える
            // (未操作の間は各ペインが自分の全体表示)。変換を写すと写した側だけ追従が外れ、
            // 次のペイン増減やリサイズで基準だけが全体表示へ戻ってずれる。
            // 等倍は倍率を揃えるのが目的なので変換を写し、基準だけが追従を続けて、
            // 基準が再フィットするたびに写し直す(OnPaneViewportResized)
            bool sourceFits = source.IsAutoFit;
            _fitBase = sourceFits && _syncMode == CompareSyncMode.PixelZoom ? source : null;
            foreach (ComparePaneView pane in _panes)
            {
                if (ReferenceEquals(pane, source) || pane.Pane is null
                    || (only is not null && !ReferenceEquals(pane, only)))
                {
                    continue;
                }

                if (sourceFits && _syncMode == CompareSyncMode.FieldOfView)
                {
                    pane.ResumeAutoFit();
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
            pane.Pane!.Image.Width, pane.Pane.Image.Height,
            viewport.DeviceScale);
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

    // ---- 表示条件(条件チップと調整リンク) ----

    private void OnPaneDisplayChanged(ComparePaneView source)
    {
        // 🔗同士なら操作結果を他ペインへ転写(ApplyDisplayはDisplayChangedを発火しない)
        if (source.IsLinked && source.Pane is not null)
        {
            DisplaySettings settings = source.Pane.Display;
            int bits = source.Pane.Format.BitDepth;
            foreach (ComparePaneView pane in _panes)
            {
                if (ReferenceEquals(pane, source) || pane.Pane is null || !pane.IsLinked)
                {
                    continue;
                }

                pane.ApplyDisplay(
                    DisplayConditions.Transfer(settings, bits, pane.Pane.Format.BitDepth));
            }
        }

        RefreshChips();
    }

    private void OnAlignConditionsClick(object sender, RoutedEventArgs e)
    {
        ComparePaneView? source = _active?.Pane is not null
            ? _active
            : _panes.FirstOrDefault(p => p.Pane is not null);
        if (source?.Pane is null)
        {
            return;
        }

        DisplaySettings settings = source.Pane.Display;
        int bits = source.Pane.Format.BitDepth;
        foreach (ComparePaneView pane in _panes)
        {
            if (ReferenceEquals(pane, source) || pane.Pane is null)
            {
                continue;
            }

            pane.ApplyDisplay(
                DisplayConditions.Transfer(settings, bits, pane.Pane.Format.BitDepth));
        }

        RefreshChips();
    }

    /// <summary>全ペインの条件チップを再計算する(他ペインと不一致の項目を強調)。</summary>
    private void RefreshChips()
    {
        List<ComparePaneView> loaded = _panes.Where(p => p.Pane is not null).ToList();
        foreach (ComparePaneView pane in loaded)
        {
            DisplaySettings settings = pane.Pane!.Display;
            int bits = pane.Pane.Format.BitDepth;
            var chips = new List<(string Text, bool Differs)>();
            foreach (ConditionKey key in DisplayConditions.Keys)
            {
                bool differs = loaded.Any(other =>
                    !ReferenceEquals(other, pane) && !DisplayConditions.AreEqual(
                        key, settings, bits,
                        other.Pane!.Display, other.Pane.Format.BitDepth));
                chips.Add((DisplayConditions.Format(key, settings, bits), differs));
            }

            pane.SetChips(chips);
        }
    }

    private async void OnPaneCloseRequested(ComparePaneView view)
    {
        if (_closing || !_panes.Remove(view))
        {
            return;
        }
        if (ReferenceEquals(_active, view))
        {
            SetActive(_panes.LastOrDefault());
        }

        if (ReferenceEquals(view, _fitBase))
        {
            HandOverFitBase(view);
        }

        Relayout();
        RefreshChips();
        await view.DetachAndDisposeAsync();
    }

    /// <summary>
    /// 等倍で未操作の基準を閉じるとき、アクティブ(なければ先頭)のペインに基準を引き継ぐ。
    /// 引き継いだペインは全体表示への追従を再開し、他ペインはレイアウト確定後に
    /// その倍率へ揃い直す。引き継がないと閉じた基準の倍率のまま全ペインが固まり、
    /// 以後の増減でも全体表示へ戻らない(オフ中は何も動かさず、基準を手放すだけ)。
    /// </summary>
    /// <param name="closing">閉じる基準のペイン(一覧からは除去済み)。</param>
    private void HandOverFitBase(ComparePaneView closing)
    {
        _fitBase = null;
        if (_syncMode != CompareSyncMode.PixelZoom || !closing.IsAutoFit)
        {
            return;
        }

        ComparePaneView? next = _active?.Pane is not null
            ? _active
            : _panes.FirstOrDefault(p => p.Pane is not null);
        if (next is not null)
        {
            _fitBase = next;
            next.ResumeAutoFit();
            ResyncFromFitBaseWhenLaidOut();
        }
    }

    private void SetActive(ComparePaneView? view)
    {
        _active = view;
        foreach (ComparePaneView pane in _panes)
        {
            pane.IsActive = ReferenceEquals(pane, view);
        }
    }

    /// <summary>開いているペインだけをグリッドへ並べ直し、ラベルを振り直す。</summary>
    private void Relayout()
    {
        PaneGrid.Children.Clear();
        for (int i = 0; i < _panes.Count; i++)
        {
            _panes[i].SetLabel(((char)('A' + i)).ToString());
            PaneGrid.Children.Add(_panes[i]);
        }

        PaneGrid.Columns = ColumnsFor(_panes.Count);
        PaneGrid.Rows = _panes.Count <= 3 ? 1 : 2;
        EmptyAddButton.Visibility = _panes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAddControls();
    }

    private void UpdateAddControls()
    {
        PaneCountText.Text = $"{_panes.Count} / {MaxPanes}枚";
        AddImageButton.IsEnabled = CanAddPane;
        EmptyAddButton.IsEnabled = CanAddPane;
        AddImageButton.Content = _loading ? "読込中…" : "＋ 画像を追加";
        AddImageButton.ToolTip = AddRefusal
            ?? "画像を選んで比較に追加。比較領域のどこへドロップしても追加できます (最大4枚)。";
        ToolTipService.SetShowOnDisabled(AddImageButton, true);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && CanAddPane
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

        // 読み込みは Drop から戻ってから始める(AddPanesFromPathsAsync)。ファイル一覧は戻る前に取り出し済み
        await AddPanesFromPathsAsync(files);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
