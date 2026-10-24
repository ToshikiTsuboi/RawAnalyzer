using System.IO;
using System.Windows;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

/// <summary>メインウィンドウの初期化と、各機能で共有する表示画像の状態。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    private RawImage? _currentImage;
    private RawFormat? _currentFormat;
    private string? _currentPath;

    // 表示中のファイル(_currentPath)を読んだときのサイズ(取得できなければ -1)。読み込みと一緒に UI スレッドの外で
    // 取っておき、連番の判定・一括書き出し・raw 参照の照合・サイズ別の記憶の訂正に使う(それらのたびに UI スレッドで
    // FileInfo を撃つと、切断した NAS ではタイムアウトまでウィンドウが固まる)
    private long _currentFileSize = -1;

    // 表示中の raw ファイルを読んだフォーマット(右パネルでの Bayer の変更を含み、ビニングなどの処理は含まない)。
    // F2 の初期値と、開き直したときに記憶を訂正する元のフォーマットに使う。raw 以外では null
    private RawFormat? _openedRawFormat;
    private CancellationTokenSource? _loadCts;

    /// <summary>
    /// 読み込み用CTSを差し替える(旧CTSはキャンセルする)。
    /// </summary>
    /// <remarks>
    /// ここで Dispose しないのは、旧トークンが fire-and-forget のピラミッド生成などに
    /// 渡っており、破棄後に <c>ParallelOptions.CancellationToken</c> が登録を試みると
    /// ObjectDisposedException になるため。WaitHandle を使っていないCTSは
    /// アンマネージ資源を持たず、参照が切れれば通常のGCで回収される。
    /// 実際に破棄が必要な ProgressWindow 側は処理完了後に Dispose している。
    /// </remarks>
    private void ReplaceLoadCts(CancellationTokenSource? next)
    {
        CancellationTokenSource? previous = _loadCts;
        _loadCts = next;
        previous?.Cancel();
    }
    private string? _correctionLabel;

    // 32bit実数などを16bitへ写したときの対応関係。画像情報欄へ添える。処理結果では処理に合わせて引き継ぐ
    private ValueMappingNote? _valueNote;

    // その係数(等倍ならnull)。画像演算・ノイズ測定で参照画像の係数と照合する。処理結果でも引き継ぐ
    // (フィルタ・欠陥補正・演算はコードの値域で行い対応を変えない。寸法の変わるビニングの結果は参照と組み合わせられない)
    private SampleScaling? _valueScaling;

    /// <summary>画像情報欄へ添える、値の対応関係の説明。</summary>
    private string ValueNoteSuffix => _valueNote is null ? "" : $" · {_valueNote.Text}";
    private ColorImage? _colorImage;

    // ウィンドウを閉じた。UI スレッドの外での確認(ファイルサイズ・実在)を待つ間に閉じられたら、ダイアログを出さない
    // (閉じたウィンドウを Owner にできない)
    private bool _closed;

    /// <summary>メインウィンドウを生成する。</summary>
    public MainWindow()
    {
        InitializeComponent();
        _session = _sessionStore.Load();
        ApplyWindowPlacement();

        // パネルの表示状態は、変更通知(隠す前に今の幅を控える CapturePanelWidths)を購読する前に入れ、
        // 保存していた幅とともに最初のレイアウトの前に列へ反映する。以前は Loaded で入れたため、隠す変更の通知で
        // XAML の既定幅(220/240)のままの列の幅を控え、保存していた幅を(表示したままの側の幅も)上書きしていた
        _vm.LeftPanelVisible = _session.LeftPanelVisible;
        _vm.RightPanelVisible = _session.RightPanelVisible;
        UpdatePanelLayout();
        DataContext = _vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Viewport.ViewportStateChanged += OnViewportStateChanged;
        Viewport.CursorPixelChanged += OnCursorPixelChanged;
        Viewport.RoiChanged += OnViewportRoiChanged;
        Viewport.ProfilePointClicked += OnProfilePointClicked;
        Viewport.WhiteBalancePicked += OnWhiteBalancePicked;

        // チャネル分割表示の出入りでは、ROI がなくても射影の対象(画像全体・象限の案内)が変わる
        Viewport.ChannelSplitLayoutChanged += (_, _) =>
        {
            // チャネル分割表示を抜けたら、射影の窓で選んだチャネルを忘れる(戻ったときは未選択から)
            if (!Viewport.IsChannelSplitLayout)
            {
                ForgetProjectionChannel();
            }

            RefreshProjections();
        };
        HorizontalProjectionToggle.ToolTip = ProjectionText.ToolbarToolTip(ProjectionDirection.Horizontal);
        VerticalProjectionToggle.ToolTip = ProjectionText.ToolbarToolTip(ProjectionDirection.Vertical);
        HorizontalProjectionMenu.ToolTip = ProjectionText.Explanation(ProjectionDirection.Horizontal);
        VerticalProjectionMenu.ToolTip = ProjectionText.Explanation(ProjectionDirection.Vertical);

        // ショートカットはコマンド表(MainWindow.Commands.cs)から一括で捌く。
        // Escだけはビューポートの画素カーソル解除を優先するため個別に扱う(バブルの KeyDown で、
        // フォーカス中のコントロールが使わなかったときだけフルスクリーンを解除する)
        PreviewKeyDown += OnWindowPreviewKeyDown;
        FullscreenEscape.Attach(this, () => _vm.IsFullscreen, () => _vm.IsFullscreen = false);
        RebuildRecentMenu();
        InitFolderTree();
        CompareArea.PanePicker = PickComparePaneAsync;
        CompareArea.PaneLoader = LoadComparePaneAsync;
        CompareArea.ExitRequested += async (_, _) => await ExitCompareModeAsync();
        CompareArea.AddRefused += reason =>
            MessageBox.Show(this, reason, "比較モード", MessageBoxButton.OK, MessageBoxImage.Information);
        Loaded += async (_, _) =>
        {
            // 起動時に構築してショートカット重複を早期に検出する
            _ = Commands;

            // 電源断などで後始末されなかった・以前の版が残した、ネットワーク上の大きな raw のローカル一時コピー
            // (数GB)を消す。使用中(実行中の別のインスタンス)のものは残る。UI スレッドで待たない
            _ = Task.Run(() => RawLoader.DeleteUnusedTemporaryCopies(RawLoader.TemporaryCopyFolder));
            _vm.FileFilterText = _session.FileFilter ?? "";

            // 起動引数のパスの実在とフォルダかどうかは UI スレッドの外で確かめている(App.StartupPath)。確かめる間に
            // 利用者が別のファイル・フォルダを開いていたら、後から来た方を優先して起動引数のパスは開かない。
            // 照合したこの UI ターンのうちに開き始める(照合の後にもう一度待つと、その間に開かれたものを追い越す)
            // 確認を待っている利用者の要求(ツリーの選択・最近使ったファイル・ドロップ)も後から来た方として優先する
            int openRequests = _openRequests;
            int startupFolderGeneration = _folderGeneration;
            int deferredRequests = _deferredOpenRequests;
            if (await App.StartupPath is { } startup)
            {
                if (openRequests == _openRequests && startupFolderGeneration == _folderGeneration
                    && deferredRequests == _deferredOpenRequests)
                {
                    OpenStartupPath(startup);
                }

                return;
            }

            if (_vm.Files.Count == 0 && _session.LastFolder is { } folder)
            {
                // 前回のフォルダの存在確認は UI スレッドで行わない。切断された NAS/UNC では SMB のタイムアウト
                // (十数秒〜数十秒)まで返らず、起動直後のウィンドウが固まる(列挙と同じくバックグラウンドで確かめる)
                int folderGeneration = _folderGeneration;
                bool exists = await Task.Run(() => Directory.Exists(folder));

                // 確かめている間に利用者がフォルダ・ファイルを開いていた(確認を待っている要求を含む)ら、前回のフォルダで
                // 一覧を上書きしない
                if (exists && folderGeneration == _folderGeneration && deferredRequests == _deferredOpenRequests
                    && _vm.Files.Count == 0)
                {
                    LoadFolder(folder, selectPath: null);
                }
            }
        };
        Closing += (_, _) => SaveWindowPlacement();
        Closed += async (_, _) =>
        {
            _closed = true;
            ReplaceLoadCts(null);
            CancelAnalysis();
            _profileWindow?.Close();
            _projections?.Shutdown();
            await CompareArea.CloseAllAsync();
            await Viewport.ClearImageAsync();
            _mainBayerPyramid?.Dispose();
            _derivedBayerPyramid?.Dispose();
            _derivedImage?.Dispose();
            _currentImage?.Dispose();
        };
    }

    /// <summary>表示中の画像(HDR派生ビューがあればそちら)。</summary>
    private RawImage? ActiveImage => _derivedImage ?? _currentImage;

    /// <summary>
    /// 表示中の画像のフォーマット。Bayerパターンのその場変更を反映するため、
    /// 派生ビュー以外では_currentFormat(最新)を返す。
    /// </summary>
    private RawFormat? ActiveFormat => _derivedImage?.Format ?? _currentFormat;

    /// <summary>
    /// 行交互HDRの raw を Raw 表示していて、ファイル上の行の偶奇がセンサの Bayer の位相と一致しないか
    /// (<see cref="HdrExposureMix.BayerPhaseMismatch"/>。派生ビューのフォーマットは HDR 方式を持たないので偽)。
    /// </summary>
    private bool BayerPhaseMismatchShown =>
        ActiveFormat is { } format && HdrExposureMix.BayerPhaseMismatch(format);

    private int CurrentShift => 16 - (ActiveFormat?.BitDepth ?? 16);
}
