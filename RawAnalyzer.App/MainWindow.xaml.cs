using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Globalization;
using System.Text;
using System.Windows.Threading;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

/// <summary>
/// メインウィンドウ。ファイル読込・ピラミッド生成・LUT更新・解析のオーケストレーションを行う。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] SupportedExtensions =
        Compare.ComparePane.RawExtensions
            .Concat(ImageFileLoader.SupportedExtensions).ToArray();

    private readonly MainViewModel _vm = new();
    private readonly FormatPresetStore _presetStore = new();

    // サイズ別フォーマット記憶(format-history.json)。raw の読み込みに成功するたびに記録・保存する
    private readonly FormatMemory _formatMemory = new(new FormatHistoryStore());
    private readonly RecentFilesStore _recentFiles = new();
    private int _recentMenuGeneration;
    private int _folderGeneration;
    private readonly SessionStore _sessionStore = new();
    private readonly SessionState _session;
    private ColorMatrix _colorMatrix = ColorMatrix.Identity;
    private bool _updatingMatrixBoxes;
    private bool _updatingFormatPanel;
    private string? _currentFolder;
    private int _lastCursorX;
    private int _lastCursorY;
    private bool _lastCursorInside;

    private RawImage? _currentImage;
    private RawFormat? _currentFormat;
    private string? _currentPath;

    // 表示中の raw ファイルを読んだフォーマット(右パネルでの Bayer の変更を含み、ビニングなどの処理は含まない)。
    // F2 の初期値と、開き直したときに記憶を訂正する元のフォーマットに使う。raw 以外では null
    private RawFormat? _openedRawFormat;

    // 表示中のファイルを同じサイズの記憶から推定して開いたことの通知(比較モードを抜けたら表示し直す)
    private string _mainFormatNotice = "";
    private string _mainFormatNoticeToolTip = "";
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _analysisCts;

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

    private void ReplaceAnalysisCts(CancellationTokenSource? next)
    {
        CancellationTokenSource? previous = _analysisCts;
        _analysisCts = next;
        previous?.Cancel();
    }

    // プロファイル/射影はヒストグラムとは独立に走るので別のトークンで打ち切る
    private CancellationTokenSource? _profileCts;

    private void ReplaceProfileCts(CancellationTokenSource? next)
    {
        CancellationTokenSource? previous = _profileCts;
        _profileCts = next;
        previous?.Cancel();
    }

    /// <summary>進行中の解析をキャンセルし、参照も外す。</summary>
    private void CancelAnalysis()
    {
        ReplaceAnalysisCts(null);
        ReplaceProfileCts(null);
    }
    private HistogramResult? _histogram;
    private IReadOnlyList<ChannelHistogram>? _channelHistograms;
    private string? _correctionLabel;

    // 32bit実数などを16bitへ写したときの対応関係。画像情報欄へ添える
    private string? _valueNote;

    /// <summary>画像情報欄へ添える、値の対応関係の説明。</summary>
    private string ValueNoteSuffix => _valueNote is null ? "" : $" · {_valueNote}";
    private ColorImage? _colorImage;
    private WindowState _preFullscreenState = WindowState.Normal;
    private WindowStyle _preFullscreenStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _preFullscreenResize = ResizeMode.CanResize;
    private LineProfileWindow? _profileWindow;
    private ushort _blackPoint;
    private ushort _whitePoint = 65535;
    private bool _updatingSliders;

    // ビューポートへ渡した現像LUTと、それを作ったパラメータ(表示中の画像のビット深度を含む)。
    // カラー現像で描く前に現在のパラメータと照合し、違えば作り直す(カラー現像表示に入るまで再生成を遅延)
    private readonly DevelopLutCache _developLutCache = new();
    private DispatcherTimer? _developLutTimer;

    // シーケンス再生
    private enum SequenceMode
    {
        None,
        Frames,
        Files,
        TiffPages,
    }

    /// <summary>再生フレームレートの既定値(コンボの初期選択と同じ)。</summary>
    private const double DefaultPlaybackFps = 15;

    // 重い処理(保存/演算/バッチ/測定/検出)の実行中を数える。
    // ShowDialog は Dispatcher の入れ子ポンプなのでモーダル表示中もタイマーや
    // 入力イベントが動き続ける。処理対象の画像が背後で差し替え・破棄されるのを防ぐ。
    private int _busyDepth;

    // 通常の読み込み(OpenPath)と、表示画像を使う操作(EnterBusy)の排他。
    // 読み込みの確定待ちの間は操作を始めず、読み込みは操作の終了を待ってから確定する
    private readonly ImageOperationGate _imageGate = new();

    // OpenPath の世代。await から戻った時点で世代が進んでいたら結果を捨てる
    private int _openGeneration;

    private SequenceMode _sequenceMode;
    private List<string> _sequenceFiles = new();
    private int _sequenceIndex;
    private DispatcherTimer? _playTimer;
    private bool _sequenceBusy;
    private bool _updatingSequenceUi;

    // ファイル連番の送りで送り先を読んでいる間の取り消し(TIFFのページ送りの _tiffPageLoadCts と同じく、
    // 表示中の画像の世代 _loadCts にリンクする)。開く・操作の開始(EnterBusyCore)・ウィンドウを閉じると取り消す
    private CancellationTokenSource? _sequenceLoadCts;

    // ファイル連番の送りで画像ファイル(TIFF等)へ引き継ぐ、右パネルのBayer指定。
    // TIFFのページ送り(TiffStackSource.BayerOverride)と同じく、開いた画像の配列から始めて
    // 右パネルの変更で更新し、カラー画像を挟んでも保持してグレーの画像にだけ付ける(ImageFileBayer)
    private BayerPattern _sequenceBayerOverride;

    // HDR分割/合成の派生ビュー
    private RawImage? _derivedImage;
    private HdrImage? _hdrFloatImage;
    private TilePyramid? _mainPyramid;
    private BayerPyramid? _mainBayerPyramid;
    private BayerPyramid? _derivedBayerPyramid;

    // ピラミッドは生成元フレーム専用。派生ビューからの復帰時に別フレーム産を
    // frame=0として誤登録しないよう、生成元フレームを一緒に記録する
    private int _mainPyramidFrame;
    private int _mainBayerPyramidFrame;

    // 欠陥検出リストはこの画像・フレーム・Bayerパターンでのみ有効。
    // 別画像(HDR派生ビュー・ファイル送り後)や別のパターンへ適用すると無関係な画素を壊すため、
    // 結果の採用時と補正時に一致を検証する(右パネルでパターンを変えたら破棄する)
    private DefectDetectionSource? _defectSource;

    /// <summary>
    /// 欠陥検出の検出元画像への参照を手放す。
    /// </summary>
    /// <remarks>
    /// 表示画像を差し替えたら検出結果は流用できない(補正適用時は
    /// <see cref="DefectDetectionSource.IsCurrent"/> で弾かれる)。参照を残すと破棄済み画像のヒープ側画素配列
    /// (最大約200MB)が回収されないため、差し替えのたびに明示的に切る。
    /// </remarks>
    private void ClearDefectSource()
    {
        _defectSource = null;
    }
    private DisplayParameters[]? _hdrFrameParams;
    private int _hdrSegmentWidth;

    /// <summary>メインウィンドウを生成する。</summary>
    public MainWindow()
    {
        InitializeComponent();
        _session = _sessionStore.Load();
        ApplyWindowPlacement();
        DataContext = _vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Viewport.ViewportStateChanged += OnViewportStateChanged;
        Viewport.CursorPixelChanged += OnCursorPixelChanged;
        Viewport.RoiChanged += OnViewportRoiChanged;
        Viewport.ProfilePointClicked += OnProfilePointClicked;
        Viewport.WhiteBalancePicked += OnWhiteBalancePicked;
        // ショートカットはコマンド表(MainWindow.Commands.cs)から一括で捌く。
        // Escだけはビューポートの画素カーソル解除を優先するため個別に扱う
        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewKeyDown += (_, e) =>
        {
            if (!e.Handled && e.Key == Key.Escape && _vm.IsFullscreen)
            {
                _vm.IsFullscreen = false;
                e.Handled = true;
            }
        };
        RebuildRecentMenu();
        InitFolderTree();
        CompareArea.PanePicker = PickComparePaneAsync;
        CompareArea.PaneLoader = LoadComparePaneAsync;
        CompareArea.ExitRequested += async (_, _) => await ExitCompareModeAsync();
        Loaded += async (_, _) =>
        {
            // 起動時に構築してショートカット重複を早期に検出する
            _ = Commands;
            _vm.LeftPanelVisible = _session.LeftPanelVisible;
            _vm.RightPanelVisible = _session.RightPanelVisible;
            _vm.FileFilterText = _session.FileFilter ?? "";
            UpdatePanelLayout();

            if (App.StartupPath is { } startup)
            {
                await OpenStartupPath(startup);
                return;
            }

            if (_vm.Files.Count == 0 && _session.LastFolder is { } folder
                && Directory.Exists(folder))
            {
                LoadFolder(folder, selectPath: null);
            }
        };
        Closing += (_, _) => SaveWindowPlacement();
        Closed += async (_, _) =>
        {
            ReplaceLoadCts(null);
            CancelAnalysis();
            _profileWindow?.Close();
            await CompareArea.CloseAllAsync();
            await Viewport.ClearImageAsync();
            _mainBayerPyramid?.Dispose();
            _derivedBayerPyramid?.Dispose();
            _derivedImage?.Dispose();
            _currentImage?.Dispose();
        };
    }

    /// <summary>起動引数で渡されたパスを開く(フォルダなら一覧表示のみ)。</summary>
    /// <param name="path">ファイルまたはフォルダのパス。</param>
    private async Task OpenStartupPath(string path)
    {
        if (Directory.Exists(path))
        {
            await LoadFolderAsync(path, selectPath: null);
            return;
        }

        // 連番判定はファイル一覧を見るので、一覧が揃ってから開く
        await LoadFolderAsync(Path.GetDirectoryName(path)!, selectPath: path);
        OpenPath(path);
    }

    /// <summary>表示中の画像(HDR派生ビューがあればそちら)。</summary>
    private RawImage? ActiveImage => _derivedImage ?? _currentImage;

    /// <summary>
    /// 表示中の画像のフォーマット。Bayerパターンのその場変更を反映するため、
    /// 派生ビュー以外では_currentFormat(最新)を返す。
    /// </summary>
    private RawFormat? ActiveFormat => _derivedImage?.Format ?? _currentFormat;

    private int CurrentShift => 16 - (ActiveFormat?.BitDepth ?? 16);

    // ---- セッション記憶 ----

    private void ApplyWindowPlacement()
    {
        if (_session.WindowWidth is { } width && _session.WindowHeight is { } height
            && _session.WindowLeft is { } left && _session.WindowTop is { } top
            && width >= 400 && height >= 300)
        {
            // 画面外への復元を防ぐ
            double maxLeft = SystemParameters.VirtualScreenLeft
                + SystemParameters.VirtualScreenWidth - 200;
            double maxTop = SystemParameters.VirtualScreenTop
                + SystemParameters.VirtualScreenHeight - 100;
            Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft, maxLeft);
            Top = Math.Clamp(top, SystemParameters.VirtualScreenTop, maxTop);
            Width = Math.Min(width, SystemParameters.VirtualScreenWidth);
            Height = Math.Min(height, SystemParameters.VirtualScreenHeight);
        }

        if (_session.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        if (_session.LeftPanelWidth is { } leftWidth && leftWidth >= 120)
        {
            _leftPanelWidth = leftWidth;
        }

        if (_session.RightPanelWidth is { } rightWidth && rightWidth >= 160)
        {
            _rightPanelWidth = rightWidth;
        }
    }

    private void SaveWindowPlacement()
    {
        CapturePanelWidths();
        _session.LeftPanelWidth = _leftPanelWidth;
        _session.RightPanelWidth = _rightPanelWidth;
        _session.LeftPanelVisible = _vm.LeftPanelVisible;
        _session.RightPanelVisible = _vm.RightPanelVisible;
        _session.FileFilter = _vm.FileFilterText;
        _session.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _session.WindowLeft = Left;
            _session.WindowTop = Top;
            _session.WindowWidth = Width;
            _session.WindowHeight = Height;
        }
        else
        {
            _session.WindowLeft = RestoreBounds.Left;
            _session.WindowTop = RestoreBounds.Top;
            _session.WindowWidth = RestoreBounds.Width;
            _session.WindowHeight = RestoreBounds.Height;
        }

        _sessionStore.Save(_session);
    }

    private void RememberFileFormat(string path, RawFormat format)
    {
        SessionStore.TouchFileFormat(_session, SessionStore.NormalizeKey(path), format);
        _sessionStore.Save(_session);
    }

    private RawFormat? TryGetRememberedFormat(string path, long fileSize)
    {
        if (!_session.FileFormats.TryGetValue(SessionStore.NormalizeKey(path), out RawFormat? format))
        {
            return null;
        }

        long required = format.HeaderOffset + format.FrameSizeInBytes * format.FrameCount;
        return required <= fileSize ? format : null;
    }

    /// <summary>
    /// 表示中のファイルを同じサイズの記憶から推定して開いたことをステータスバーに示す(空文字で消す)。
    /// </summary>
    /// <remarks>
    /// 画像情報(ImageInfoText)とは別の欄に出すので、読み込み後の画像情報や保存完了などの一時メッセージで
    /// 消えず、別のファイルを表示するまで見える。
    /// </remarks>
    /// <param name="text">通知の文。</param>
    /// <param name="toolTip">通知のツールチップ(推定したフォーマットと直し方)。</param>
    private void SetMainFormatNotice(string text, string toolTip = "")
    {
        _mainFormatNotice = text;
        _mainFormatNoticeToolTip = toolTip;
        _vm.FormatNoticeText = text;
        _vm.FormatNoticeToolTip = toolTip;
    }

    // ---- ファイル読込 ----

    private const string OpenImageFilter =
        "対応画像 (*.raw;*.bin;*.tif;*.tiff;*.dng;*.jpg;*.jpeg;*.png;*.bmp)"
        + "|*.raw;*.bin;*.tif;*.tiff;*.dng;*.jpg;*.jpeg;*.png;*.bmp"
        + "|Raw (*.raw;*.bin)|*.raw;*.bin"
        + "|画像 (*.tif;*.tiff;*.dng;*.jpg;*.jpeg;*.png;*.bmp)|*.tif;*.tiff;*.dng;*.jpg;*.jpeg;*.png;*.bmp"
        + "|すべてのファイル (*.*)|*.*";

    private async void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = OpenImageFilter };
        if (dialog.ShowDialog(this) == true)
        {
            // 連番判定はファイル一覧を見るので、一覧が揃ってから開く
            await LoadFolderAsync(Path.GetDirectoryName(dialog.FileName)!, dialog.FileName);
            OpenPath(dialog.FileName);
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true)
        {
            LoadFolder(dialog.FolderName, selectPath: null);
        }
    }

    /// <summary>
    /// フォルダ一覧を読み込む(待つ必要がない場所向けの投げっぱなし版)。
    /// </summary>
    /// <param name="folder">対象フォルダ。</param>
    /// <param name="selectPath">読み込み後に選択するファイル。</param>
    private void LoadFolder(string folder, string? selectPath)
    {
        _ = LoadFolderAsync(folder, selectPath);
    }

    /// <summary>
    /// フォルダ一覧を読み込む。列挙はバックグラウンドで行う。
    /// </summary>
    /// <remarks>
    /// DirectoryInfo.EnumerateFiles はネットワーク共有だと数秒かかることがあり、
    /// UIスレッドで回すとその間ウィンドウが固まる。
    /// </remarks>
    /// <param name="folder">対象フォルダ。</param>
    /// <param name="selectPath">読み込み後に選択するファイル。</param>
    /// <returns>読み込み完了を表すタスク。</returns>
    private async Task LoadFolderAsync(string folder, string? selectPath)
    {
        folder = Path.GetFullPath(folder);

        // 遅いフォルダの列挙中に別フォルダを開くと、後から終わった古い結果が
        // 画面とセッションを巻き戻してしまう。ファイル読み込みと同じ世代番号で弾く
        int generation = ++_folderGeneration;
        List<FileEntry> entries;
        try
        {
            entries = await Task.Run(() => EnumerateFolder(folder));
        }
        catch (Exception ex)
        {
            if (generation == _folderGeneration)
            {
                MessageBox.Show(this, $"フォルダを読み込めません: {ex.Message}", "RawAnalyzer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return;
        }

        if (generation != _folderGeneration)
        {
            return; // 列挙中に別のフォルダが開かれた
        }

        _currentFolder = folder;
        _vm.FolderPath = $"📂 {folder}";
        _vm.ReplaceFiles(entries);

        ExpandTreeToFolder(folder);
        _session.LastFolder = folder;
        _session.FileFilter = _vm.FileFilterText;
        _sessionStore.Save(_session);

        if (selectPath is not null)
        {
            // 絞り込みで隠れているファイルは選択しない(リストに無い項目は選択できない)
            _vm.SelectedFile = _vm.FilteredFiles.FirstOrDefault(
                f => string.Equals(f.FullPath, selectPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// フォルダ内の対応ファイルをサイズ付きで列挙する。
    /// </summary>
    /// <remarks>
    /// DirectoryInfo.EnumerateFiles は列挙時にサイズを持ってくるので、
    /// あとから1ファイルずつ FileInfo.Length を撃つ必要がない。
    /// ネットワーク共有の5000ファイルで数秒〜十数秒UIが止まっていた原因。
    /// </remarks>
    private static List<FileEntry> EnumerateFolder(string folder)
    {
        var entries = new List<FileEntry>();
        foreach (FileInfo info in new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => SupportedExtensions.Contains(
                f.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.FullName, NaturalOrderComparer.Instance))
        {
            long length;
            try
            {
                length = info.Length;
            }
            catch (Exception)
            {
                length = -1;
            }

            entries.Add(new FileEntry(info.Name, info.FullName, IsDirectory: false, length));
        }

        return entries;
    }

    private void OnFileListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedFile is not { } entry)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            LoadFolder(entry.FullPath, selectPath: null);
        }
        else
        {
            OpenPath(entry.FullPath);
        }
    }

    private void OnChangeFormatClick(object sender, RoutedEventArgs e)
    {
        if (_currentPath is not null && IsRawFile(_currentPath))
        {
            ChangeCurrentRawFormat(_currentPath);
            return;
        }

        // 画像ファイル(TIFF 等)はフォーマットをファイル自身が持ち、開き直せない。メニュー・右パネルの
        // ボタンは無効にしてツールチップで理由を示すが、キー(F2)・コマンドパレットからは実行されるので、
        // 黙って何もしないのではなく同じ理由を知らせる
        if (_currentImage is not null)
        {
            MessageBox.Show(this, FormatChangeAvailability.ExplainUnavailable(_colorImage is not null),
                "フォーマット変更", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// 表示中の raw をフォーマットを指定し直して開き直す(F2)。開き直せたら、元のフォーマットが同じサイズの
    /// 記憶にあれば新しいフォーマットで置き換える(誤って自動で開いたものを直すと、次からは直した方で開く)。
    /// </summary>
    /// <param name="path">表示中の raw ファイル。</param>
    private void ChangeCurrentRawFormat(string path)
    {
        // ビニング後の縮小寸法を元ファイルの寸法として提案しない。
        RawFormat? initial = _correctionLabel is null ? _currentFormat
            : _openedRawFormat ?? TryGetRememberedFormat(path, SafeFileSize(path));
        OpenPathChoosingFormat(path, initial, correctFrom: initial);
    }

    /// <summary>フォーマット指定が必要な生バイナリ(.raw/.bin)かどうか。</summary>
    private static bool IsRawFile(string path)
    {
        return Compare.ComparePane.IsRawFile(path);
    }

    /// <summary>
    /// ファイルを開く。raw は同じパスの記憶 → 同じサイズの記憶(自動適用がちょうど1つ)の順に使い、
    /// なければダイアログで指定する(<see cref="RawOpenPlanner"/>)。
    /// </summary>
    /// <param name="path">開くファイル。</param>
    private void OpenPath(string path)
    {
        OpenPathCore(path, chooseFormat: false, initialFormat: null, correctFrom: null);
    }

    /// <summary>raw をフォーマットを指定し直して開く(記憶を使わず必ずダイアログを出す)。</summary>
    /// <param name="path">開く raw ファイル。</param>
    /// <param name="initialFormat">ダイアログの初期値(null なら候補一覧の先頭)。</param>
    /// <param name="correctFrom">開き直す前のフォーマット(同じサイズの記憶にあれば置き換える)。</param>
    private void OpenPathChoosingFormat(string path, RawFormat? initialFormat, RawFormat? correctFrom)
    {
        OpenPathCore(path, chooseFormat: true, initialFormat, correctFrom);
    }

    private async void OpenPathCore(
        string path, bool chooseFormat, RawFormat? initialFormat, RawFormat? correctFrom)
    {
        // 読み込み中に再生タイマーや保留中の連番送りが画像を差し替えないようにする
        using BusyScope busy = EnterLoadBusy();

        // 開始から確定(または破棄)までは、保存・演算など表示画像を使う操作を始めさせない。
        // 確定がそれらのダイアログ・進捗表示(入れ子ポンプ)の中で走ると、操作のために
        // 作った画面が別の画像を処理し、処理中の画像も破棄されてしまう
        using IDisposable pendingLoad = _imageGate.BeginLoad();
        int generation = ++_openGeneration;

        RawFormat? format = null;
        RawFormatOrigin? origin = null;
        bool? autoOpenChoice = null;
        if (IsRawFile(path))
        {
            // 同じファイルを開き直すときは記憶したフォーマットでダイアログをスキップし、なければ同じサイズ・
            // 拡張子のファイルの記憶(自動適用がちょうど1つ)で開く。それ以外はダイアログ(候補一覧の先頭が初期値)。
            // フォーマットを指定し直すとき(F2 など)は記憶を使わず、渡された初期値でダイアログを出す
            long size = SafeFileSize(path);
            RawOpenPlan plan = RawOpenPlanner.Plan(
                path, size, chooseFormat ? null : TryGetRememberedFormat(path, size),
                _formatMemory.History, _currentFormat, initialFormat, chooseFormat);
            origin = plan.Origin;
            format = plan.Format;
            if (format is null)
            {
                var dialog = new RawImportDialog(
                    path, _presetStore, plan.DialogInitial, _formatMemory, _currentFormat)
                {
                    Owner = this,
                };
                if (dialog.ShowDialog() != true || dialog.Result is null)
                {
                    return;
                }

                format = dialog.Result;
                autoOpenChoice = dialog.AutoOpenNextTime;
            }
        }

        CancelAnalysis();
        var cts = new CancellationTokenSource();
        ReplaceLoadCts(cts);
        _vm.ImageInfoText = "読込中…";
        _vm.LoadProgress = 0;
        _vm.IsLoading = true;

        // Progress<T>は生成スレッド(UI)へマーシャリングして通知する
        var loadProgress = new Progress<double>(
            p => _vm.LoadProgress = Math.Clamp(p * 100, 0, 100));

        RawImage image;
        ColorImage? color = null;
        int pageCount = 1;
        string? valueNote = null;
        try
        {
            if (IsRawFile(path))
            {
                image = await Task.Run(
                    () => RawLoader.Load(path, format!, cts.Token, loadProgress), cts.Token);
            }
            else
            {
                DecodedImage decoded = await Task.Run(
                    () => ImageFileLoader.Load(path, cts.Token, loadProgress), cts.Token);
                image = decoded.Luminance;
                color = decoded.Color;
                pageCount = decoded.PageCount;
                valueNote = decoded.ValueNote;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _vm.ImageInfoText = "読込失敗";
            MessageBox.Show(this, $"読み込みに失敗しました: {ex.Message}", "RawAnalyzer",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            // 読み込み中に別ファイルが開かれた場合、後続世代の進捗表示を
            // 先行世代のfinallyが消してしまわないよう、現行世代のみ落とす
            if (generation == _openGeneration)
            {
                _vm.IsLoading = false;
            }
        }

        // 読み込みより先に始まっていた操作(保存・演算・HDR分割など)があれば、終わるのを
        // 待ってから差し替える。操作のダイアログ・進捗表示の中で確定すると、操作の
        // 対象画像を背後で差し替え・破棄してしまう(開く要求は捨てずに後から表示する)
        try
        {
            while (_imageGate.IsOperationRunning
                && !cts.IsCancellationRequested && generation == _openGeneration)
            {
                _vm.ImageInfoText = $"処理の完了後に {Path.GetFileName(path)} を表示します…";
                await _imageGate.WhenOperationsIdleAsync().WaitAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 待機中に別ファイルが開かれた・ウィンドウが閉じられた(下で結果を捨てる)
        }

        if (cts.IsCancellationRequested || generation != _openGeneration)
        {
            // 読み込み中に別ファイルを開かれていた場合は結果を捨てる
            image.Dispose();
            return;
        }

        // 旧画像の描画を止めてから破棄し、新画像へ差し替える
        await Viewport.ClearImageAsync();
        _derivedImage?.Dispose();
        _derivedImage = null;
        _vm.IsHdrViewShown = false;
        _hdrFloatImage = null;
        _hdrFrameParams = null;

        // 合成ビューの表示中に開いたら、合成ビューへ入る前の黒点・白点を後で戻さない(表示調整は下で既定へ戻す)
        _mergedViewLevels.Reset();
        _mainPyramid = null;
        _mainBayerPyramid?.Dispose();
        _mainBayerPyramid = null;
        _derivedBayerPyramid?.Dispose();
        _derivedBayerPyramid = null;
        _correctionLabel = null;
        UpdateProcessingBadge();
        _channelHistograms = null;
        _colorImage = color;
        _vm.IsColorImage = color is not null;
        _vm.HdrTargetVisible = false;
        _currentImage?.Dispose();
        _currentImage = image;
        ClearDefectSource();
        _currentFormat = image.Format;
        _currentPath = path;

        // 「フォーマット変更…」は raw でしか使えない。ファイル連番の送りは同じ拡張子のファイルだけを
        // 送る(SequenceScanner)ので、開いたときに決めれば送りの後も変わらない
        _vm.IsRawFile = IsRawFile(path);
        _tiffStack = pageCount > 1
            ? new TiffStackSource(path, pageCount) { BayerOverride = image.Format.Bayer } : null;
        _sequenceBayerOverride = image.Format.Bayer;
        _tiffPageIndex = 0;
        _valueNote = valueNote;
        _histogram = null;
        _vm.HasRoi = false;
        ClearCursorReadout();
        _vm.BlackLevelMax = (1 << image.Format.BitDepth) - 1;
        ResetDisplayParameters();

        Title = $"RawAnalyzer — {Path.GetFileName(path)}{TiffPageNote}";
        Viewport.SetDefectMarkers(null);
        _defectWindow?.DiscardResult();
        UpdateNoiseWindowSource();
        _recentFiles.Add(path);
        RebuildRecentMenu();
        long fileSize = SafeFileSize(path);
        bool guessedFromSize = origin == RawFormatOrigin.SizeMemory;
        if (IsRawFile(path))
        {
            // 同じパスの記憶は従来どおり記録する。ただし同じサイズの記憶から推定して開いたときは記録しない
            // (推定した形式をこのファイルの記憶として固定すると、同じサイズの記憶を後から F2 で直しても
            // このファイルだけ古い形式で開き続ける。一覧を次々に開いて上限の50件を使い切ることもない)
            if (!guessedFromSize)
            {
                RememberFileFormat(path, image.Format);
            }

            // 同じサイズ・拡張子の記憶へ記録する(F2 で開き直したときは元の形式を置き換える)
            _formatMemory.RememberLoaded(path, fileSize, image.Format, autoOpenChoice, correctFrom);
            _openedRawFormat = image.Format;
        }
        else
        {
            _openedRawFormat = null;
        }

        // 読み込み後の画像情報とは別の欄に出し、このファイルを表示している間は見えるようにする
        SetMainFormatNotice(
            guessedFromSize ? RawOpenPlanner.AutoOpenNotice : "",
            guessedFromSize ? RawOpenPlanner.AutoOpenToolTip(path, fileSize, image.Format) : "");

        UpdateFormatPanel(image.Format);
        _vm.ImageInfoText =
            $"{image.Width}×{image.Height} · {image.Format.BitDepth}bit"
            + (color is not null ? " · RGB" : "")
            + (fileSize >= 0 ? $" · {fileSize / (1024.0 * 1024.0):F1} MB" : "")
            + (image.FrameCount > 1 ? $" · {image.FrameCount}fr" : "") + TiffPageNote + ValueNoteSuffix;
        _vm.HasImage = true;

        DisplayModeCombo.SelectedIndex = 0;
        DisplayModeCombo.IsEnabled = color is null;
        Viewport.SetDisplayMode(ViewportDisplayMode.Raw);
        Viewport.SetImage(image, image.Format);
        Viewport.SetColorImage(color);
        Viewport.SetLut(BuildLut());
        DetectSequence();

        RefreshHistogram(roi: null);

        // 開いているラインプロファイル窓は、ヒストグラムと同じく開いた画像で計算し直す(同じ基準点・方向)。
        // 以前は前の画像の断面を出し続けていた(マーカーだけが消える)
        RefreshLineProfile();

        // 差し替えを終えたので、以降の操作は新しい画像を対象に始めてよい
        // (ピラミッド生成は差し替え後の画像を検証してから取り付ける。二重の Dispose は無視される)
        pendingLoad.Dispose();
        await BuildPyramidAsync(image, cts.Token);
    }

    // 進行中の縮小ピラミッド(グレー・Bayer)の生成。同じ画像・フレーム・世代の生成を重ねて始めない
    private readonly PyramidBuildCoalescer _pyramidBuilds = new();
    private readonly PyramidBuildCoalescer _bayerPyramidBuilds = new();

    /// <summary>
    /// 縮小ピラミッドを作り、生成元の画像・フレーム・世代のままなら取り付ける。
    /// </summary>
    /// <remarks>
    /// 同じ画像・フレーム・世代(トークン)の生成が進行中なら新たに始めず、その完了を待つ。
    /// 送り後の作り直し(RefreshAfterSequenceMove)は取り付け済みかどうかしか見ないので、生成中に
    /// 同じフレームの作り直しを重ねて求めると(再生中に「次」を押して停止するなど)、同じ全走査が
    /// 二重に走っていた。フレームの送り・画像の差し替えは世代を進めるので、旧世代の生成には相乗りしない。
    /// </remarks>
    /// <param name="image">生成元の画像。</param>
    /// <param name="ct">表示中の画像・フレームの世代のトークン。</param>
    /// <param name="frame">生成元のフレーム番号。</param>
    /// <returns>取り付け(または結果の破棄)の完了を表すタスク。</returns>
    private Task BuildPyramidAsync(RawImage image, CancellationToken ct, int frame = 0)
    {
        return _pyramidBuilds.RunAsync(
            image, frame, ct, () => BuildPyramidCoreAsync(image, ct, frame));
    }

    private async Task BuildPyramidCoreAsync(RawImage image, CancellationToken ct, int frame)
    {
        TilePyramid pyramid;
        try
        {
            pyramid = await TilePyramid.CreateAsync(image, frame, cancellationToken: ct);
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            return;
        }

        // 生成中にフレームを移した・画像を差し替えた場合は捨てる。フレームの送りは表示中の画像・
        // フレームの世代(_loadCts)を進めてトークンを取り消すので、取り消しの前に生成が終わっていても、
        // 前フレーム用のピラミッドで現フレームのものを上書きしない(画像の差し替えは画像でも照合する)
        if (ct.IsCancellationRequested || !ReferenceEquals(image, _currentImage))
        {
            return;
        }

        _mainPyramid = pyramid;
        _mainPyramidFrame = frame;
        if (_derivedImage is null)
        {
            Viewport.SetPyramid(pyramid, frame);
        }
    }

    /// <summary>
    /// カラー系表示に入る直前に、Bayer位相を保った縮小ピラミッドを用意する。
    /// </summary>
    /// <remarks>
    /// 全画像で先に作ると読み込みのたびに元画像をもう一度全走査することになるため、
    /// 実際に縮小描画が必要なカラー表示へ入るときだけ生成する。
    /// </remarks>
    private async Task EnsureBayerPyramidAsync()
    {
        RawImage? image = ActiveImage;
        RawFormat? format = ActiveFormat;
        if (image is null || format is null || format.Bayer == BayerPattern.None)
        {
            return;
        }

        bool derived = _derivedImage is not null;
        if ((derived ? _derivedBayerPyramid : _mainBayerPyramid) is not null)
        {
            return;
        }

        int frame = derived ? 0 : Viewport.Frame;

        // 表示中の画像・フレームの世代(_loadCts)のトークンで作る。フレームの送り・画像の差し替えで取り消される
        CancellationToken ct = _loadCts?.Token ?? default;

        // 生成中はまだ取り付けていないので、上の確認だけでは表示モードの切替(Bayerカラー→現像など)で
        // 同じピラミッドの生成を重ねて始め、後から終わった方が先に取り付けた方を描画中に破棄していた。
        // 同じ画像・フレーム・世代の生成が進行中なら新たに始めず、その完了(取り付け)を待つ
        await _bayerPyramidBuilds.RunAsync(
            image, frame, ct, () => BuildBayerPyramidAsync(image, format, derived, frame, ct));
    }

    /// <summary>
    /// Bayerピラミッドを作り、生成元の画像・フレーム・世代のままなら取り付ける。
    /// </summary>
    /// <param name="image">生成元の画像(元画像、またはHDR派生ビューの画像)。</param>
    /// <param name="format">Bayerパターンを含むフォーマット。</param>
    /// <param name="derived">生成元がHDR派生ビューの画像か。</param>
    /// <param name="frame">生成元のフレーム番号。</param>
    /// <param name="ct">表示中の画像・フレームの世代のトークン。</param>
    /// <returns>取り付け(または結果の破棄)の完了を表すタスク。</returns>
    private async Task BuildBayerPyramidAsync(
        RawImage image, RawFormat format, bool derived, int frame, CancellationToken ct)
    {
        BayerPyramid bayer;
        try
        {
            bayer = await BayerPyramid.CreateAsync(
                image, format, frame, cancellationToken: ct);
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            // キャンセル、または画像切替と競合して生成元が破棄された。
            // 作り直しは次の表示切替に任せる
            return;
        }

        // 取り消しの前に生成が終わっていた前の世代の結果も捨てる(グレーのピラミッドと同じ規約)。
        // フレームを移して戻ったときに、戻った後で始めた生成の結果と二重に取り付けない
        if (ct.IsCancellationRequested
            || !ReferenceEquals(image, ActiveImage) || frame != (derived ? 0 : Viewport.Frame))
        {
            bayer.Dispose();
            return;
        }

        if (derived)
        {
            _derivedBayerPyramid?.Dispose();
            _derivedBayerPyramid = bayer;
        }
        else
        {
            _mainBayerPyramid?.Dispose();
            _mainBayerPyramid = bayer;
            _mainBayerPyramidFrame = frame;
        }

        Viewport.SetBayerPyramid(bayer, frame);
    }

    private void UpdateFormatPanel(RawFormat format)
    {
        string packing = format.Packing == BitPacking.Lsb ? "下詰め" : "上詰め";
        _vm.FmtBitDepthText = $"{format.BitDepth}bit {packing}";
        _vm.FmtEndianText = format.Endianness == Endianness.Little ? "Little" : "Big";
        _updatingFormatPanel = true;
        FmtBayerCombo.SelectedIndex = format.Bayer switch
        {
            BayerPattern.Rggb => 0,
            BayerPattern.Bggr => 1,
            BayerPattern.Grbg => 2,
            BayerPattern.Gbrg => 3,
            _ => 4,
        };
        _updatingFormatPanel = false;
        _vm.FmtHdrText = DescribeHdr(format);
    }

    /// <summary>フォーマットパネル用のHDR設定の要約。</summary>
    private static string DescribeHdr(RawFormat format)
    {
        if (format.Hdr == HdrMode.None)
        {
            return "なし";
        }

        string layout;
        try
        {
            layout = HdrSplitter.ResolveLayout(format, format.HdrStages) switch
            {
                HdrMode.LineInterleaved => "行交互",
                HdrMode.FrameSequential => "フレーム連結",
                _ => "?",
            };
            if (format.Hdr == HdrMode.Auto)
            {
                layout += "(自動)";
            }
        }
        catch (InvalidOperationException)
        {
            layout = "レイアウト不明";
        }

        string detail = format.Hdr == HdrMode.FrameSequential
            ? ""
            : $" / {format.EffectiveHdrLineBlock}行単位"
                + (format.HdrRowOffset != 0 ? $" / 行オフセット{format.HdrRowOffset:+#;-#;0}" : "");
        return $"{layout} {format.HdrStages}段 (露光比 {format.ExposureRatio:F0}){detail}";
    }

    // ---- ヒストグラム・ROI解析 ----

    private async void RefreshHistogram(RegionOfInterest? roi)
    {
        if (ActiveImage is null)
        {
            return;
        }

        RawImage image = ActiveImage;
        int frame = Viewport.Frame;
        var analyzed = new AnalysisSource(image, frame);
        BayerPattern pattern = ActiveFormat?.Bayer ?? BayerPattern.None;
        bool byChannel = _vm.HistogramByChannel && pattern != BayerPattern.None;

        // ROIは表示座標。チャネル分割表示ではタイル画像の座標なので、
        // 表示されている画素の集合へ対応づけてから集計する
        RoiAnalysisTarget target = ResolveRoiTarget(image, roi);
        if (target is UnsupportedRoiTarget unsupported)
        {
            // 画像全体など別の画素で代わりに集計すると、ROIの統計と誤読される
            ReplaceAnalysisCts(null);
            ShowUnanalyzableRoi(unsupported.Reason);
            return;
        }

        var cts = new CancellationTokenSource();
        ReplaceAnalysisCts(cts);
        RoiHistogram analysis;
        try
        {
            analysis = await Task.Run(
                () => RoiAnalysis.ComputeHistogram(
                    image, frame, target, pattern, byChannel, cts.Token),
                cts.Token);
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            return; // キャンセル、または解析中に画像が差し替わった
        }

        // 解析中に画像を差し替えた・フレームを送ったなら、結果は表示中の画像・フレームのものではない
        // (フレーム送りは画像がそのままなので、画像の照合だけでは前のフレームの統計を表示してしまう)
        if (cts.IsCancellationRequested || !analyzed.IsCurrent(ActiveImage, Viewport.Frame))
        {
            return;
        }

        HistogramResult result = analysis.Histogram;
        RegionStatistics? exactStats = analysis.RoiStatistics;
        _histogram = result;
        _channelHistograms = analysis.Channels;
        UpdateChannelStatsPanel();
        bool statsSampled = exactStats is { } s && s.SampleCount < RoiAnalysis.PixelCount(target);
        _vm.HistogramIsSampled = result.IsSampled || statsSampled;
        RegionStatistics stats = exactStats ?? result.Statistics;
        _vm.HistMeanSigmaText = $"{stats.Mean:F1} / {stats.Sigma:F1}";
        _vm.HistMinMaxText = $"{stats.Min} / {stats.Max}";

        HistogramMetrics metrics = ImageAnalysis.ComputeHistogramMetrics(result);
        _vm.HistMedianModeText = $"{metrics.Median} / {metrics.Mode}";
        _vm.HistPercentileText = $"{metrics.P1} / {metrics.P99}";
        _vm.HistClipText = $"{metrics.SaturatedPercent:F2}% / {metrics.ZeroPercent:F2}%";
        _vm.HistDynamicRangeText = metrics.DynamicRangeDb > 0
            ? $"{metrics.DynamicRangeDb:F1} dB"
            : "—";
        RedrawHistogram();

        if (exactStats is { } es)
        {
            _vm.RoiOverlayText = target switch
            {
                ChannelRoiTarget channel =>
                    $"ROI: {channel.Region.Width}×{channel.Region.Height} " +
                    $"({BayerHelper.GetLabel(channel.Channel)}のみ)  mean {es.Mean:F1}  σ {es.Sigma:F1}",
                SourceRoiTarget source =>
                    $"ROI: {source.Roi.Width}×{source.Roi.Height}  mean {es.Mean:F1}  σ {es.Sigma:F1}",
                _ => "",
            };
            _vm.HasRoi = true;
        }
    }

    /// <summary>
    /// 表示中のROI(表示座標)を、解析で集計する元画像の画素の集合へ対応づける。
    /// </summary>
    /// <param name="image">表示中の画像。</param>
    /// <param name="displayRoi">表示座標のROI(なければnull)。</param>
    /// <returns>集計対象。</returns>
    private RoiAnalysisTarget ResolveRoiTarget(RawImage image, RegionOfInterest? displayRoi)
    {
        return RoiAnalysis.Resolve(
            displayRoi, Viewport.IsChannelSplitLayout, image.Width, image.Height,
            ActiveFormat?.Bayer ?? BayerPattern.None);
    }

    /// <summary>ROIが選ばれていて、表示中の画素へ対応づけて解析できるか。</summary>
    private bool HasAnalyzableRoi =>
        ActiveImage is { } image && Viewport.Roi is { PixelCount: > 0 } roi
        && RoiAnalysis.IsAnalyzableRoi(ResolveRoiTarget(image, roi));

    /// <summary>
    /// 解析できないROIであることを示し、ヒストグラム欄を空にする(別の画素の統計を残さない)。
    /// </summary>
    /// <param name="reason">利用者向けの理由。</param>
    private void ShowUnanalyzableRoi(string reason)
    {
        _histogram = null;
        _channelHistograms = null;
        UpdateChannelStatsPanel();
        _vm.HistogramIsSampled = false;
        _vm.HistMeanSigmaText = "— / —";
        _vm.HistMinMaxText = "— / —";
        _vm.HistMedianModeText = "— / —";
        _vm.HistPercentileText = "— / —";
        _vm.HistClipText = "— / —";
        _vm.HistDynamicRangeText = "—";
        RedrawHistogram();
        _vm.RoiOverlayText = reason;

        // 枠は残るので、解除(右クリック / Ctrl+G)できる状態にしておく
        _vm.HasRoi = true;
    }

    private void UpdateChannelStatsPanel()
    {
        if (_channelHistograms is { Count: 4 } channels)
        {
            _vm.ChRText = FormatChannelStats(channels[0].Statistics);
            _vm.ChGrText = FormatChannelStats(channels[1].Statistics);
            _vm.ChGbText = FormatChannelStats(channels[2].Statistics);
            _vm.ChBText = FormatChannelStats(channels[3].Statistics);
            _vm.ChannelStatsVisible = true;
        }
        else
        {
            _vm.ChannelStatsVisible = false;
        }
    }

    private static string FormatChannelStats(RegionStatistics stats)
    {
        return $"{stats.Mean:F1} / {stats.Sigma:F1}";
    }

    private void RedrawHistogram()
    {
        if (_histogram is null)
        {
            _vm.HistogramSource = null;
            return;
        }

        _vm.HistogramSource = _channelHistograms is { Count: 4 } channels
            ? RenderChannelHistogram(channels, _vm.HistogramIsLog, _vm.HistogramIsCumulative)
            : RenderHistogram(_histogram.Bins, _vm.HistogramIsLog, _vm.HistogramIsCumulative);
    }

    private static ImageSource RenderChannelHistogram(
        IReadOnlyList<ChannelHistogram> channels, bool logScale, bool cumulative)
    {
        const int width = 210;
        const int height = 70;
        var curves = new double[channels.Count][];
        double maxValue = 0;
        for (int c = 0; c < channels.Count; c++)
        {
            double[] columns = HistogramTools.Aggregate(channels[c].Bins, width, cumulative);
            curves[c] = columns;
            maxValue = Math.Max(maxValue, columns.Max());
        }

        double maxScale = logScale ? Math.Log(1 + maxValue) : maxValue;
        var pixels = new byte[width * height * 4];

        // チャネル色 (BGR順): R, Gr, Gb, B
        var colors = new (byte B, byte G, byte R)[]
        {
            (0x4E, 0x52, 0xE0), (0x72, 0xCB, 0x7E), (0x4E, 0x9A, 0x4E), (0xD9, 0x9D, 0x5B),
        };
        for (int c = 0; c < curves.Length; c++)
        {
            for (int x = 0; x < width; x++)
            {
                double value = logScale ? Math.Log(1 + curves[c][x]) : curves[c][x];
                int barHeight = maxScale > 0 ? (int)(value / maxScale * (height - 2)) : 0;
                int top = Math.Clamp(height - 1 - barHeight, 0, height - 2);
                for (int y = top; y < Math.Min(height, top + 2); y++)
                {
                    int offset = (y * width + x) * 4;
                    pixels[offset] = colors[c].B;
                    pixels[offset + 1] = colors[c].G;
                    pixels[offset + 2] = colors[c].R;
                    pixels[offset + 3] = 0xFF;
                }
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static ImageSource RenderHistogram(long[] bins, bool logScale, bool cumulative)
    {
        const int width = 210;
        const int height = 70;
        // cumulative時は縦軸=累積頻度(そのcode以下の画素数)
        double[] columns = HistogramTools.Aggregate(bins, width, cumulative);
        double maxValue = columns.Max();
        double maxScale = logScale ? Math.Log(1 + maxValue) : maxValue;
        var pixels = new byte[width * height * 4];
        for (int x = 0; x < width; x++)
        {
            double value = logScale ? Math.Log(1 + columns[x]) : columns[x];
            int barHeight = maxScale > 0 ? (int)(value / maxScale * (height - 2)) : 0;
            for (int y = height - barHeight; y < height; y++)
            {
                int offset = (y * width + x) * 4;
                bool accent = cumulative;
                pixels[offset] = accent ? (byte)0xD9 : (byte)0x85;
                pixels[offset + 1] = accent ? (byte)0x9D : (byte)0x8A;
                pixels[offset + 2] = accent ? (byte)0x5B : (byte)0x8A;
                pixels[offset + 3] = 0xFF;
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnHistogramRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshHistogram(Viewport.Roi);
    }

    private void OnViewportRoiChanged(object? sender, EventArgs e)
    {
        // 象限をまたぐなど解析できないROIでは「ROI内のみ」を選ばせない
        _noiseWindow?.SetRoiAvailability(HasAnalyzableRoi);
        if (Viewport.Roi is { PixelCount: > 0 } roi)
        {
            RefreshHistogram(roi);
        }
        else
        {
            _vm.HasRoi = false;
            _vm.RoiOverlayText = "";
            RefreshHistogram(roi: null);
        }
    }

    private void OnRoiToggleChanged(object sender, RoutedEventArgs e)
    {
        if (RoiToggle.IsChecked == true)
        {
            ProfileToggle.IsChecked = false;
            WbPickToggle.IsChecked = false;
            Viewport.InteractionMode = ViewportInteractionMode.RoiSelect;
        }
        else if (Viewport.InteractionMode == ViewportInteractionMode.RoiSelect)
        {
            // モードを抜けてもROI選択自体は保持する(射影プロファイル等で使うため)。
            // 解除は右クリックメニューの「ROIを解除」から行う
            Viewport.InteractionMode = ViewportInteractionMode.Pan;
        }
    }

    private void OnProfileToggleChanged(object sender, RoutedEventArgs e)
    {
        if (ProfileToggle.IsChecked == true)
        {
            RoiToggle.IsChecked = false;
            WbPickToggle.IsChecked = false;
            Viewport.InteractionMode = ViewportInteractionMode.LineProfile;
        }
        else if (Viewport.InteractionMode == ViewportInteractionMode.LineProfile)
        {
            Viewport.InteractionMode = ViewportInteractionMode.Pan;
        }
    }

    /// <summary>
    /// 表示上のクリック座標を元画像の座標へ写像する。
    /// チャネル分割表示は R/Gr/Gb/B の2x2タイル並置なので、タイル内座標を
    /// 元のBayer座標に戻さないと、クリックした位置と異なる画素を読むことになる。
    /// </summary>
    /// <returns>写像できない座標(端数行/列)ならfalse。</returns>
    private bool TryMapToSourceCoordinates(
        RawImage image, int x, int y, out int sourceX, out int sourceY)
    {
        sourceX = x;
        sourceY = y;

        // 表示モードが分割でもBayerなしならRawとして描かれている(表示座標=元画像座標)
        if (!Viewport.IsChannelSplitLayout)
        {
            return true;
        }

        int evenW = image.Width & ~1;
        int evenH = image.Height & ~1;
        if (x >= evenW || y >= evenH)
        {
            return false;
        }

        (sourceX, sourceY) = BayerSplit.MapTiledToSource(x, y, evenW, evenH);
        return true;
    }

    private void OnProfilePointClicked(object? sender, CursorPixelEventArgs e)
    {
        if (ActiveImage is not { } image
            || !TryMapToSourceCoordinates(image, e.X, e.Y, out int sourceX, out int sourceY))
        {
            return;
        }

        _ = ShowLineProfileAsync(sourceX, sourceY, fromClick: true);
    }

    /// <summary>
    /// 表示画像を送った・差し替えた後に、開いているラインプロファイル窓を同じ基準点・方向で計算し直す。
    /// </summary>
    /// <remarks>
    /// ヒストグラム・ROI統計と同じ扱い。フレーム・ページ・ファイルの送り(再生中は止めたとき。
    /// <see cref="RefreshAfterSequenceMove"/>)と、別ファイルを開く・処理結果での差し替え・HDR表示の出入りの後に
    /// 呼ぶ。射影は送った後(差し替えた後)のROIで求める。基準点は元画像の座標のまま使い、寸法の違う画像で
    /// 範囲外になったら前の断面を残さず範囲外であることを示す。窓を閉じていれば何もしない。
    /// </remarks>
    private void RefreshLineProfile()
    {
        if (_profileWindow is not { } window)
        {
            return;
        }

        (int x, int y) = window.CurrentPoint;
        _ = ShowLineProfileAsync(x, y, fromClick: false);
    }

    /// <summary>
    /// 表示中の画像・フレームで、基準点を通る断面と表示中のROIの射影を計算し、ラインプロファイル窓に出す。
    /// </summary>
    /// <param name="sourceX">基準点の元画像X座標。</param>
    /// <param name="sourceY">基準点の元画像Y座標。</param>
    /// <param name="fromClick">
    /// 画像上のクリックからか。クリックなら窓を開いて前面に出す。送り・差し替えの後の計算し直しでは、
    /// 計算中に窓が閉じられていたら開き直さない。
    /// </param>
    private async Task ShowLineProfileAsync(int sourceX, int sourceY, bool fromClick)
    {
        if (ActiveImage is not { } image)
        {
            return;
        }

        int frame = Viewport.Frame;
        var analyzed = new AnalysisSource(image, frame);
        RegionOfInterest? roi = Viewport.Roi is { PixelCount: > 0 } r ? r : null;

        // 射影はROIに表示されている画素だけで取る(チャネル分割では1チャネルの格子)。
        // 象限をまたぐなど対応づけられないROIの射影は出さない。
        // 射影の横軸はROIを描いた表示座標で示す
        RoiAnalysisTarget target = ResolveRoiTarget(image, roi);
        RegionOfInterest? projectionRoi = target switch
        {
            SourceRoiTarget source => source.Roi,
            ChannelRoiTarget channel => channel.DisplayRoi,
            _ => null,
        };

        // 全面ROIの巨大画像では射影に時間がかかる。次のクリックや
        // 画像切替で確実に打ち切れるようにトークンを渡す
        var cts = new CancellationTokenSource();
        ReplaceProfileCts(cts);
        CancellationToken token = cts.Token;
        LineProfileData? data;
        try
        {
            data = await Task.Run(
                () => LineProfileData.Compute(image, frame, sourceX, sourceY, target, token), token);
        }
        catch (Exception)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        // 計算中に画像を差し替えた・フレームを送ったなら、プロファイルは表示中の画像・フレームのものではない
        // (フレーム送りは画像がそのままなので、画像の照合だけでは前のフレームの値を表示してしまう)
        if (!analyzed.IsCurrent(ActiveImage, Viewport.Frame))
        {
            return;
        }

        if (_profileWindow is null)
        {
            if (!fromClick)
            {
                return;
            }

            _profileWindow = new LineProfileWindow { Owner = this };
            _profileWindow.DirectionChanged += horizontal =>
            {
                // 基準点が範囲外で断面を出していないときは、マーカーを出さない
                if (_profileWindow is { IsOutsideImage: false } window)
                {
                    // マーカーはプロファイルと同じ元画像の列・行で渡す
                    // (表示のどこに並ぶかはビューポートが表示モードに合わせて写す)
                    (int px, int py) = window.CurrentPoint;
                    Viewport.SetProfileMarker(px, py, horizontal);
                }
            };
            _profileWindow.Closed += (_, _) =>
            {
                _profileWindow = null;
                Viewport.ClearProfileMarker();
            };
            _profileWindow.Show();
        }

        int maxCode = (1 << ActiveFormat!.BitDepth) - 1;
        if (data is null)
        {
            // 寸法の違う画像へ送った・差し替えたため、基準点が範囲外になった
            _profileWindow.ShowOutsideImage(sourceX, sourceY, image.Width, image.Height, maxCode);
            Viewport.ClearProfileMarker();
        }
        else
        {
            _profileWindow.SetProfiles(
                data.Row, data.Column, data.HorizontalProjection, data.VerticalProjection, projectionRoi,
                sourceX, sourceY, maxCode);

            // 表示座標(e.X, e.Y)で渡すと、分割⇔非分割の切替後にプロファイルと別の行・列を指す
            Viewport.SetProfileMarker(sourceX, sourceY, _profileWindow.IsHorizontal);
        }

        if (fromClick)
        {
            _profileWindow.Activate();
        }
    }

    // ---- 表示調整 (LUT) ----

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HistogramIsLog)
            or nameof(MainViewModel.HistogramIsCumulative))
        {
            RedrawHistogram();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.HistogramByChannel))
        {
            RefreshHistogram(Viewport.Roi is { PixelCount: > 0 } roi ? roi : null);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.ZebraOn))
        {
            Viewport.SetZebra(_vm.ZebraOn);
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsFullscreen))
        {
            CapturePanelWidths();
            ApplyFullscreen(_vm.IsFullscreen);
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.LeftPanelVisible)
            or nameof(MainViewModel.RightPanelVisible))
        {
            CapturePanelWidths();
            UpdatePanelLayout();
            return;
        }

        if (_updatingSliders)
        {
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.Gain)
            or nameof(MainViewModel.Gamma)
            or nameof(MainViewModel.Contrast)
            or nameof(MainViewModel.BlackLevel)
            or nameof(MainViewModel.WhiteLevel))
        {
            if (e.PropertyName is nameof(MainViewModel.BlackLevel)
                or nameof(MainViewModel.WhiteLevel))
            {
                ApplyLevelCodes(_vm.BlackLevel, _vm.WhiteLevel);
            }

            ApplyDisplayParametersToViews();
        }

        if (e.PropertyName is nameof(MainViewModel.WbGainR) or nameof(MainViewModel.WbGainG)
            or nameof(MainViewModel.WbGainB)
            && _vm.HasImage)
        {
            UpdateDevelopLuts();
        }
    }

    /// <summary>
    /// 現在の表示調整パラメータを、いま画面に効いているLUT経路すべてへ反映する。
    /// スライダー変更・リセット・自動コントラストで共通に使う。
    /// Raw表示のLUTだけ更新すると、HDR分割(SegmentLuts)やカラー現像(_developLuts)
    /// 表示中に見た目が変わらず「効かないボタン」になる。
    /// </summary>
    private void ApplyDisplayParametersToViews()
    {
        if (!_vm.HasImage)
        {
            return;
        }

        if (_hdrFrameParams is not null)
        {
            // HDR分割表示中は調整対象フレームのLUTのみ更新する
            DisplayParameters parameters = CurrentDisplayParameters();
            int target = HdrTargetCombo.SelectedIndex;
            if (target <= 0)
            {
                for (int i = 0; i < _hdrFrameParams.Length; i++)
                {
                    _hdrFrameParams[i] = parameters;
                }
            }
            else
            {
                int frame = Math.Min(target - 1, _hdrFrameParams.Length - 1);
                _hdrFrameParams[frame] = parameters;
            }

            ApplySplitLuts();
            return;
        }

        Viewport.SetLut(BuildLut());

        // ゲイン・コントラストも現像LUTに影響する。ここを絞ると
        // ColorDevelopモードでスライダーが完全に無反応になる
        UpdateDevelopLuts();
    }

    private DevelopParameters CurrentDevelopParameters()
    {
        double gamma = _vm.Gamma > 0 ? _vm.Gamma : 1.0;
        // 白点(_whitePoint)は CurrentShift と同じビット深度で白レベルcodeの上端に置いている。
        // 白飛び(白レベルのcode以上)の判定を code で行えるよう、同じビット深度を渡す
        return new DevelopParameters(
            _blackPoint, _vm.WbGainR, _vm.WbGainG, _vm.WbGainB, gamma,
            _colorMatrix.IsIdentity ? null : _colorMatrix,
            _whitePoint, _vm.Gain, _vm.Contrast,
            SourceBitDepth: ActiveFormat?.BitDepth ?? 16);
    }

    /// <summary>
    /// 現像LUTの再生成を予約する。カラー現像表示中でなければ何もせず、実際の生成
    /// (65536×3回のMath.Powを含む)はカラー現像表示に入るとき(<see cref="EnsureDevelopLuts"/> の照合)まで遅らせる。
    /// 表示中でもスライダー連続操作で毎ティック作り直さないよう間引く。
    /// </summary>
    private void UpdateDevelopLuts()
    {
        if (Viewport.DisplayMode != ViewportDisplayMode.ColorDevelop)
        {
            return;
        }

        _developLutTimer ??= CreateDevelopLutTimer();
        _developLutTimer.Stop();
        _developLutTimer.Start();
    }

    private DispatcherTimer CreateDevelopLutTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            EnsureDevelopLuts();
        };
        return timer;
    }

    /// <summary>
    /// 現像LUTを現在のパラメータ(表示中の画像のビット深度を含む)と照合し、違えば作り直してビューポートへ渡す
    /// (カラー現像表示に入るとき・カラー現像のまま画像を差し替えたとき・間引きの後)。
    /// </summary>
    /// <remarks>
    /// 作り直しの印に頼ると、印を立て忘れた経路(HDR派生ビューへの出入りでビット深度が変わる)で旧ビット深度の
    /// LUTのまま描き、白飛びの判定を誤る(DevelopLutCache)。
    /// </remarks>
    private void EnsureDevelopLuts()
    {
        _developLutTimer?.Stop();
        if (_developLutCache.Refresh(CurrentDevelopParameters()) is { } luts)
        {
            Viewport.SetDevelopLuts(luts);
        }
    }

    // ---- カラーマトリクス ----

    private void OnMatrixChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingMatrixBoxes || M33Box is null || MatrixStateText is null)
        {
            return;
        }

        var boxes = new[]
        {
            M11Box, M12Box, M13Box, M21Box, M22Box, M23Box, M31Box, M32Box, M33Box,
        };
        var values = new double[9];
        for (int i = 0; i < 9; i++)
        {
            // NaN/Infinity が入ると現像結果が全画素破綻するので有限値だけ通す
            if (!NumericInput.TryParseFinite(boxes[i].Text, out values[i]))
            {
                MatrixStateText.Text = "入力エラー";
                return;
            }
        }

        _colorMatrix = new ColorMatrix(
            values[0], values[1], values[2],
            values[3], values[4], values[5],
            values[6], values[7], values[8]);
        MatrixStateText.Text = _colorMatrix.IsIdentity ? "単位行列 (無効)" : "適用中";
        if (_vm.HasImage)
        {
            UpdateDevelopLuts();
        }
    }

    private void OnMatrixResetClick(object sender, RoutedEventArgs e)
    {
        _updatingMatrixBoxes = true;
        M11Box.Text = "1.00";
        M12Box.Text = "0.00";
        M13Box.Text = "0.00";
        M21Box.Text = "0.00";
        M22Box.Text = "1.00";
        M23Box.Text = "0.00";
        M31Box.Text = "0.00";
        M32Box.Text = "0.00";
        M33Box.Text = "1.00";
        _updatingMatrixBoxes = false;
        _colorMatrix = ColorMatrix.Identity;
        MatrixStateText.Text = "単位行列 (無効)";
        if (_vm.HasImage)
        {
            UpdateDevelopLuts();
        }
    }

    // ---- メニュー・エクスポート ----

    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        List<string> recent = _recentFiles.Load();
        if (recent.Count == 0)
        {
            RecentMenu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "(なし)",
                IsEnabled = false,
            });
            return;
        }

        foreach (string path in recent)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = Path.GetFileName(path),
                ToolTip = path,
            };
            string captured = path;
            item.Click += async (_, _) =>
            {
                // 連番判定はファイル一覧を見るので、一覧が揃ってから開く
                await LoadFolderAsync(Path.GetDirectoryName(captured)!, captured);
                OpenPath(captured);
            };
            RecentMenu.Items.Add(item);
        }

        // 存在確認は切断されたNAS/UNCパスでSMBタイムアウトまでブロックするため、
        // メニューは先に出してから裏で確認し、消えていた項目だけ後から取り除く
        int generation = ++_recentMenuGeneration;
        _ = PruneMissingRecentItemsAsync(generation, recent);
    }

    /// <summary>最近使ったファイルのうち実在しないものをメニューから取り除く。</summary>
    /// <param name="generation">起動した時点の世代(後発の再構築があれば破棄する)。</param>
    /// <param name="paths">確認対象のパス。</param>
    /// <returns>取り除きの完了を表すタスク。</returns>
    private async Task PruneMissingRecentItemsAsync(int generation, List<string> paths)
    {
        List<string> missing;
        try
        {
            missing = await Task.Run(() => paths.Where(p => !File.Exists(p)).ToList());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"最近使ったファイルの存在確認に失敗: {ex.Message}");
            return;
        }

        if (generation != _recentMenuGeneration || missing.Count == 0)
        {
            return; // 確認中に作り直された(結果は古い)
        }

        foreach (System.Windows.Controls.MenuItem item in RecentMenu.Items
                     .OfType<System.Windows.Controls.MenuItem>()
                     .Where(i => i.ToolTip is string path && missing.Contains(path))
                     .ToList())
        {
            RecentMenu.Items.Remove(item);
        }

        if (RecentMenu.Items.Count == 0)
        {
            RecentMenu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "(なし)",
                IsEnabled = false,
            });
        }
    }

    private void OnMenuDisplayModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: string tag }
            && int.TryParse(tag, out int index))
        {
            DisplayModeCombo.SelectedIndex = index;
        }
    }

    private string? BuildHistogramTable(char separator)
    {
        return _histogram is null
            ? null
            : HistogramTools.BuildTable(_histogram.Bins, separator, _channelHistograms);
    }

    private void OnHistogramCopyClick(object sender, RoutedEventArgs e)
    {
        string? table = BuildHistogramTable('\t');
        if (table is not null)
        {
            _vm.ImageInfoText = ClipboardHelper.TrySetText(table)
                ? "ヒストグラムをクリップボードへコピーしました"
                : "クリップボードを使用できませんでした(他のアプリが使用中の可能性があります)";
        }
    }

    private void OnHistogramSaveCsvClick(object sender, RoutedEventArgs e)
    {
        string? table = BuildHistogramTable(',');
        if (table is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = Path.GetFileNameWithoutExtension(_currentPath ?? "image") + "_hist.csv",
        };
        if (dialog.ShowDialog(this) == true
            && ClipboardHelper.WriteTextOrWarn(this, dialog.FileName, table, "ヒストグラム保存"))
        {
            _vm.ImageInfoText = $"保存完了: {Path.GetFileName(dialog.FileName)}";
        }
    }

    private void OnOpenPresetFolderClick(object sender, RoutedEventArgs e)
    {
        string directory = Path.GetDirectoryName(_presetStore.FilePath)!;
        Directory.CreateDirectory(directory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    private void OnUsageGuideClick(object sender, RoutedEventArgs e)
    {
        ShowInfoWindow("操作ガイド",
            "キーボード操作\n" +
            "  Ctrl+Shift+P: コマンドパレット(全機能を検索して実行)\n" +
            "  F1: ショートカット一覧\n" +
            "  矢印: パン / Ctrl+矢印: 画素カーソルを1画素移動\n" +
            "  起動引数にパスを渡すとそのファイルを直接開く\n\n" +
            "マウス操作\n" +
            "  ホイール: カーソル中心ズーム\n" +
            "  ドラッグ: パン / ダブルクリック: 全体表示\n" +
            "  ズーム3200%以上で画素上にraw値を表示\n\n" +
            "解析\n" +
            "  ROI: ツールバーのROIを押してドラッグで矩形選択\n" +
            "  ラインプロファイル: モードを押して画素をクリック\n" +
            "  ヒストグラム/プロファイルは右クリックでデータ保存\n\n" +
            "ホワイトバランス\n" +
            "  AWB(グレーワールド): 全体平均から自動計算\n" +
            "  スポイト: クリック画素を無彩色として計算\n\n" +
            "HDR\n" +
            "  フォーマットでHDR方式を指定 → 表示モードで分割/合成");
    }

    // ---- 比較モード ----

    private bool _compareMode;

    private async void OnCompareModeClick(object sender, RoutedEventArgs e)
    {
        await ToggleCompareModeAsync();
    }

    /// <summary>比較モードへ入る/抜ける。</summary>
    private async Task ToggleCompareModeAsync()
    {
        if (_compareMode)
        {
            await ExitCompareModeAsync();
        }
        else
        {
            await EnterCompareModeAsync();
        }
    }

    private async Task EnterCompareModeAsync()
    {
        if (_compareMode)
        {
            return;
        }

        CancelTiffPageLoad();
        StopPlayback();
        _compareMode = true;
        _vm.IsCompareMode = true;
        CompareArea.Visibility = Visibility.Visible;

        // 開いている画像があれば最初のペイン(A)として読み直す。
        // メイン表示の画像を共有せず独立にロードするのは、所有権を
        // 比較モードに閉じ、終了時の後始末を単純にするため
        // (補正・HDR適用中はファイルと内容が一致しないため対象外)
        if (CompareArea.PaneCount == 0 && _currentPath is not null
            && _derivedImage is null && _correctionLabel is null)
        {
            await CompareArea.AddPaneFromPathAsync(_currentPath);
        }
    }

    private async Task ExitCompareModeAsync()
    {
        if (!_compareMode)
        {
            return;
        }

        await CompareArea.CloseAllAsync();
        CompareArea.Visibility = Visibility.Collapsed;
        _compareMode = false;
        _vm.IsCompareMode = false;

        // 比較ペインの推定の通知から、表示中のファイルの通知へ戻す
        _vm.FormatNoticeText = _mainFormatNotice;
        _vm.FormatNoticeToolTip = _mainFormatNoticeToolTip;
    }

    /// <summary>ファイル選択ダイアログを出して比較ペイン資源を用意する。</summary>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>読み込んだ資源。キャンセル・失敗はnull。</returns>
    private async Task<Compare.ComparePane?> PickComparePaneAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog { Filter = OpenImageFilter };
        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        return await LoadComparePaneAsync(dialog.FileName, cancellationToken);
    }

    /// <summary>
    /// 指定パスから比較ペイン資源を読み込む。rawは通常の「開く」と同じく、同じパスの記憶 →
    /// 同じサイズの記憶(自動適用がちょうど1つ)の順に使い、なければインポートダイアログで確認する。
    /// </summary>
    /// <param name="path">対象ファイル。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>読み込んだ資源。キャンセル・失敗はnull。</returns>
    private async Task<Compare.ComparePane?> LoadComparePaneAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            RawFormat? format = null;
            RawFormatOrigin? origin = null;
            bool? autoOpenChoice = null;
            if (Compare.ComparePane.IsRawFile(path))
            {
                long size = SafeFileSize(path);
                RawOpenPlan plan = RawOpenPlanner.Plan(
                    path, size, TryGetRememberedFormat(path, size), _formatMemory.History,
                    _currentFormat);
                origin = plan.Origin;
                format = plan.Format;
                if (format is null)
                {
                    var dialog = new RawImportDialog(
                        path, _presetStore, plan.DialogInitial, _formatMemory, _currentFormat)
                    {
                        Owner = this,
                    };
                    if (dialog.ShowDialog() != true || dialog.Result is null)
                    {
                        return null;
                    }

                    format = dialog.Result;
                    autoOpenChoice = dialog.AutoOpenNextTime;
                    RememberFileFormat(path, format);
                }
            }

            Compare.ComparePane pane = await Compare.ComparePane.LoadAsync(
                path, format, cancellationToken);
            if (format is not null)
            {
                // 読み込めたら同じサイズ・拡張子の記憶へ記録する(同じパスの記憶は従来どおりダイアログの確定時)
                long loadedSize = SafeFileSize(path);
                _formatMemory.RememberLoaded(path, loadedSize, format, autoOpenChoice);

                // 比較ペインは F2 で開き直せないので、直し方を添えて知らせる(比較モードを抜けたら戻す)
                if (origin == RawFormatOrigin.SizeMemory && _compareMode)
                {
                    _vm.FormatNoticeText = RawOpenPlanner.CompareAutoOpenNotice(path);
                    _vm.FormatNoticeToolTip = RawOpenPlanner.AutoOpenToolTip(path, loadedSize, format);
                }
            }

            return pane;
        }
        catch (OperationCanceledException)
        {
            return null; // 比較モード終了で打ち切られた
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"読み込みに失敗しました: {ex.Message}", "比較モード",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void ShowInfoWindow(string title, string message)
    {
        var text = new System.Windows.Controls.TextBlock
        {
            Text = message,
            Margin = new Thickness(20, 16, 20, 8),
            FontSize = 12,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
        };
        var ok = new System.Windows.Controls.Button
        {
            Content = "OK",
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 8, 20, 14),
            MinWidth = 80,
        };
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(text);
        panel.Children.Add(ok);
        var window = new Window
        {
            Title = title,
            Content = panel,
            Owner = this,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
        };
        ok.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    // ---- 保存 ----

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ActiveImage is null || _currentFormat is null)
        {
            return;
        }

        if (RejectWhileImageReplacing("保存"))
        {
            return;
        }

        // ダイアログ表示中も再生タイマーは動くため、ここから保存完了まで差し替えを止める
        using BusyScope busy = EnterBusy();

        // 保存ダイアログはこの画像(とHDR合成の結果)に合わせて作る
        RawImage target = ActiveImage;
        HdrImage? targetFloat = _hdrFloatImage;

        // WIC 経路は表示中の1フレームのみ扱うため、判定も1フレームの画素数で行う
        var dialog = new SaveDialog(
            (long)target.Width * target.Height,
            allowFloatRaw: targetFloat is not null,
            hasBayer: ActiveFormat?.Bayer is not (null or BayerPattern.None))
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        SaveChoice choice = dialog.Result;
        (string filter, string extension) = choice.Format switch
        {
            SaveFormat.Tiff16 => ("TIFF (*.tif)|*.tif", ".tif"),
            SaveFormat.Png16 or SaveFormat.Png8 => ("PNG (*.png)|*.png", ".png"),
            SaveFormat.Jpeg8 => ("JPEG (*.jpg)|*.jpg", ".jpg"),
            SaveFormat.FloatRaw => ("float raw (*.fraw)|*.fraw", ".fraw"),
            _ => ("Raw (*.raw)|*.raw", ".raw"),
        };
        var fileDialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = Path.GetFileNameWithoutExtension(_currentPath ?? "image")
                + (_tiffStack?.PageSuffix(_tiffPageIndex) ?? "") + extension,
        };
        if (fileDialog.ShowDialog(this) != true)
        {
            return;
        }

        // ダイアログ(入れ子ポンプ)の間に表示中の画像が替わっていたら、ダイアログを作った画像とは別の画像を
        // 保存することになる(替える処理の途中は上で断っているが、保存する直前にも確かめる)
        if (!ReferenceEquals(target, ActiveImage) || !ReferenceEquals(targetFloat, _hdrFloatImage))
        {
            MessageBox.Show(this, "保存の準備中に表示中の画像が替わったため、保存を中止しました。" +
                "表示中の画像を確かめてから、もう一度保存してください。", "RawAnalyzer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ExecuteSave(choice, fileDialog.FileName);
    }

    private void ExecuteSave(SaveChoice choice, string path)
    {
        if (_tiffStack?.IsSourcePath(path) == true)
        {
            MessageBox.Show(this, "これは現在の1ページだけの保存です。スタック全体を失わないよう、" +
                "元TIFFとは別の名前で保存してください。", "TIFFスタックの保存",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RawImage image = ActiveImage!;
        HdrImage? hdrFloat = _hdrFloatImage;

        // 読み込んだRGB画像(JPEG/PNG/カラーTIFF)は輝度化せずそのまま保存する。
        // 派生ビュー(HDR分割・合成)の表示中は元のカラーとは別物なので対象外
        ColorImage? colorImage = _derivedImage is null ? _colorImage : null;

        // 表示中のフレームを保存する(rawは全フレーム出力なので対象外)
        int frame = Math.Clamp(Viewport.Frame, 0, image.FrameCount - 1);

        // チェック状態に応じて、適用しない処理は恒等パラメータへ落とす
        DisplayLut lut = choice.ApplyDisplayLut
            ? BuildLut()
            : DisplayLut.Create(new DisplayParameters());
        DevelopParameters developParameters = CurrentDevelopParameters();
        if (!choice.ApplyDisplayLut)
        {
            developParameters = developParameters with
            {
                BlackLevel = 0,
                Gamma = 1.0,
                WhitePoint = 65535,
                Gain = 1.0,
                Contrast = 1.0,
            };
        }

        if (!choice.ApplyWhiteBalance)
        {
            developParameters = developParameters with { GainR = 1.0, GainG = 1.0, GainB = 1.0 };
        }

        if (!choice.ApplyMatrix)
        {
            developParameters = developParameters with { Matrix = null };
        }

        ViewportDisplayMode mode = choice.ApplyDemosaic
            ? ViewportDisplayMode.ColorDevelop
            : ViewportDisplayMode.Raw;
        BayerPattern pattern = choice.ApplyDemosaic ? ActiveFormat!.Bayer : BayerPattern.None;
        var devLuts = DevelopLuts.Create(developParameters);

        // HDR分割ビューは表示調整を段ごとに持ち、段ごとのLUTで描く。表示LUTを焼き込むなら画面と同じく
        // 各段をその段の表示調整で焼き込む(スライダーの値は最後に調整した段のもの)。焼き込まないときも各段は
        // 別の画像として焼き込む。段ごとの値はここで控え、付随テキストにも同じ値を書く
        HdrSplitAdjustments? split = HdrSplitAdjustments.ForSave(
            _hdrFrameParams, _hdrSegmentWidth, choice.ApplyDisplayLut);

        ProgressWindow result = ProgressWindow.Run(
            this,
            $"保存中: {Path.GetFileName(path)}",
            (progress, ct) => Task.Run(() =>
            {
                switch (choice.Format)
                {
                    case SaveFormat.FloatRaw:
                        hdrFloat!.SaveFloatRaw(path, progress, ct);
                        break;
                    case SaveFormat.Raw:
                        RawSaver.Save(image, path, choice.Packing, choice.Endianness, progress, ct);
                        break;
                    default:
                        // TIFF/PNG/JPEG。1億画素を超えるTIFFは自前ライタで行単位に書く
                        ImageFileSaver.Save(image, frame, path, choice.Format, mode, pattern,
                            lut, devLuts, colorImage, progress, ct, split: split);
                        break;
                }
            }, ct));

        if (result.Error is not null)
        {
            MessageBox.Show(this, $"保存に失敗しました: {result.Error.Message}", "RawAnalyzer",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else if (!result.WasCanceled)
        {
            if (choice.WriteSidecar)
            {
                WriteProcessingSidecar(path, choice, developParameters, split);
            }

            // RawSaver はヘッダを出力しないため、保存したrawを開き直したときに
            // 元のHeaderOffsetのままだと開けない。出力実体に合うフォーマットを記憶する。
            // Bayerはパネルで変更した値が_currentFormat側にしか反映されないため、
            // 読み込み時のimage.FormatではなくActiveFormatから取る
            if (choice.Format == SaveFormat.Raw)
            {
                RememberFileFormat(path, image.Format with
                {
                    HeaderOffset = 0,
                    Packing = choice.Packing,
                    Endianness = choice.Endianness,
                    Bayer = ActiveFormat?.Bayer ?? image.Format.Bayer,
                });
            }

            // マルチフレームでは「どのフレームを出したか」を明示する
            string frameNote = _tiffStack is not null ? TiffPageNote : image.FrameCount > 1
                ? choice.Format == SaveFormat.Raw
                    ? $" (全{image.FrameCount}フレーム)"
                    : $" (フレーム {frame + 1}/{image.FrameCount})"
                : "";
            _vm.ImageInfoText = $"保存完了: {Path.GetFileName(path)}"
                + frameNote
                + (choice.IsProcessed ? " (処理を焼き込み)" : " (無処理)");
        }
    }

    /// <summary>保存画像に何が適用されたかを記録するテキストを書き出す。</summary>
    /// <param name="imagePath">保存した画像のパス。</param>
    /// <param name="choice">保存ダイアログの選択。</param>
    /// <param name="developParameters">保存に使った現像パラメータ。</param>
    /// <param name="split">HDR分割ビューから保存したときの段ごとの表示調整(分割ビューでなければ null)。</param>
    private void WriteProcessingSidecar(
        string imagePath, SaveChoice choice, DevelopParameters developParameters, HdrSplitAdjustments? split)
    {
        try
        {
            // HDR派生ビュー(分割・合成)の表示中も、入力フォーマットは元ファイルの形式(HDR方式を含む)を書く。
            // 派生画像の形式と作り方は[HDR派生ビュー]に書く(派生ビューでなければ ActiveFormat と同じ)
            RawFormat? format = _currentFormat;
            var sb = new StringBuilder();
            sb.AppendLine("RawAnalyzer 保存情報");
            sb.AppendLine("====================");
            sb.Append("保存日時: ").AppendLine(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.Append("出力ファイル: ").AppendLine(Path.GetFileName(imagePath));
            sb.Append("元ファイル: ").AppendLine(_currentPath ?? "(不明)");
            if (_tiffStack is not null)
            {
                sb.Append("元TIFFのページ: ").AppendLine($"{_tiffPageIndex + 1}/{_tiffStack.PageCount}");
            }
            if (_correctionLabel is not null)
            {
                sb.Append("適用済み補正: ").AppendLine(_correctionLabel);
            }

            sb.AppendLine();
            sb.AppendLine("[入力フォーマット]");
            if (format is not null)
            {
                sb.Append("  サイズ: ").Append(format.Width).Append('×')
                    .AppendLine(format.Height.ToString(CultureInfo.InvariantCulture));
                sb.Append("  ビット深度: ").Append(format.BitDepth)
                    .Append("bit ").AppendLine(
                        format.Packing == BitPacking.Lsb ? "下詰め" : "上詰め");
                sb.Append("  エンディアン: ").AppendLine(format.Endianness.ToString());
                sb.Append("  Bayer: ").AppendLine(format.Bayer.ToString());
                sb.Append("  HDR: ").Append(format.Hdr).Append(' ')
                    .AppendLine(format.Hdr == HdrMode.None
                        ? "" : $"{format.HdrStages}段 露光比{format.ExposureRatio:F1}");
            }

            // HDR派生ビューから保存した画像は元ファイルの画素ではない。どの派生ビューか(合成なら合成で減算した黒点・
            // 露光比・量子化)を書く。[適用処理]の黒点/白点は合成ビューの表示黒点・白点(合成画像の値域で黒0・白65535
            // から始まる)で、合成で減算した黒点とは別物になる
            if (_derivedImage is { } derived && format is not null)
            {
                sb.AppendLine();
                sb.Append(_hdrFloatImage is { } merged
                    ? HdrViewSidecar.DescribeMerge(derived.Format, merged, format, _hdrSourceFrame.Frame,
                        floatRawOutput: choice.Format == SaveFormat.FloatRaw)
                    : HdrViewSidecar.DescribeSplit(derived.Format, _hdrFrameParams?.Length ?? format.HdrStages,
                        format, _hdrSourceFrame.Frame));
            }

            sb.AppendLine();
            sb.AppendLine("[出力]");
            sb.Append("  形式: ").AppendLine(choice.Format.ToString());
            if (choice.Format == SaveFormat.Raw)
            {
                sb.Append("  詰め方向: ").AppendLine(
                    choice.Packing == BitPacking.Lsb ? "下詰め (LSB)" : "上詰め (MSB)");
                sb.Append("  エンディアン: ").AppendLine(choice.Endianness.ToString());
            }

            sb.Append("  要約: ").AppendLine(choice.Summary);

            sb.AppendLine();
            sb.AppendLine("[適用処理]");

            // HDR分割ビューから表示LUTを焼き込んだ画像は各段をその段の表示調整で焼き込んでいる。スライダーの値は
            // 最後に調整した段のもので、1組だけ書くと実体と食い違うので段ごとに書く
            HdrSplitAdjustments? stageAdjustments = choice.ApplyDisplayLut ? split : null;
            if (stageAdjustments is not null)
            {
                sb.Append(HdrViewSidecar.DescribeSplitDisplayLut(stageAdjustments.Stages));
            }
            else
            {
                sb.Append("  表示LUT: ").AppendLine(choice.ApplyDisplayLut ? "適用" : "なし");
            }

            if (choice.ApplyDisplayLut && stageAdjustments is null)
            {
                sb.Append("    黒点/白点: ").Append(_blackPoint).Append(" / ")
                    .AppendLine(_whitePoint.ToString(CultureInfo.InvariantCulture));
                sb.Append("    ゲイン: ")
                    .Append(_vm.GainDb.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(" dB (×")
                    .Append(_vm.Gain.ToString("F3", CultureInfo.InvariantCulture))
                    .AppendLine(")");
                sb.Append("    ガンマ: ").AppendLine(
                    _vm.Gamma.ToString("F3", CultureInfo.InvariantCulture));
                sb.Append("    コントラスト: ").AppendLine(
                    _vm.Contrast.ToString("F3", CultureInfo.InvariantCulture));
            }

            // WB/マトリクスはデモザイク経路でのみ画素に適用される。
            // チェックだけ見て「適用」と書くと、デモザイクOFF保存で来歴が実体と食い違う
            bool wbApplied = choice.ApplyWhiteBalance && choice.ApplyDemosaic;
            bool matrixApplied =
                choice.ApplyMatrix && choice.ApplyDemosaic && !_colorMatrix.IsIdentity;
            sb.Append("  ホワイトバランス: ").AppendLine(
                wbApplied ? "適用"
                : choice.ApplyWhiteBalance ? "なし(デモザイクなしのため未適用)" : "なし");
            if (wbApplied)
            {
                sb.Append("    R/G/B ゲイン: ")
                    .Append(developParameters.GainR.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(" / ")
                    .Append(developParameters.GainG.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(" / ")
                    .AppendLine(developParameters.GainB.ToString("F3", CultureInfo.InvariantCulture));
            }

            sb.Append("  カラーマトリクス: ").AppendLine(
                matrixApplied ? "適用"
                : choice.ApplyMatrix && !_colorMatrix.IsIdentity
                    ? "なし(デモザイクなしのため未適用)" : "なし");
            if (matrixApplied)
            {
                double[] m = _colorMatrix.ToArray();
                for (int row = 0; row < 3; row++)
                {
                    sb.Append("    ")
                        .Append(m[row * 3].ToString("F4", CultureInfo.InvariantCulture)).Append("  ")
                        .Append(m[row * 3 + 1].ToString("F4", CultureInfo.InvariantCulture)).Append("  ")
                        .AppendLine(m[row * 3 + 2].ToString("F4", CultureInfo.InvariantCulture));
                }
            }

            sb.Append("  デモザイク: ").AppendLine(choice.ApplyDemosaic ? "適用 (バイリニア)" : "なし");

            string sidecarPath = Path.ChangeExtension(imagePath, ".txt");
            File.WriteAllText(sidecarPath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception)
        {
            // 付随情報の保存失敗は本体の保存結果に影響させない
        }
    }

    // ---- 画像演算 (ダーク減算/フラット補正) ----

    private void OnImageCalculatorClick(object sender, RoutedEventArgs e)
    {
        if (_currentImage is null || _currentFormat is null || _currentPath is null)
        {
            return;
        }

        // 読み込み中・縮小表示の作成中・他の処理の実行中は始めない(ビニング・フィルタと同じ)。
        // HDR分割・合成の計算中に始めると、ダイアログ・進捗表示の中で派生ビューが表示され、その後で
        // 元画像だけを演算結果へ差し替えてしまう。黙って無視せず理由を知らせる
        if (RejectWhileBusy("画像演算"))
        {
            return;
        }

        using BusyScope busy = EnterBusy();
        if (_derivedImage is not null)
        {
            MessageBox.Show(this, "HDR表示中は画像演算できません。Raw表示に戻してから実行してください。",
                "画像演算", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 演算は1フレーム単位なので、判定もフレーム画素数で行う
        if ((long)_currentFormat.Width * _currentFormat.Height
            > RawLoader.DefaultInMemoryPixelThreshold)
        {
            MessageBox.Show(this, "1億画素を超える画像の演算はサポートされていません。",
                "画像演算", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 参照画像は1フレームだけ読むため、期待サイズも1フレーム分
        long expectedSize = _currentFormat.HeaderOffset + _currentFormat.FrameSizeInBytes;
        var dialog = new ImageCalculatorDialog(
            Path.GetFileName(_currentPath) + (_correctionLabel is null ? "" : $" [{_correctionLabel}]"),
            _currentFolder, expectedSize)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        ExecuteImageCalculation(dialog.Result);
    }

    private async void ExecuteImageCalculation(ImageCalculatorChoice choice)
    {
        using BusyScope busy = EnterBusy();
        RawImage source = _currentImage!;
        RawFormat format = _currentFormat!;
        int frame = Viewport.Frame;

        RawImage? corrected = null;
        RawImage? reference = null;
        ProgressWindow result = ProgressWindow.Run(
            this,
            $"画像演算中: {Path.GetFileName(choice.ReferencePath)}",
            (progress, ct) => Task.Run(() =>
            {
                // 読み込み段階からキャンセルを効かせる
                // (NAS等では参照の読み込みだけで数十秒かかることがある)
                reference = IsRawFile(choice.ReferencePath)
                    ? RawLoader.Load(choice.ReferencePath, format with { FrameCount = 1 }, ct)
                    : ImageFileLoader.Load(choice.ReferencePath, ct).Luminance;
                // 右パネルで変更したBayerは source.Format に入らないため、結果へ明示的に引き継ぐ
                corrected = ImageCalculator.Apply(
                    source, reference, choice.Operation, frame, 0, format.Bayer, progress, ct);
            }, ct));
        reference?.Dispose();

        if (result.Error is not null)
        {
            corrected?.Dispose();
            MessageBox.Show(this, $"画像演算に失敗しました: {result.Error.Message}", "画像演算",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (result.WasCanceled || corrected is null)
        {
            corrected?.Dispose();
            return;
        }

        string opLabel = choice.Operation switch
        {
            ImageOperation.Subtract => "−",
            ImageOperation.AbsoluteDifference => "|−|",
            _ => "÷",
        };
        await ApplyProcessedImageAsync(
            source, corrected, $"{opLabel} {Path.GetFileName(choice.ReferencePath)}");
    }

    /// <summary>
    /// 加工済み画像を現在の画像として差し替える(以後の解析・現像・保存すべてに反映)。
    /// </summary>
    /// <remarks>
    /// 欠陥検出の結果は処理前の画像のものなので、欠陥補正の結果を含めて破棄する
    /// (欠陥ウィンドウは閉じずに「未実行」へ戻す。他の差し替え経路と同じ規約)。
    /// 処理を始めた後で前提が崩れていたら(HDR分割・合成の派生ビューを表示中、元画像が処理の元では
    /// なくなった)、結果を破棄して理由を知らせ、差し替えない(ProcessedImageReplacement)。
    /// </remarks>
    /// <param name="source">処理の元にした画像(処理の開始時の元画像)。</param>
    /// <param name="processed">差し替える画像。差し替えなかったときは破棄する。</param>
    /// <param name="label">タイトル等に表示する処理ラベル。</param>
    /// <param name="color">RGBの処理結果。processedは同じ画像の輝度であること。</param>
    /// <param name="defectNotice">
    /// 欠陥ウィンドウで一覧を破棄したときに出す案内。省略時は画像が替わったことを示す。
    /// 差し替えと同じUIターンで出す(縮小表示の作成を待った後で出すと、その間に検出し直した一覧を消す)。
    /// </param>
    /// <returns>差し替えた場合はtrue。</returns>
    private async Task<bool> ApplyProcessedImageAsync(
        RawImage source, RawImage processed, string label, ColorImage? color = null,
        string? defectNotice = null)
    {
        // 処理は実行中の操作・HDR表示の間は始めないが、前提が崩れていたら表示に触れる前に断る
        if (RejectProcessedImage(source, processed, label))
        {
            return false;
        }

        // 旧画像を読んでいる解析タスクを止めてから破棄する。描画の停止を待つ間も操作を受け付けるので、
        // 差し替えるまでは保存などを始めさせない(そのダイアログの中で対象の画像を差し替え・破棄してしまう)
        CancelAnalysis();
        using (_imageGate.BeginReplacement())
        {
            await Viewport.ClearImageAsync();
        }

        if (RejectProcessedImage(source, processed, label))
        {
            return false;
        }

        // 差し替えを確定したこのUIターンで、旧画像の縮小ピラミッド・Bayerピラミッドの生成を打ち切る
        // (ファイル連番・TIFFのページ送りと同じく、表示中の画像の世代を進める)。生成は _loadCts の
        // トークンで走り、結果は差し替え後の画像と照合して捨てられるだけなので、止めないと旧画像を
        // 破棄するまで、また破棄後も元画像を読まない縮小段が CPU とメモリを使い続ける。処理結果の
        // ピラミッドは新しいトークンで作る。上で断ったとき(差し替えないとき)は取り消さない。
        // 通常の読み込みが確定待ち(処理の完了を待つ間に開かれた)のときは進めない。_loadCts はその
        // 読み込みの中止にも使われ(読み込みはフォーマットの確定後に世代を進めて旧画像の生成を取り消し、
        // 処理の完了を待って画像を差し替える)、進めると開く要求を黙って捨ててしまう
        if (!_imageGate.IsLoadPending)
        {
            ReplaceLoadCts(new CancellationTokenSource());
        }

        // 旧画像から作られたBayerピラミッドを残すと、EnsureBayerPyramidAsyncの
        // 早期returnで補正後画像のピラミッドが作られず、縮小カラー表示が
        // 補正前の画素を出し続ける
        _mainBayerPyramid?.Dispose();
        _mainBayerPyramid = null;
        _derivedBayerPyramid?.Dispose();
        _derivedBayerPyramid = null;
        _currentImage?.Dispose();
        _currentImage = processed;
        ClearDefectSource();
        _currentFormat = processed.Format;
        _colorImage = color;
        _vm.IsColorImage = color is not null;
        _valueNote = null;
        _histogram = null;
        _channelHistograms = null;
        _vm.HasRoi = false;
        ClearCursorReadout();
        // 演算結果の16bit化や寸法変更に合わせてUIを更新する。
        // 表示LUTの内部値は維持し、コード値だけ新しいビット深度へ換算する。
        _updatingSliders = true;
        _vm.BlackLevelMax = (1 << processed.Format.BitDepth) - 1;
        _vm.BlackLevel = _blackPoint >> CurrentShift;
        _vm.WhiteLevel = _whitePoint >> CurrentShift;
        _updatingSliders = false;
        UpdateFormatPanel(processed.Format);
        Viewport.SetDefectMarkers(null);
        _defectWindow?.DiscardResult(defectNotice);

        _correctionLabel = _correctionLabel is null ? label : $"{_correctionLabel}, {label}";
        UpdateNoiseWindowSource();
        UpdateProcessingBadge();
        Title = $"RawAnalyzer — {Path.GetFileName(_currentPath!)} [{_correctionLabel}]";
        _vm.ImageInfoText =
            $"{processed.Width}×{processed.Height} · {processed.Format.BitDepth}bit" +
            (color is null ? " · " : " · RGB · ") +
            $"補正: {_correctionLabel}(再読込で元に戻せます)";

        Viewport.SetImage(processed, processed.Format);
        Viewport.SetColorImage(color);

        // 表示モードは処理前の選択を保つ(送りと同じ規約)。カラーの処理結果は RGB のまま表示する
        // (Raw 表示へ戻すとカラー画像のビニング・フィルタの結果がグレーで表示される)
        DisplayModeSelection.Choice display =
            ApplyDisplayModeToNewImage(color is not null, processed.Format.Bayer);
        Viewport.SetLut(BuildLut());

        // 加工結果はディスク上のファイルと一致しないためシーケンス再生は無効化
        StopPlayback();
        _sequenceMode = SequenceMode.None;
        UpdateSequenceUi();

        RefreshHistogram(roi: null);

        // 開いているラインプロファイル窓は閉じずに、ヒストグラムと同じく処理結果で計算し直す
        // (同じ基準点・方向。ビニングで縮んで範囲外になったら、範囲外であることを示す)
        RefreshLineProfile();
        _mainPyramid = null;
        await BuildPyramidAsync(processed, _loadCts?.Token ?? CancellationToken.None);
        if (display.ComboIndex != 0 && ReferenceEquals(processed, _currentImage))
        {
            await EnsureBayerPyramidAsync();
        }

        return true;
    }

    /// <summary>
    /// 処理結果で差し替える前提が崩れていたら、結果を破棄して理由を知らせる。
    /// </summary>
    /// <param name="source">処理の元にした画像。</param>
    /// <param name="processed">差し替える予定だった画像。断ったときは破棄する。</param>
    /// <param name="label">処理ラベル。</param>
    /// <returns>断った場合はtrue。</returns>
    private bool RejectProcessedImage(RawImage source, RawImage processed, string label)
    {
        ProcessedImageReplacement.Refusal refusal = ProcessedImageReplacement.Check(
            source, _currentImage, derivedViewShown: _derivedImage is not null);
        if (refusal == ProcessedImageReplacement.Refusal.None)
        {
            return false;
        }

        processed.Dispose();
        MessageBox.Show(this, ProcessedImageReplacement.Explain(refusal, label),
            "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    // ---- バッチ現像 / 動画書き出し ----

    private void OnBatchExportClick(object sender, RoutedEventArgs e)
    {
        if (_currentImage is null || _currentFormat is null || _currentPath is null)
        {
            return;
        }

        // 補正・HDR派生を適用した画像はディスク上のファイルと一致しないため、
        // ファイル単位で読み直すバッチの対象にしない
        if (_derivedImage is not null || _correctionLabel is not null)
        {
            MessageBox.Show(this,
                "補正・HDR表示を適用中はバッチ書き出しできません。元のファイルを開き直してください。",
                "バッチ書き出し", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (RejectWhileImageReplacing("バッチ書き出し"))
        {
            return;
        }

        using BusyScope busy = EnterBusy();
        bool rawTargets = IsRawFile(_currentPath);
        RawFormat format = _currentFormat;
        IReadOnlyList<string> targets;
        if (rawTargets)
        {
            long size = SafeFileSize(_currentPath);
            if (size <= 0)
            {
                MessageBox.Show(this, "対象ファイルのサイズを取得できませんでした。",
                    "バッチ書き出し", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            targets = SequenceScanner.FindStack(_currentPath, size, CandidateFiles());
        }
        else if (_tiffStack is { PageNavigationEnabled: true })
        {
            targets = new[] { _currentPath };
        }
        else
        {
            // TIFF等は連番の命名で対象を決める(再生パネルと同じ規則)。
            // 連番でなければ開いているファイル1件だけを対象にする
            targets = SequenceScanner.FindNumberedStack(_currentPath, CandidateFiles());
            if (targets.Count == 0)
            {
                targets = new[] { _currentPath };
            }
        }

        if (targets.Count == 0)
        {
            return;
        }

        string folder = Path.GetDirectoryName(_currentPath)!;
        var dialog = new BatchExportDialog(
            targets.Count, Path.Combine(folder, "export"), rawTargets,
            _currentFormat?.Width ?? 0, _currentFormat?.Height ?? 0,
            _tiffStack is { PageNavigationEnabled: true } stack ? stack.PageCount : 1)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        BatchChoice choice = dialog.Result;
        // バッチも1フレーム単位で現像するため、判定はフレーム画素数で行う。開始前は表示中の画像で判定し、
        // 寸法の異なるページ・連番のファイルは書き出しの実行中に1枚ごとに判定して中止する(BatchFrameRenderer)
        if (choice.Format != BatchFormat.Tiff16
            && (long)format.Width * format.Height > RawLoader.DefaultInMemoryPixelThreshold)
        {
            MessageBox.Show(this, "1億画素を超える画像の現像バッチはサポートされていません(TIFF16は可)。",
                "バッチ書き出し", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExecuteBatch(targets, format, choice);
    }

    private void ExecuteBatch(
        IReadOnlyList<string> targets, RawFormat format, BatchChoice choice)
    {
        // 「表示調整を焼き込む」オフなら黒/白点・ゲイン・ガンマ・コントラストを
        // ニュートラルにする。WB・マトリクス・デモザイクの現像段は残す
        // (どう写っているかの色は保ちつつ、見え方の調整だけ外す)
        DisplayLut lut = choice.ApplyDisplayLut
            ? BuildLut()
            : DisplayLut.Create(new DisplayParameters());
        DevelopParameters developParameters = CurrentDevelopParameters();
        if (!choice.ApplyDisplayLut)
        {
            developParameters = developParameters with
            {
                BlackLevel = 0,
                WhitePoint = 65535,
                Gain = 1.0,
                Gamma = 1.0,
                Contrast = 1.0,
            };
        }

        // 現像LUTはページ・ファイルごとのビット深度で作る(白飛びの判定がビット深度で決まる)
        var renderer = new BatchFrameRenderer(format.Bayer, developParameters, lut);
        int width = format.Width;
        int height = format.Height;
        bool video = choice.Format is BatchFormat.AviMjpeg or BatchFormat.Mp4H264;
        string videoPath = Path.Combine(
            choice.OutputFolder,
            Path.GetFileNameWithoutExtension(targets[0])
                + (choice.Format == BatchFormat.Mp4H264 ? "_seq.mp4" : "_seq.avi"));

        // 動画は一時ファイルへ書き切ってから置換する。最終パスへ直接書くと、
        // 失敗・キャンセル時に上書き対象だった既存の動画を失う
        string videoTempPath = OutputPaths.BuildPartialPath(videoPath);

        ProgressWindow result = ProgressWindow.Run(
            this,
            $"バッチ書き出し中 ({targets.Count}件)",
            (progress, ct) => Task.Run(() =>
            {
                Directory.CreateDirectory(choice.OutputFolder);
                int jpegQuality = VideoQualitySettings.JpegQuality(choice.Quality);
                AviMjpegWriter? avi = null;
                Mp4H264Writer? mp4 = null;
                try
                {
                    if (choice.Format == BatchFormat.AviMjpeg)
                    {
                        avi = new AviMjpegWriter(videoTempPath, width, height, choice.Fps);
                    }
                    else if (choice.Format == BatchFormat.Mp4H264)
                    {
                        mp4 = new Mp4H264Writer(
                            videoTempPath, width, height, choice.Fps,
                            VideoQualitySettings.EncoderQuality(choice.Quality),
                            VideoQualitySettings.BitsPerPixel(choice.Quality));
                    }

                    for (int i = 0; i < targets.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string file = targets[i];
                        string baseName = Path.GetFileNameWithoutExtension(file);
                        // RAWの全フレーム／TIFFの全ページを1枚ずつ処理する。
                        // 列挙子が画像を所有するので、中断・例外でも確実に解放される。
                        foreach (FileFrame entry in FileFrameReader.Read(file, format, ct))
                        {
                            RawImage image = entry.Image;

                            // PNG/JPEG・動画は1枚ごとに1億画素を超えないか確かめ、超えたらどのファイルの何枚目かと
                            // TIFF16 なら書き出せることを示して中止する(焼き込みでも確かめるが、動画では寸法違いの
                            // 理由より先に示す)。TIFF16 は従来どおり上限なし
                            if (choice.Format != BatchFormat.Tiff16)
                            {
                                renderer.EnsureWithinPixelLimit(entry);
                            }

                            if (video && (image.Width != width || image.Height != height))
                            {
                                throw new NotSupportedException(
                                    $"{Path.GetFileName(file)} の{entry.Index + 1}枚目のサイズ " +
                                    $"({image.Width}×{image.Height}) が動画のサイズ " +
                                    $"({width}×{height}) と異なるため動画にできません。");
                            }

                            if (video)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (avi is not null)
                                {
                                    avi.AddFrame(renderer.EncodeJpeg(entry, jpegQuality, ct));
                                }
                                else
                                {
                                    mp4!.AddFrameRgb24(renderer.RenderRgb24(entry, ct));
                                }
                            }
                            else
                            {
                                ct.ThrowIfCancellationRequested();
                                string stem = entry.IsTiffPage ? $"{baseName}_p{entry.Index + 1:D4}"
                                    : entry.Count > 1 ? $"{baseName}_f{entry.Index:D3}"
                                    : baseName;
                                if (choice.Format == BatchFormat.Tiff16)
                                {
                                    TiffWriter.SaveGray16(
                                        image, entry.Frame,
                                        Path.Combine(choice.OutputFolder, stem + ".tif"),
                                        null, ct);
                                }
                                else
                                {
                                    bool jpeg = choice.Format == BatchFormat.Jpeg8;
                                    renderer.Save(
                                        entry,
                                        Path.Combine(choice.OutputFolder,
                                            stem + (jpeg ? ".jpg" : ".png")),
                                        jpeg, ct);
                                }
                            }

                            progress.Report((i + ((entry.Index + 1) / (double)entry.Count)) / targets.Count);
                        }

                        progress.Report((double)(i + 1) / targets.Count);
                    }

                    avi?.Finish();
                    mp4?.Finish();
                }
                finally
                {
                    avi?.Dispose();
                    mp4?.Dispose();
                }

                if (video)
                {
                    // 書き切れたときだけ最終パスへ置き換える
                    File.Move(videoTempPath, videoPath, overwrite: true);
                }
            }, ct));

        // 中断・エラー時は一時ファイルだけを片付ける。最終パスは書き切るまで
        // 触っていないので、同名の既存動画はそのまま残る
        if (video && File.Exists(videoTempPath))
        {
            AtomicFileWriter.TryDelete(videoTempPath);
        }

        if (result.Error is not null)
        {
            MessageBox.Show(this, $"バッチ書き出しに失敗しました: {result.Error.Message}",
                "バッチ書き出し", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else if (!result.WasCanceled)
        {
            _vm.ImageInfoText = video
                ? $"動画書き出し完了: {Path.GetFileName(videoPath)}"
                : $"バッチ書き出し完了: {targets.Count}件 → {choice.OutputFolder}";
        }
    }

    // ---- 表示モード・ホワイトバランス ----

    private async void OnDisplayModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Viewport is null || _currentFormat is null || _currentImage is null)
        {
            return;
        }

        int index = DisplayModeCombo.SelectedIndex;
        if (index is 4 or 5)
        {
            // HDR 方式は raw の読み込みダイアログ(フォーマット変更…)でしか指定できない。
            // カラー画像・画像ファイルでは、そこから設定するよう案内せず、実際に取れる手段を示す
            DisplayModeSelection.Refusal hdrRefusal = DisplayModeSelection.ForHdrMode(
                _currentFormat.Hdr, _colorImage is not null,
                isRawFile: _currentPath is not null && IsRawFile(_currentPath));
            if (hdrRefusal != DisplayModeSelection.Refusal.None)
            {
                MessageBox.Show(this, DisplayModeSelection.Explain(hdrRefusal),
                    "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
                DisplayModeCombo.SelectedIndex = 0;
                return;
            }

            if (index == 4)
            {
                await EnterHdrSplitAsync();
            }
            else
            {
                await EnterHdrMergeAsync();
            }

            return;
        }

        // HDR合成ビュー中: Bayer/現像/分割は合成結果に適用する(Raw表示で元画像へ復帰)
        if (_hdrFloatImage is not null && index is 1 or 2 or 3)
        {
            if (ActiveFormat?.Bayer is null or BayerPattern.None)
            {
                MessageBox.Show(this,
                    "HDR合成結果にBayerパターンがありません。\n" +
                    "元rawのフォーマットでBayerパターンを指定してからHDR合成してください。",
                    "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
                DisplayModeCombo.SelectedIndex = 5;
                return;
            }

            ViewportDisplayMode hdrMode = index switch
            {
                1 => ViewportDisplayMode.BayerColor,
                2 => ViewportDisplayMode.ColorDevelop,
                _ => ViewportDisplayMode.ChannelSplit,
            };
            if (hdrMode == ViewportDisplayMode.ColorDevelop)
            {
                EnsureDevelopLuts();
            }

            Viewport.SetDisplayMode(hdrMode);
            await EnsureBayerPyramidAsync();
            return;
        }

        // 通常モード: HDR派生ビューから復帰
        if (_derivedImage is not null)
        {
            await RestoreMainImageAsync();
        }

        // カラー画像の Raw 表示は RGB のままの表示で、Bayer 系の表示は使わない(開いたとき・送りと同じ)。
        // メニューなどから Bayer 系を選んで断ったあとの戻り先も、グレーの Raw 表示ではなくカラー表示にする
        DisplayModeSelection.Selected selected = DisplayModeSelection.ForSelectedMode(
            index, _colorImage is not null, _currentFormat.Bayer);
        if (selected.Refusal != DisplayModeSelection.Refusal.None)
        {
            MessageBox.Show(this, DisplayModeSelection.Explain(selected.Refusal),
                "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        ViewportDisplayMode mode = selected.ViewportMode;
        if (mode == ViewportDisplayMode.ColorDevelop)
        {
            EnsureDevelopLuts();
        }

        Viewport.SetDisplayMode(mode);
        if (mode is ViewportDisplayMode.BayerColor or ViewportDisplayMode.ColorDevelop
            or ViewportDisplayMode.ChannelSplit)
        {
            await EnsureBayerPyramidAsync();
        }
    }

    /// <summary>
    /// 表示する画像を差し替えた直後に、表示モードの選択とビューポートの表示モードを同じ結果にそろえる。
    /// </summary>
    /// <remarks>
    /// ファイル連番の送り・TIFF のページ送り・処理結果の差し替えで共通(DisplayModeSelection の規約)。
    /// 選択中のモードは新しい画像でも成立すれば保ち、成立しないモードだけ選択ごと戻す。
    /// カラー画像は RGB のまま表示し、選択は Raw 表示・操作不可にする。
    /// 選択の変更は OnDisplayModeChanged を通るので、MainWindow の状態(画像・フォーマット・カラー画像)を
    /// 新しい画像へ交換してから呼ぶ。分割⇔非分割の切替で ROI を捨てるのはビューポートが行う。
    /// </remarks>
    /// <param name="isColor">新しい画像がデコード済みのカラー画像か。</param>
    /// <param name="bayer">新しい画像のBayerパターン。</param>
    /// <returns>適用した表示モード。</returns>
    private DisplayModeSelection.Choice ApplyDisplayModeToNewImage(bool isColor, BayerPattern bayer)
    {
        DisplayModeSelection.Choice display = DisplayModeSelection.ForSequenceImage(
            DisplayModeCombo.SelectedIndex, isColor, bayer);
        DisplayModeCombo.IsEnabled = display.ComboEnabled;

        // 再生中の送りでは多くの場合どちらも変わらないので、変わるときだけ設定する
        if (DisplayModeCombo.SelectedIndex != display.ComboIndex)
        {
            DisplayModeCombo.SelectedIndex = display.ComboIndex;
        }

        // 現像LUTは白飛びの判定に表示中の画像のビット深度を使う。カラー現像で表示するなら、このUIターンで
        // 照合して作り直す(間引きのタイマーを待つと、その間はビット深度の違う旧画像のLUTで新しい画像を描く)
        if (display.ViewportMode == ViewportDisplayMode.ColorDevelop)
        {
            EnsureDevelopLuts();
        }

        if (Viewport.DisplayMode != display.ViewportMode)
        {
            Viewport.SetDisplayMode(display.ViewportMode);
        }

        return display;
    }

    /// <summary>
    /// HDR派生ビューの計算結果を適用してよいか判定する。
    /// </summary>
    /// <remarks>
    /// 分割・合成は数秒かかり、その間も表示モードは操作できる。元画像の差し替えだけでなく
    /// モード変更も見ないと、ユーザーが選び直した表示を計算結果が後から上書きしてしまう。
    /// 計算を始めたときのフォーマット(Bayer など派生ビューに効く値)とも照合し、替わっていたら結果を捨てて
    /// 理由を知らせ、表示モードを Raw 表示へ戻す(計算中は Bayer を選べなくしているので、多重防御。
    /// 判定は HdrViewReplacement)。
    /// </remarks>
    /// <param name="expectedModeIndex">開始時の表示モード(分割=4、合成=5)。</param>
    /// <param name="source">計算元の画像。</param>
    /// <param name="sourceFrame">計算元のフレーム(<see cref="CaptureHdrSourceFrame"/> の戻り値)。</param>
    /// <param name="sourceFormat">計算に使ったフォーマット(計算を始めたときの元画像のフォーマット)。</param>
    /// <returns>適用してよければtrue。</returns>
    private bool CanApplyHdrView(int expectedModeIndex, RawImage source, int sourceFrame, RawFormat sourceFormat)
    {
        HdrViewReplacement.Refusal refusal = HdrViewReplacement.Check(
            modeReselected: DisplayModeCombo.SelectedIndex != expectedModeIndex,
            sourceReplaced: !IsHdrSourceCurrent(source, sourceFrame),
            sourceFormat, _currentFormat);
        if (refusal == HdrViewReplacement.Refusal.FormatChanged)
        {
            // 表示モードの選択は分割・合成のまま、表示は計算前のまま残っている。そろえて Raw 表示へ戻す
            string operation = expectedModeIndex == 4 ? "HDR分割" : "HDR合成";
            MessageBox.Show(this, HdrViewReplacement.Explain(refusal, operation),
                operation, MessageBoxButton.OK, MessageBoxImage.Information);
            DisplayModeCombo.SelectedIndex = 0;
        }

        return refusal == HdrViewReplacement.Refusal.None;
    }

    /// <summary>
    /// HDR派生ビューの計算元(元画像とフレーム)が、いまも表示中の元画像・フレームか判定する。
    /// </summary>
    /// <param name="source">計算元の画像。</param>
    /// <param name="sourceFrame">計算元のフレーム(<see cref="CaptureHdrSourceFrame"/> の戻り値)。</param>
    /// <returns>計算を始めたときと同じならtrue。</returns>
    private bool IsHdrSourceCurrent(RawImage source, int sourceFrame)
    {
        return ReferenceEquals(source, _currentImage)
            && _hdrSourceFrame.IsCurrent(sourceFrame, Viewport.Frame, derivedViewShown: _derivedImage is not null);
    }

    // HDR派生ビュー(分割・合成)の元にした元画像のフレーム。
    // 派生ビューの計算とRaw表示への復帰(表示し直すフレーム)で参照する
    private readonly HdrSourceFrame _hdrSourceFrame = new();

    // HDR合成ビューの表示中に、合成ビューへ入る前(元画像・分割ビュー)の黒点・白点を控える。
    // 合成画像は黒レベル減算済みで合成域のフルスケールを65535へ量子化しているので、合成ビューの表示黒点は0、
    // 表示白点は65535から始める
    private readonly MergedViewLevels _mergedViewLevels = new();

    // HDR分割・合成の計算中(開始から、派生ビューへ差し替えるか採用せずに終えるまで)の数。
    // 計算は始めたときのフォーマットで行うので、計算中は右パネルの Bayer の選択を無効にする
    private int _hdrComputations;

    /// <summary>
    /// HDR分割・合成の計算中に入る。戻り値を using で受けること。抜けると(採用・取り消し・失敗・元画像の
    /// 差し替えのどれで終えても)計算中でなくなる。
    /// </summary>
    /// <returns>計算中を抜けるスコープ。</returns>
    private HdrComputationScope BeginHdrComputation()
    {
        _hdrComputations++;
        _vm.IsHdrComputing = true;
        return new HdrComputationScope(this);
    }

    /// <summary>HDR分割・合成の計算中のスコープ。Disposeで抜ける。</summary>
    private readonly struct HdrComputationScope : IDisposable
    {
        private readonly MainWindow _owner;

        internal HdrComputationScope(MainWindow owner)
        {
            _owner = owner;
        }

        /// <summary>スコープを抜ける。</summary>
        public void Dispose()
        {
            _owner._hdrComputations--;
            _owner._vm.IsHdrComputing = _owner._hdrComputations > 0;
        }
    }

    /// <summary>
    /// HDR分割・合成の元にする元画像のフレーム番号を決めて控える。
    /// </summary>
    /// <remarks>
    /// 行交互HDRは1フレームが全露光を含む1回の撮影なので、表示中のフレームを分割する。
    /// 派生ビューの表示中は Viewport.Frame が派生画像(常に0)を指すため、
    /// 分割⇔合成の切替では派生ビューの元にしたフレームを引き継ぐ。
    /// </remarks>
    /// <returns>元画像のフレーム番号。</returns>
    private int CaptureHdrSourceFrame()
    {
        return _hdrSourceFrame.Capture(Viewport.Frame, derivedViewShown: _derivedImage is not null);
    }

    private async Task EnterHdrSplitAsync()
    {
        // 読み込み中・縮小表示の作成中・他の処理(HDR分割・合成の計算を含む)の実行中は始めない。
        // ビニング・フィルタ・欠陥検出/補正・画像演算と同じく理由を示し、表示モードの選択も戻す
        if (RejectWhileBusy("HDR分割表示"))
        {
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        // 再生タイマーの連番送りと競合すると、Split中の画像が背後で破棄される
        using BusyScope busy = EnterBusy();

        // 計算は待つ間も操作を受け付け、完了すると表示画像を派生ビューへ差し替える。その間は保存・バッチ書き出し・
        // ノイズ測定を始めさせない(始めると、そのダイアログ・進捗表示の中で対象の画像が入れ替わる・破棄される)
        using IDisposable replacing = _imageGate.BeginReplacement();

        // 計算は下で決めるフォーマットで行うので、計算中は右パネルの Bayer を選べなくする。採用されずに終えたら
        // 戻す(派生ビューへ差し替えたら、派生ビューの表示中として無効のまま)
        using HdrComputationScope computing = BeginHdrComputation();
        RawImage image = _currentImage!;
        int sourceFrame = CaptureHdrSourceFrame();

        // フォーマットパネルで変更したBayerパターンやHDR方式を反映する
        // (image.Format は読み込み時のまま固定なので _currentFormat を渡す)。適用時にもこのフォーマットのままか照合する
        RawFormat splitFormat = _currentFormat!;
        IReadOnlyList<RawImage> frames;
        try
        {
            frames = await Task.Run(() => HdrSplitter.Split(image, splitFormat, sourceFrame));
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            // 別ファイルへの切替と競合して元画像が破棄された。結果は不要
            // (InvalidOperationExceptionの派生なので先に受ける)
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "HDR分割", MessageBoxButton.OK, MessageBoxImage.Warning);
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        if (!CanApplyHdrView(4, image, sourceFrame, splitFormat))
        {
            foreach (RawImage frame in frames)
            {
                frame.Dispose();
            }

            return;
        }

        // 長秒/短秒を左右並置した合成画像を作る
        int stages = frames.Count;
        int subWidth = frames[0].Width;
        int subHeight = frames[0].Height;
        int compositeWidth = subWidth * stages;
        RawImage composite = await Task.Run(() =>
        {
            var pixels = new ushort[(long)compositeWidth * subHeight];
            Parallel.For(0, subHeight, y =>
            {
                for (int stage = 0; stage < stages; stage++)
                {
                    frames[stage].CopyRegion(0, 0, y, subWidth, 1,
                        pixels.AsSpan(y * compositeWidth + stage * subWidth, subWidth));
                }
            });
            RawFormat format = splitFormat with
            {
                Width = compositeWidth,
                Height = subHeight,
                FrameCount = 1,
                Hdr = HdrMode.None,

                // 負の行オフセットでは整列後の位相が元と変わる(分割フレーム側に合わせる)
                Bayer = frames[0].Format.Bayer,
            };
            return RawImage.FromPixels(format, pixels);
        });
        foreach (RawImage frame in frames)
        {
            frame.Dispose();
        }

        if (!CanApplyHdrView(4, image, sourceFrame, splitFormat))
        {
            composite.Dispose();
            return;
        }

        if (!await ApplyDerivedViewAsync(composite, image, sourceFrame, merged: false))
        {
            return;
        }

        _hdrSegmentWidth = subWidth;
        _hdrFrameParams = new DisplayParameters[stages];
        DisplayParameters current = CurrentDisplayParameters();
        for (int i = 0; i < stages; i++)
        {
            _hdrFrameParams[i] = current;
        }

        HdrTargetCombo.Items.Clear();
        HdrTargetCombo.Items.Add("全体");
        HdrTargetCombo.Items.Add("長秒");
        if (stages == 3)
        {
            HdrTargetCombo.Items.Add("中秒");
        }

        HdrTargetCombo.Items.Add("短秒");
        HdrTargetCombo.SelectedIndex = 0;
        _vm.HdrTargetVisible = true;
        ApplySplitLuts();
        _vm.LevelOverlayText = $"HDR分割表示 (左: 長秒 → 右: 短秒, {stages}段)";
    }

    private async Task EnterHdrMergeAsync()
    {
        // すでに合成ビュー表示中ならRaw表示へ戻すだけ(再計算しない)
        if (_hdrFloatImage is not null && _derivedImage is not null)
        {
            Viewport.SetDisplayMode(ViewportDisplayMode.Raw);
            return;
        }

        // 読み込み中・縮小表示の作成中・他の処理(HDR分割・合成の計算を含む)の実行中は始めない。
        // ビニング・フィルタ・欠陥検出/補正・画像演算と同じく理由を示し、表示モードの選択も戻す
        if (RejectWhileBusy("HDR合成表示"))
        {
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        // 再生タイマーの連番送りと競合すると、Split/Merge中の画像が背後で破棄される
        using BusyScope busy = EnterBusy();

        // 完了すると表示画像を派生ビューへ差し替える。その間は保存などを始めさせない(HDR分割と同じ)
        using IDisposable replacing = _imageGate.BeginReplacement();

        // 計算中は右パネルの Bayer を選べなくする(HDR分割と同じ。適用時にもこのフォーマットのままか照合する)
        using HdrComputationScope computing = BeginHdrComputation();
        RawImage image = _currentImage!;
        RawFormat format = _currentFormat!;
        int sourceFrame = CaptureHdrSourceFrame();

        // 元画像(分割ビューからの切替では分割フレーム。どちらも未減算)の黒点を減算して合成する。
        // 計算中に黒レベルを動かしても、合成に使う値はここで決める(UIの状態を計算のスレッドから読まない)
        ushort mergeBlackPoint = _blackPoint;
        HdrImage merged;
        RawImage quantized;
        try
        {
            (merged, quantized) = await Task.Run(() =>
            {
                IReadOnlyList<RawImage> frames = HdrSplitter.Split(image, format, sourceFrame);
                try
                {
                    HdrImage result = HdrMerger.Merge(frames, new HdrMergeParameters(
                        format.ExposureRatio, mergeBlackPoint));
                    return (result, result.ToRawImage16());
                }
                finally
                {
                    foreach (RawImage frame in frames)
                    {
                        frame.Dispose();
                    }
                }
            });
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            // 別ファイルへの切替と競合して元画像が破棄された。結果は不要
            // (InvalidOperationExceptionの派生なので先に受ける)
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "HDR合成", MessageBoxButton.OK, MessageBoxImage.Warning);
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        if (!CanApplyHdrView(5, image, sourceFrame, format))
        {
            quantized.Dispose();
            return;
        }

        if (!await ApplyDerivedViewAsync(quantized, image, sourceFrame, merged: true))
        {
            return;
        }

        _hdrFloatImage = merged;

        // 派生ビュー適用時点ではまだ合成結果を持っていないのでバッジを出し直す
        UpdateProcessingBadge();
        _vm.HdrTargetVisible = false;

        // 16bit量子化で情報が落ちる構成では、解析値がその精度で読まれることを明示する
        // (LostBitsは元素材のLSB基準。12bit・2段・露光比16などは無損失なので出ない)
        string lossNote = merged.LostBits >= 0.5
            ? $", 表示・解析は16bit量子化後 (1LSB={merged.QuantizationStep:F1}, " +
              $"元素材比 約{merged.LostBits:F0}bit損失 / 無損失はfloat raw保存)"
            : "";
        _vm.LevelOverlayText =
            $"HDR合成表示 (フルスケール {merged.FullScale:F0}, ゲイン=露出{lossNote})";
    }

    /// <summary>
    /// HDR分割・合成の計算結果(派生画像)を派生ビューとして表示する。
    /// </summary>
    /// <remarks>
    /// 呼び出し側は <see cref="CanApplyHdrView"/> で適用してよいか判定してから呼ぶ。旧派生画像を読んでいる
    /// 描画の停止を待つ間に元画像・元フレームが替わっていたら、結果を破棄して派生ビューへ差し替えない。
    /// </remarks>
    /// <param name="derived">派生画像。適用しなかったときは破棄する。</param>
    /// <param name="source">計算元の画像。</param>
    /// <param name="sourceFrame">計算元のフレーム(<see cref="CaptureHdrSourceFrame"/> の戻り値)。</param>
    /// <param name="merged">
    /// HDR合成の画像(黒レベル減算済み)か。falseはHDR分割の画像(各フレームは黒レベル未減算)。
    /// </param>
    /// <returns>派生ビューへ差し替えた場合はtrue。</returns>
    private async Task<bool> ApplyDerivedViewAsync(
        RawImage derived, RawImage source, int sourceFrame, bool merged)
    {
        StopPlayback();
        _vm.HasSequence = false;

        // 旧派生画像を読んでいる描画・解析・縮小ピラミッドの生成を止めてから破棄する
        CancelAnalysis();
        _derivedPyramidBuild.Cancel();
        if (_derivedImage is not null)
        {
            // 分割⇔合成の切替では旧派生画像用のBayerピラミッドの生成も取り消す(Raw表示から入るときは
            // 派生画像用の生成は走っていない。元画像の縮小ピラミッドの生成はRaw表示へ戻ったときに使うので残す)
            CancelDerivedBayerPyramidBuild();
        }

        await Viewport.ClearImageAsync();

        // 適用の直前に、計算元の元画像・フレームのままか確かめ直す。描画の停止を待つ間に替わっていたら
        // (差し替えた側が表示し直している)、結果は表示中の画像のものではないので捨てる。
        // 表示モードの選び直しはここでは見ない(元画像を表示し直さないので、断るとビューポートが空のまま残る)
        if (!IsHdrSourceCurrent(source, sourceFrame))
        {
            derived.Dispose();
            return false;
        }

        // 旧派生画像のBayerピラミッドを残すと、EnsureBayerPyramidAsyncが
        // 非nullを見て早期returnし、新しい派生画像のピラミッドが二度と作られない
        _derivedBayerPyramid?.Dispose();
        _derivedBayerPyramid = null;
        _derivedImage?.Dispose();
        _derivedImage = derived;

        // 派生ビューは計算を始めたときの Bayer で作った画像。表示中は右パネルの Bayer を選べなくする
        // (変えても派生ビューは古いパターンのままで、右パネルの表示と食い違う)。Raw表示へ戻ると選べる
        _vm.IsHdrViewShown = true;

        // 欠陥検出の結果は検出した画像(元画像、または前の派生ビュー)の座標・画素のもの。
        // 他の差し替え経路と同じく、派生ビューへ差し替えるこのUIターンで検出元を手放し、
        // マーカーと欠陥ウィンドウの一覧も破棄する(元画像座標のマーカーを派生ビューに重ねない)
        ClearDefectSource();
        Viewport.SetDefectMarkers(null);
        _defectWindow?.DiscardResult();
        _hdrFloatImage = null;
        _hdrFrameParams = null;
        _vm.HasRoi = false;

        // 派生ビューは元画像と座標の意味が違う(分割は段の並置)。カーソル位置の表示を消す
        ClearCursorReadout();

        // HDR合成の画像は黒レベル減算済みで、合成域のフルスケールを65535へ量子化している。元画像の黒点のまま
        // 表示すると黒を二重に引き、元画像で下げた白点のままだと合成で取り戻した高輝度を白飛びとして切るので、
        // 合成ビューへ入る前の黒点・白点を控えて表示黒点を0、表示白点を65535にする。合成 → 分割では控えた
        // 黒点・白点へ戻す(分割フレームは未減算で元画像と同じ値域)。合成ビューで動かした黒/白レベルは
        // 合成ビューの表示だけのもので、抜けるときに捨てる(ゲイン・ガンマ・コントラストは共有のまま)
        (_blackPoint, _whitePoint) = merged
            ? _mergedViewLevels.Enter(_blackPoint, _whitePoint)
            : _mergedViewLevels.Leave(_blackPoint, _whitePoint);

        // HDR合成は16bitになる。黒/白レベルの上限とコード値を、表示する画像のビット深度で表し直す
        SyncLevelControlsToActiveBitDepth();
        Viewport.SetDisplayMode(ViewportDisplayMode.Raw);
        Viewport.SetImage(derived, derived.Format);
        Viewport.SetLut(BuildLut());
        UpdateNoiseWindowSource();
        UpdateProcessingBadge();
        RefreshHistogram(roi: null);

        // 開いているラインプロファイル窓も、ヒストグラムと同じく派生ビューの画像で計算し直す
        // (同じ基準点・方向。派生ビューの寸法で範囲外なら範囲外であることを示す)
        RefreshLineProfile();
        _ = BuildDerivedPyramidAsync(derived);
        return true;
    }

    // HDR派生画像の縮小ピラミッドの生成。派生画像の差し替え・Raw表示への復帰で旧派生画像用の生成を取り消す
    private readonly DerivedPyramidBuild _derivedPyramidBuild = new();

    private async Task BuildDerivedPyramidAsync(RawImage derived)
    {
        // 取り消された・派生画像の破棄と競合した生成は null で終わる(投げっぱなしのタスクから例外を漏らさない)
        TilePyramid? pyramid = await _derivedPyramidBuild.CreateAsync(derived);
        if (pyramid is not null && ReferenceEquals(derived, _derivedImage))
        {
            Viewport.SetPyramid(pyramid);
        }
    }

    /// <summary>
    /// 派生ビューを抜ける・差し替えるときに、旧派生画像用のBayerピラミッドの生成を取り消す。
    /// </summary>
    /// <remarks>
    /// 派生ビューのBayer系表示(HDR合成のBayerカラー・カラー現像・チャネル分割)のBayerピラミッドは、
    /// EnsureBayerPyramidAsync が表示中の画像・フレームの世代(_loadCts)のトークンで作る。派生ビューの出入りでは
    /// 世代が進まないので、進めないとRaw表示へ戻っても分割⇔合成を切り替えても旧派生画像を読み続ける
    /// (結果は表示中の画像との照合で捨てられるだけ)。世代を進めると同じトークンで走る元画像の縮小ピラミッドの
    /// 生成も取り消されるので、派生画像用の生成が走り得る派生ビューの表示中だけ呼ぶ。
    /// 通常の読み込みが確定待ちのときは、開く要求(同じ世代のトークン)を取り消さないよう進めない
    /// (確定で派生画像ごと差し替わり、生成の結果は捨てられる)。
    /// </remarks>
    private void CancelDerivedBayerPyramidBuild()
    {
        if (!_imageGate.IsLoadPending)
        {
            ReplaceLoadCts(new CancellationTokenSource());
        }
    }

    private async Task RestoreMainImageAsync()
    {
        // 派生画像を読んでいる描画・解析・縮小ピラミッド(Bayerを含む)の生成を止めてから破棄する。
        // 描画の停止を待つ間も操作を受け付けるので、差し替えるまでは保存などを始めさせない
        CancelAnalysis();
        _derivedPyramidBuild.Cancel();
        CancelDerivedBayerPyramidBuild();
        using (_imageGate.BeginReplacement())
        {
            await Viewport.ClearImageAsync();
        }

        _derivedImage?.Dispose();
        _derivedImage = null;
        _vm.IsHdrViewShown = false;

        // 派生ビューで検出した結果は、ここで破棄する派生画像のもの。HDR表示に入るときに元画像の
        // 結果も破棄しているので、Raw表示へ戻したら検出し直す(他の差し替え経路と同じ規約)。
        // マーカーと欠陥ウィンドウの一覧も破棄し、派生ビュー座標のマーカーを元画像に重ねない
        ClearDefectSource();
        Viewport.SetDefectMarkers(null);
        _defectWindow?.DiscardResult();
        _hdrFloatImage = null;
        _hdrFrameParams = null;
        _vm.HdrTargetVisible = false;
        _vm.HasRoi = false;

        // 派生ビューの座標で持っていたカーソル位置の表示を消す(元画像とは座標の意味が違う)
        ClearCursorReadout();

        // 合成ビューから戻るなら、合成ビューへ入る前の元画像の黒点・白点へ戻す(合成画像は黒レベル減算済みで
        // 値域も元画像と別物。合成ビューで動かした黒/白レベルは合成ビューの表示だけのもの)。分割ビューからはそのまま
        (_blackPoint, _whitePoint) = _mergedViewLevels.Leave(_blackPoint, _whitePoint);

        // HDR合成(16bit)から元画像のビット深度へ戻す。黒/白レベルのコード値を元画像のビット深度で表し直す
        SyncLevelControlsToActiveBitDepth();
        _derivedBayerPyramid?.Dispose();
        _derivedBayerPyramid = null;

        // HDR表示の元にしたフレームを表示し直す(既定のフレーム0へ戻すと、分割・合成した
        // 撮影とは別のフレームになる)。シーケンスUIは下の DetectSequence が表示フレームに合わせる
        Viewport.SetImage(
            _currentImage!, _currentFormat!,
            _hdrSourceFrame.ResolveRestoreFrame(_currentImage!.FrameCount));

        // 生成元フレームを偽らずに渡す(別フレーム産はレンダラ側で使われない)
        Viewport.SetPyramid(_mainPyramid, _mainPyramidFrame);
        Viewport.SetBayerPyramid(_mainBayerPyramid, _mainBayerPyramidFrame);
        Viewport.SetLut(BuildLut());
        UpdateNoiseWindowSource();
        UpdateProcessingBadge();
        DetectSequence();
        RefreshHistogram(roi: null);

        // 派生ビューで出していたラインプロファイルも、元画像(HDR表示の元にしたフレーム)で計算し直す
        RefreshLineProfile();
    }

    private DisplayParameters CurrentDisplayParameters()
    {
        return new DisplayParameters(
            _blackPoint, _whitePoint, _vm.Gain, _vm.Gamma, _vm.Contrast);
    }

    private void ApplySplitLuts()
    {
        if (_hdrFrameParams is null)
        {
            return;
        }

        var luts = new DisplayLut[_hdrFrameParams.Length];
        for (int i = 0; i < luts.Length; i++)
        {
            luts[i] = DisplayLut.Create(_hdrFrameParams[i]);
        }

        Viewport.SetSplitLuts(luts, _hdrSegmentWidth);
    }

    private async void OnGrayWorldClick(object sender, RoutedEventArgs e)
    {
        if (ActiveImage is null || ActiveFormat is null
            || ActiveFormat.Bayer == BayerPattern.None)
        {
            return;
        }

        RawImage image = ActiveImage;
        BayerPattern pattern = ActiveFormat.Bayer;

        // フレーム0固定にすると、マルチフレームでフレームN表示中のAWBが
        // 見えていない画素から計算される
        int frame = Viewport.Frame;

        // 現像は黒減算後にWBを掛けるので、推定も現像と同じ黒レベルで行う
        ushort blackLevel = _blackPoint;
        WhiteBalanceGains gains;
        try
        {
            gains = await Task.Run(
                () => WhiteBalance.ComputeGrayWorld(image, frame, pattern, blackLevel));
        }
        catch (Exception)
        {
            return;
        }

        // 推定中に右パネルでBayerパターンを変えたら、ゲインは変える前のパターンで R・B として平均したもの
        // (RGGB→BGGR では R と B のゲインが入れ替わる)。表示中の画像の条件と違う推定は適用しない
        if (!ReferenceEquals(image, ActiveImage) || frame != Viewport.Frame
            || pattern != ActiveFormat?.Bayer || blackLevel != _blackPoint)
        {
            return;
        }

        _vm.WbGainG = 1.0;
        _vm.WbGainR = Math.Clamp(gains.GainR, 0.5, 4.0);
        _vm.WbGainB = Math.Clamp(gains.GainB, 0.5, 4.0);
    }

    private void OnWbPickToggleChanged(object sender, RoutedEventArgs e)
    {
        if (WbPickToggle.IsChecked == true)
        {
            RoiToggle.IsChecked = false;
            ProfileToggle.IsChecked = false;
            Viewport.InteractionMode = ViewportInteractionMode.WhiteBalancePick;
        }
        else if (Viewport.InteractionMode == ViewportInteractionMode.WhiteBalancePick)
        {
            Viewport.InteractionMode = ViewportInteractionMode.Pan;
        }
    }

    private void OnWhiteBalancePicked(object? sender, CursorPixelEventArgs e)
    {
        if (ActiveImage is null || ActiveFormat is null
            || ActiveFormat.Bayer == BayerPattern.None)
        {
            return;
        }

        // 表示フレーム・元画像座標(チャネル分割はタイル→元座標)で計算する
        if (!TryMapToSourceCoordinates(ActiveImage, e.X, e.Y, out int sourceX, out int sourceY))
        {
            return;
        }

        // 現像と同じ黒レベルで推定しないと、黒レベル設定時にスポイトした色が中性にならない
        WhiteBalanceGains gains = WhiteBalance.ComputeSpotGains(
            ActiveImage, Viewport.Frame, ActiveFormat.Bayer, sourceX, sourceY, _blackPoint);
        _vm.WbGainG = 1.0;
        _vm.WbGainR = Math.Clamp(gains.GainR, 0.5, 4.0);
        _vm.WbGainB = Math.Clamp(gains.GainB, 0.5, 4.0);
    }

    private DisplayLut BuildLut()
    {
        return DisplayLut.Create(new DisplayParameters(
            _blackPoint, _whitePoint, _vm.Gain, _vm.Gamma, _vm.Contrast));
    }

    /// <summary>raw code の黒/白レベルを16bitフルスケールの内部値へ反映する。</summary>
    private void ApplyLevelCodes(double blackCode, double whiteCode)
    {
        // 白点はそのcodeの上端まで含める(下位ビットを立てる)
        (_blackPoint, _whitePoint) = DisplayLevels.ToPoints(
            blackCode, whiteCode, ActiveFormat?.BitDepth ?? 16);
    }

    /// <summary>
    /// 黒/白レベルの上限とコード値を、表示中の画像のビット深度で表し直す。
    /// 表示LUTの内部値(黒点・白点)は変えない。
    /// </summary>
    /// <remarks>
    /// 表示中の画像のビット深度が変わる経路(HDR合成の16bit化・元画像への復帰・
    /// ビット深度の異なる連番)で呼ぶ。上限だけ更新すると、旧ビット深度のコード値が
    /// 次のスライダー操作で新しいシフト量のまま内部値へ戻され、白点が急落する。
    /// </remarks>
    private void SyncLevelControlsToActiveBitDepth()
    {
        int bitDepth = ActiveFormat?.BitDepth ?? 16;
        (int blackCode, int whiteCode) = DisplayLevels.ToCodes(_blackPoint, _whitePoint, bitDepth);
        _updatingSliders = true;
        _vm.BlackLevelMax = DisplayLevels.MaxCode(bitDepth);
        _vm.BlackLevel = blackCode;
        _vm.WhiteLevel = whiteCode;
        _updatingSliders = false;
    }

    private void ResetDisplayParameters()
    {
        _updatingSliders = true;
        _vm.Gain = 1.0;
        _vm.Gamma = 1.0;
        _vm.Contrast = 1.0;
        _vm.BlackLevel = 0;
        _vm.WhiteLevel = _vm.BlackLevelMax;
        _updatingSliders = false;
        _blackPoint = 0;
        _whitePoint = 65535;
    }

    private void OnResetDisplayClick(object sender, RoutedEventArgs e)
    {
        ResetDisplayParameters();
        ApplyDisplayParametersToViews();
    }

    private void OnAutoContrastClick(object sender, RoutedEventArgs e)
    {
        if (_histogram is null || _currentFormat is null)
        {
            return;
        }

        if (HistogramTools.ComputeAutoLevels(_histogram.Bins) is not { } levels)
        {
            return;
        }

        _updatingSliders = true;
        _vm.BlackLevel = levels.BlackCode;
        _vm.WhiteLevel = levels.WhiteCode;
        _updatingSliders = false;
        ApplyLevelCodes(levels.BlackCode, levels.WhiteCode);
        ApplyDisplayParametersToViews();
    }

    // ---- ビューポートイベント ----

    private void OnViewportStateChanged(object? sender, ViewportStateEventArgs e)
    {
        string percent = e.Zoom >= 0.1
            ? $"{e.Zoom * 100:F0}%"
            : $"{e.Zoom * 100:F2}%";
        int levelIndex = (int)Math.Round(Math.Log2(e.RenderedFactor));
        _vm.ZoomPercentText = percent;
        _vm.ZoomStatusText = $"Zoom {percent} · L{levelIndex}";
        _vm.LevelOverlayText = e.RenderedFactor > 1
            ? $"1/{e.RenderedFactor} 間引き表示 (ピラミッド L{levelIndex})"
            : "等倍データ表示 (L0)";
    }

    private void OnCursorPixelChanged(object? sender, CursorPixelEventArgs e)
    {
        RawImage? image = ActiveImage;
        RawFormat? format = ActiveFormat;
        if (image is null || format is null || !e.IsInsideImage)
        {
            _lastCursorInside = false;
            _vm.CursorStatusText = "";
            _vm.CursorOverlayText = "";
            return;
        }

        // チャネル分割表示ではタイル座標を元画像座標へ写像する
        if (!TryMapToSourceCoordinates(image, e.X, e.Y, out int sourceX, out int sourceY))
        {
            return;
        }

        if (CursorReadout.Compose(image, format, _colorImage, sourceX, sourceY, Viewport.Frame)
            is not { } text)
        {
            return;
        }

        _lastCursorX = sourceX;
        _lastCursorY = sourceY;
        _lastCursorInside = true;
        _vm.CursorStatusText = text.Status;
        _vm.CursorOverlayText = text.Overlay;
    }

    /// <summary>
    /// カーソル位置を保ったまま表示中の画像・フレーム・フォーマットが替わった後に、カーソル位置の画素値の表示を
    /// 表示中の画像・フレームから作り直す(読めなければ消す)。
    /// </summary>
    /// <remarks>
    /// 表示は <see cref="OnCursorPixelChanged"/>(マウス移動・キーボードの画素カーソル)でしか作られないので、
    /// フレーム・ページ・ファイルの送り、再生、右パネルの Bayer の変更の後に呼ばないと、前の画像・フレームの値と
    /// 前のパターンのチャネル名を出し続ける。座標の意味が変わる差し替え(HDR表示の出入り、別ファイルを開く、
    /// 寸法の変わる差し替え)では読み直さず <see cref="ClearCursorReadout"/> で消す。
    /// </remarks>
    private void RefreshCursorReadout()
    {
        if (_lastCursorInside && ActiveImage is { } image && ActiveFormat is { } format
            && CursorReadout.Compose(image, format, _colorImage, _lastCursorX, _lastCursorY, Viewport.Frame)
                is { } text)
        {
            _vm.CursorStatusText = text.Status;
            _vm.CursorOverlayText = text.Overlay;
            return;
        }

        ClearCursorReadout();
    }

    /// <summary>
    /// カーソル位置の画素値の表示を消し、カーソル位置を持たない状態にする。
    /// </summary>
    /// <remarks>
    /// 座標の意味が変わる差し替え(HDR表示の出入り、別ファイルを開く、寸法の変わる差し替え)で呼ぶ。位置を残すと、
    /// 前の画像の値を出し続けるうえ、Ctrl+C(カーソル位置の画素値・座標のコピー)が新しい画像の同じ数値座標、
    /// つまり別の画素を黙ってコピーする。次にマウス・画素カーソルを動かせば表示し直す。
    /// </remarks>
    private void ClearCursorReadout()
    {
        _lastCursorInside = false;
        _vm.CursorStatusText = "";
        _vm.CursorOverlayText = "";
    }

    // ---- ズーム操作 ----

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        Viewport.ZoomIn();
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        Viewport.ZoomOut();
    }

    private void OnActualSizeClick(object sender, RoutedEventArgs e)
    {
        Viewport.ActualSize();
    }

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        Viewport.FitToView();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ---- シーケンス再生 ----

    private int SequenceCount => _sequenceMode switch
    {
        SequenceMode.Frames => _currentImage?.FrameCount ?? 0,
        SequenceMode.Files => _sequenceFiles.Count,
        SequenceMode.TiffPages => _tiffStack?.PageCount ?? 0,
        _ => 0,
    };

    private void DetectSequence()
    {
        StopPlayback();
        _sequenceMode = SequenceMode.None;
        _sequenceFiles = new List<string>();
        _sequenceIndex = 0;

        if (_currentImage is not null && _derivedImage is null && _correctionLabel is null)
        {
            if (_tiffStack is { PageNavigationEnabled: true })
            {
                _sequenceMode = SequenceMode.TiffPages;
                _sequenceIndex = _tiffPageIndex;
            }
            else if (_currentImage.FrameCount > 1)
            {
                _sequenceMode = SequenceMode.Frames;
                _sequenceIndex = Viewport.Frame;
            }
            else if (_currentPath is not null)
            {
                // 同一フォルダのファイル群をバーチャルスタックとみなす。
                // rawはファイルサイズが解像度そのものを表すためサイズ一致で判定できるが、
                // 画像ファイルは解像度がヘッダにあるうえ圧縮でサイズが変わるので、
                // ファイル名の連番で判定する
                IReadOnlyList<string> files = IsRawFile(_currentPath)
                    ? SequenceScanner.FindStack(
                        _currentPath, CurrentFileLength(), CandidateFiles())
                    : SequenceScanner.FindNumberedStack(_currentPath, CandidateFiles());
                if (files.Count > 1)
                {
                    _sequenceMode = SequenceMode.Files;
                    _sequenceFiles = files.ToList();
                    _sequenceIndex = SequenceScanner.IndexOf(files, _currentPath);
                }
            }
        }

        UpdateSequenceUi();
    }

    /// <summary>ファイル一覧を仮想スタック判定の候補へ変換する(サイズは列挙時のキャッシュ)。</summary>
    private IEnumerable<SequenceFile> CandidateFiles()
    {
        return _vm.Files
            .Where(f => !f.IsDirectory)
            .Select(f => new SequenceFile(f.FullPath, f.Length));
    }

    /// <summary>現在開いているファイルのサイズ。一覧のキャッシュを優先する。</summary>
    private long CurrentFileLength()
    {
        if (_currentPath is null)
        {
            return -1;
        }

        FileEntry? entry = _vm.Files.FirstOrDefault(
            f => !f.IsDirectory
                && string.Equals(f.FullPath, _currentPath, StringComparison.OrdinalIgnoreCase));
        return entry is { Length: > 0 } ? entry.Length : SafeFileSize(_currentPath);
    }

    private static long SafeFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private void UpdateSequenceUi()
    {
        _updatingSequenceUi = true;
        int count = SequenceCount;

        // HDR分割・合成の派生ビューの表示中は送りのUIを有効にしない。送りの件数は派生ビューの間も残るので、
        // 派生ビューより前に始まった送りの後始末(ShowSequenceIndexAsync の finally)から呼ばれても有効に戻さない
        _vm.HasSequence = SequenceNavigation.IsAvailable(count, derivedViewShown: _derivedImage is not null);
        _vm.SequenceMax = Math.Max(0, count - 1);
        _vm.SequenceIndex = _sequenceIndex;
        _vm.SequenceLabel = count > 1 ? $"{_sequenceIndex + 1} / {count}"
            + (_sequenceMode == SequenceMode.TiffPages ? " (TIFF)" : "") : "";
        _updatingSequenceUi = false;
    }

    private async Task ShowSequenceIndexAsync(int index, bool refreshAnalysis)
    {
        if (_sequenceMode == SequenceMode.TiffPages)
        {
            await ShowTiffPageAsync(index, refreshAnalysis);
            return;
        }

        int count = SequenceCount;

        // HDR分割・合成の派生ビューの表示中は送らない(送りのUIも無効)。ファイル連番では派生ビューを残したまま
        // 元画像だけがビューポートへ入り、フレーム送りでは派生ビューのBayerピラミッドがビューポートから外れる
        bool available = SequenceNavigation.IsAvailable(count, derivedViewShown: _derivedImage is not null);

        // 重い処理の実行中は画像を差し替えない(処理対象が背後で破棄されるため)。
        // スライダーが先に動いてしまっているので、表示中のフレームへ戻す
        if (!available || _sequenceBusy || _busyDepth > 0)
        {
            // 実行中で送れないときは黙って戻さず、理由をステータスバーに出す
            // (前の送りの完了待ちは連続操作で普通に起こるので知らせない)
            if (available)
            {
                NotifySequenceBusy();
            }

            UpdateSequenceUi();
            return;
        }

        index = ((index % count) + count) % count;
        if (index == _sequenceIndex)
        {
            UpdateSequenceUi();
            return;
        }

        _sequenceBusy = true;
        try
        {
            if (_sequenceMode == SequenceMode.Frames)
            {
                // 旧フレーム用の縮小ピラミッド・Bayerピラミッドの生成を打ち切る(ファイル連番・TIFFのページ送りと
                // 同じく、表示中の画像・フレームの世代を進める)。生成は _loadCts のトークンで走り、完了時に
                // トークンの取り消しを見て結果を捨てる。フレームを移しても取り消さないと、連続して送ったときに
                // 前フレーム用の生成が後から終わって現フレームのピラミッドを上書きし(描画側はフレームの
                // 不一致で使わないので、次に送るまで等倍データから描く)、送りのたびの生成も積み上がって
                // CPU とメモリを使い続ける。送れなかったときは取り消さない
                ReplaceLoadCts(new CancellationTokenSource());

                // 旧フレームを読んでいる解析(ヒストグラム・ROI統計・ラインプロファイル・射影)も打ち切る
                // (ファイル連番・TIFFのページ送りと同じ)。フレーム送りでは画像がそのままなので、取り消さないと
                // 送った後に届いた前フレームの結果を、画像の照合だけで現フレームの測定値として表示していた
                // (結果の採用時にもフレームを照合する)。送った後の解析は RefreshAfterSequenceMove が始める
                CancelAnalysis();

                // Bayerピラミッドも生成元フレーム専用なので、フレームを移したら捨てる。切り離しは送りを決めた
                // このUIターンで、フレームを移すのと一緒に行う(先に切り離して描画の停止を待つと、その間に
                // 始まった操作に譲って送りをやめたとき、表示中のフレームのピラミッドだけを失って作り直されず、
                // Bayer系の表示が等倍データからの描画になる)。切り離す前に始まっていた描画は読んでいる
                // 可能性があるので、止まってから破棄する(描画中に破棄すると読み出しがODEになる)。
                // フィールドはawait前に切っておく(await中に別経路が同じインスタンスを掴むとDisposeが二重になる)
                BayerPyramid? oldBayer = _mainBayerPyramid;
                _mainBayerPyramid = null;
                Task bayerDetached = oldBayer is null
                    ? Task.CompletedTask
                    : Viewport.DetachBayerPyramidAsync();

                // 欠陥検出の結果は検出したフレームの画素のもの。フレームを移すこのUIターンで、
                // 他の差し替え経路と同じく検出元を手放し、マーカーと欠陥ウィンドウの一覧も破棄する
                // (前フレームの一覧を現フレームのものと誤解させない。残すと一覧からの移動は
                // 現フレームの同じ座標を指し、補正はフレームの不一致で断られる)。
                // 送れなかったときは一覧・マーカーをそろえて残すので、消すのは移すときだけにする
                ClearDefectSource();
                Viewport.SetDefectMarkers(null);
                _defectWindow?.DiscardResult();
                Viewport.SetFrame(index);
                _sequenceIndex = index;

                // カーソル位置の画素値も送った先のフレームから読み直す(再生中も毎フレーム)
                RefreshCursorReadout();

                if (oldBayer is not null)
                {
                    try
                    {
                        await bayerDetached;
                    }
                    finally
                    {
                        oldBayer.Dispose();
                    }
                }
            }
            else
            {
                string path = _sequenceFiles[index];
                bool isRaw = IsRawFile(path);
                RawFormat expectedFormat = _currentFormat!;

                // 送りは保存・演算などの操作や通常の読み込みに譲る。次のファイルを読む間に
                // それらが始まったら、差し替えの時点で終わっていても送らない(HDR分割の
                // 派生ビューや開き直した画像を上書きしない。TIFFのページ送りは読み込みの中止で同じことをする)
                int activity = _imageGate.ActivityStamp;

                // await中にモーダル(保存・測定・演算)が開いていたら差し替えない。
                // モーダルのディスパッチャポンプ内でここが再開すると、処理対象の
                // 画像を背後で破棄してしまう。HDR分割・合成の派生ビューの表示中も差し替えない
                // (派生ビューを残したまま元画像だけが入れ替わる。派生ビューはHDR表示の開始で
                // ActivityStamp が進むので通常はその判定で譲るが、確定の前提として確かめる)
                bool Yielded() => _busyDepth > 0 || activity != _imageGate.ActivityStamp
                    || !ReferenceEquals(expectedFormat, _currentFormat) || _derivedImage is not null;
                RawImage image;
                ColorImage? color = null;
                int pageCount = 1;

                // 値の対応関係の説明は送りが確定したときに状態へ入れる(送れなかったときに
                // 表示中の画像と食い違わせない)
                string? valueNote = null;

                // 読み込みは取り消せるようにする。読む間に別ファイルを開く・操作を始める・ウィンドウを閉じると
                // 結果は下で捨てるが、取り消さないと大きなファイルの読み込み(ネットワーク上の raw の一時コピー、
                // TIFF のデコード)が最後まで走り、新しい読み込みや処理と I/O・メモリを奪い合う
                var loadCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _loadCts?.Token ?? CancellationToken.None);
                _sequenceLoadCts = loadCts;
                try
                {
                    // raw は表示中のフォーマットで、TIFF等の連番はファイル自身のフォーマットで読む
                    DecodedImage decoded = await Task.Run(
                        () => SequenceFileLoad.Load(path, isRaw, expectedFormat, loadCts.Token), loadCts.Token);
                    image = decoded.Luminance;
                    color = decoded.Color;
                    pageCount = decoded.PageCount;
                    valueNote = decoded.ValueNote;
                }
                catch (OperationCanceledException)
                {
                    return; // 開く・操作の開始・終了で取り消した
                }
                catch (Exception ex)
                {
                    // 消えた・ロックされた・壊れたファイル。黙って戻ると、再生は毎ティック同じファイルを読み直して
                    // 捨て続け(前のファイルで止まって見える)、「次」も理由なく効かない。再生を止めて理由を知らせる
                    // (送り先は飛ばさない。スライダー・End などで越えられる)。読む間に始まった操作などに譲るときは知らせない
                    if (!Yielded())
                    {
                        NotifySequenceMoveFailed(SequenceFileLoad.ExplainUnreadable(path, ex));
                    }

                    return;
                }
                finally
                {
                    if (ReferenceEquals(_sequenceLoadCts, loadCts))
                    {
                        _sequenceLoadCts = null;
                    }

                    loadCts.Dispose();
                }

                // 読み込みの間に始まった操作・読み込みなどに譲る(上の Yielded)
                if (Yielded())
                {
                    image.Dispose();
                    return;
                }

                // 通常のファイル連番では既存どおり同一サイズだけを送る。寸法の違う画像ファイルが混じっていたら、
                // 読めないときと同じく再生を止めて理由を知らせる
                if (_currentImage is not null
                    && SequenceFileLoad.CheckSize(path, image, _currentImage) is { } sizeRefusal)
                {
                    image.Dispose();
                    NotifySequenceMoveFailed(sizeRefusal);
                    return;
                }

                // raw は現在のフォーマット(右パネルの Bayer 指定を含む)で読んでいる。画像ファイル(TIFF等)は
                // ファイル自身のフォーマットに、右パネルの Bayer 指定を引き継ぐ(TIFFのページ送りと同じ規約。
                // グレーの画像ではファイルの CFAPattern より指定を優先し、カラー画像には付けない)。
                // ファイル自身の Bayer(通常の TIFF はなし)をそのまま使うと、指定が送りで消えて
                // Bayer 系の表示が Raw 表示へ戻る
                RawFormat format = isRaw ? expectedFormat
                    : ImageFileBayer.Apply(image.Format, color is not null, _sequenceBayerOverride);

                // ビット深度やカラー/グレーが変わると、黒レベル上限・画像情報・
                // フォーマットパネル・表示モードの前提が崩れる。追従させる
                bool layoutChanged = format.BitDepth != _currentFormat!.BitDepth
                    || (color is not null) != (_colorImage is not null)
                    || format.Bayer != _currentFormat.Bayer;

                // 表示と MainWindow の状態は同じUIターンで交換する(TIFFのページ送りと同じ)。
                // ReplaceImageAsync は最初の await より前にビューポートの参照を交換する。
                // 状態の交換を await の後にすると、その間に始まった保存・演算などが旧画像を
                // 対象にし、その途中で画像を差し替え・破棄してしまう。
                // 旧画像を読んでいる解析は先に止め、旧画像と旧Bayerピラミッドは
                // それを読んでいた描画が止まってから破棄する
                // (走行中だとParallel.For内でObjectDisposedExceptionになる)
                CancelAnalysis();

                // 旧画像の縮小ピラミッド・Bayerピラミッドの生成も打ち切る(TIFFのページ送りと同じ)。
                // 生成は _loadCts のトークンで走り、結果は差し替え後の画像と照合して捨てられるだけなので、
                // 止めないと旧画像を破棄するまで、また破棄後も元画像を読まない縮小段が CPU とメモリを使い続ける。
                // 新しい画像のピラミッドは差し替え後に新しいトークンで作る
                ReplaceLoadCts(new CancellationTokenSource());
                RawImage? oldImage = _currentImage;
                BayerPyramid? oldBayer = _mainBayerPyramid;
                _currentImage = image;
                _currentFormat = format;
                _currentPath = path;

                // 送り先は表示中のフォーマットで読んだ(記憶から推定したのではない)ので、推定の通知は消す。
                // 送りでは同じサイズの記憶へ記録しない(開いたときに記録済みのフォーマットで読むだけ)
                _openedRawFormat = isRaw ? format : null;
                SetMainFormatNotice("");
                _valueNote = valueNote;
                _tiffStack = pageCount > 1
                    ? new TiffStackSource(path, pageCount, pageNavigationEnabled: false)
                    { BayerOverride = format.Bayer } : null;
                _tiffPageIndex = 0;
                _mainPyramid = null;
                _mainBayerPyramid = null;
                _sequenceIndex = index;

                // 欠陥検出の結果は旧画像の座標・画素のもの。他の差し替え経路(開く・TIFFのページ送り)と
                // 同じく、検出元への参照(この後で破棄する旧画像)を手放し、マーカーと欠陥ウィンドウの一覧も
                // 破棄して別画像の欠陥を残して見せない。送れなかったときは画像も検出結果もそのまま残すので、
                // マーカーを消すのは差し替えるときだけにする
                ClearDefectSource();
                Viewport.SetDefectMarkers(null);
                _defectWindow?.DiscardResult();

                // カラー画像は輝度と一緒に差し替える。片方だけだと前フレームの色が残る
                _colorImage = color;
                _vm.IsColorImage = color is not null;
                Task<RawImage?> pending = Viewport.ReplaceImageAsync(image, format, color: color);

                // 表示モードはツールバーの選択を保つ。新しい画像で成立しないモード(カラー画像への
                // Bayer系表示、Bayerなしでのカラー・現像・分割)だけ、選択も含めて戻す
                // (SetColorImage は選択を見ずに Raw/カラー表示へ戻すので、表示と選択が食い違う)。
                // 選択の変更は OnDisplayModeChanged を通るので状態の交換後に行う
                ApplyDisplayModeToNewImage(color is not null, format.Bayer);

                Title = $"RawAnalyzer — {Path.GetFileName(path)}{TiffPageNote}";
                if (layoutChanged)
                {
                    // 上限だけ変えると旧ビット深度のコード値が残り、次の操作で白点が飛ぶ
                    SyncLevelControlsToActiveBitDepth();
                    UpdateFormatPanel(format);
                    _histogram = null;
                    _channelHistograms = null;

                    // 現像LUT(白飛びの判定に素材のビット深度を使う)は、カラー現像のまま送った場合に
                    // 上の ApplyDisplayModeToNewImage が新しいビット深度で作り直している
                }

                // ノイズ測定ウィンドウの対象名・飽和コード・ROI の有無を新しい画像へ合わせる
                // (表示モードの適用後に行う。分割表示かどうかで ROI を測れるかが変わる)
                UpdateNoiseWindowSource();

                // カーソル位置の画素値も送った先の画像から読み直す(寸法は同じ。ビット深度・パターンは
                // 送った先のもの)
                RefreshCursorReadout();

                long frameFileSize = SafeFileSize(path);
                _vm.ImageInfoText =
                    $"{image.Width}×{image.Height} · {format.BitDepth}bit"
                    + (color is not null ? " · RGB" : "")
                    + (frameFileSize >= 0
                        ? $" · {frameFileSize / (1024.0 * 1024.0):F1} MB"
                        : "") + TiffPageNote + ValueNoteSuffix;
                _vm.SelectedFile = _vm.FilteredFiles.FirstOrDefault(f => string.Equals(
                    f.FullPath, path, StringComparison.OrdinalIgnoreCase));
                // Filesのリスト・位置・再生状態を維持する。TIFFのページ送りへは切り替えない。

                try
                {
                    await pending;
                }
                finally
                {
                    oldBayer?.Dispose();
                    oldImage?.Dispose();
                }
            }

        }
        finally
        {
            // 読み込み失敗・サイズ不一致などで送れなかった場合も、
            // スライダーとフレーム番号を実際の表示内容へ戻す
            UpdateSequenceUi();
            _sequenceBusy = false;
        }

        // 再生中の送りでも、読み込みの間に再生が止まっていたら送った先で作り直す(止めたときの作り直しは
        // 送る前の画像に対して始まり、上の入れ替えで取り消されている)
        if (SequenceNavigation.RefreshesAnalysisAfterMove(refreshAnalysis, _playTimer?.IsEnabled == true))
        {
            RefreshAfterSequenceMove();
        }
    }

    private void RefreshAfterSequenceMove()
    {
        if (_currentImage is null)
        {
            return;
        }

        // ピラミッドは生成元フレームでしか使えない。フレーム送りでも作り直さないと
        // フレーム1以降が常に等倍描画になり、縮小表示のパン追従が破綻する
        if (_derivedImage is null && !Viewport.HasPyramidForCurrentFrame)
        {
            _ = BuildPyramidAsync(
                _currentImage, _loadCts?.Token ?? CancellationToken.None, Viewport.Frame);
        }

        if (Viewport.DisplayMode is ViewportDisplayMode.BayerColor
            or ViewportDisplayMode.ColorDevelop or ViewportDisplayMode.ChannelSplit)
        {
            _ = EnsureBayerPyramidAsync();
        }

        RefreshHistogram(Viewport.Roi is { PixelCount: > 0 } roi ? roi : null);

        // ラインプロファイル窓も、送った先の画像・フレームで同じ基準点・方向と送った後のROIで計算し直す
        // (送りで取り消した計算は結果を出さないので、開いたままだと送る前の断面が残る)
        RefreshLineProfile();
    }

    private void OnPlayToggleChanged(object sender, RoutedEventArgs e)
    {
        if (PlayToggle.IsChecked == true)
        {
            if (SequenceCount <= 1)
            {
                PlayToggle.IsChecked = false;
                return;
            }

            // 実行中は再生しない(始めても送れず、実行中に入ると再生は止まる)。理由を知らせる
            if (NotifySequenceBusy())
            {
                PlayToggle.IsChecked = false;
                return;
            }

            PlayToggle.Content = "⏸ 停止";
            _playTimer ??= new DispatcherTimer();
            _playTimer.Tick -= OnPlayTick;
            _playTimer.Tick += OnPlayTick;
            _playTimer.Interval = TimeSpan.FromSeconds(1.0 / CurrentPlaybackFps());
            _playTimer.Start();
        }
        else
        {
            StopPlayback();
        }
    }

    /// <summary>
    /// 再生フレームレート。一覧の選択と手入力のどちらも同じ経路で読む。
    /// </summary>
    /// <returns>0.1〜240のフレームレート。</returns>
    private double CurrentPlaybackFps()
    {
        return FpsInput.Parse(FpsCombo.Text, DefaultPlaybackFps);
    }

    private void OnFpsChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyPlaybackFps();
    }

    private void OnFpsTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // 手入力(編集可能コンボ)はSelectionChangedが出ないのでこちらで拾う
        ApplyPlaybackFps();
    }

    private void ApplyPlaybackFps()
    {
        // 再生中の変更も即座にタイマー間隔へ反映する(開始時にしか読まないと
        // 表示上の値と実際の再生速度が食い違う)
        if (_playTimer?.IsEnabled == true)
        {
            _playTimer.Interval = TimeSpan.FromSeconds(1.0 / CurrentPlaybackFps());
        }
    }

    /// <summary>
    /// 重い処理の実行中スコープに入る。再生を止め、連番送りを抑止する。
    /// 表示画像を使う操作として数え、その間は通常の読み込みを確定させない。
    /// 戻り値を using で受けること。
    /// </summary>
    private BusyScope EnterBusy() => EnterBusyCore(_imageGate.EnterOperation());

    /// <summary>
    /// 通常の読み込み(OpenPath)用の実行中スコープに入る。再生を止め、連番送りを抑止するが、
    /// 表示画像を使う操作としては数えない(読み込み自身が操作の終了を待って確定するため)。
    /// 戻り値を using で受けること。
    /// </summary>
    private BusyScope EnterLoadBusy() => EnterBusyCore(operation: null);

    private BusyScope EnterBusyCore(IDisposable? operation)
    {
        CancelTiffPageLoad();

        // ファイル連番の送りの読み込みも取り消す(TIFFのページ送りと同じ。結果は実行中の判定で捨てるので、
        // 読み込みを最後まで走らせない)
        _sequenceLoadCts?.Cancel();
        StopPlayback();
        _busyDepth++;
        return new BusyScope(this, operation);
    }

    /// <summary>
    /// 表示画像の差し替えを待っている間(通常の読み込みの確定待ち、HDR分割・合成の計算など完了時に
    /// 表示画像を差し替えるモーダルでない処理の途中)なら、表示画像を使う操作を始めずに理由を知らせる。
    /// </summary>
    /// <remarks>
    /// 保存・バッチ書き出し・ノイズ測定は、ダイアログ・進捗表示(Dispatcher の入れ子ポンプ)の間も開始時の
    /// 画像を対象にし続ける。差し替えを待っている間に始めると、その中で差し替えが走り、操作のために作った
    /// 画面と別の画像を処理したり(HDR合成の計算中に保存を始めると派生ビューを保存していた)、処理中の画像が
    /// 破棄されたりする。縮小表示の作成中や、差し替えを終えた処理の後始末の間は始められる(BusyNotice.ForImageUse)。
    /// </remarks>
    /// <param name="operation">操作名(「保存」など)。</param>
    /// <returns>拒否した場合はtrue。</returns>
    private bool RejectWhileImageReplacing(string operation)
    {
        if (BusyNotice.ForImageUse(_imageGate.IsLoadPending, _imageGate.IsReplacementPending)
            is not { } reason)
        {
            return false;
        }

        MessageBox.Show(this, BusyNotice.ForOperation(reason, operation),
            "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    /// <summary>
    /// 実行中(読み込み・操作・縮小表示の作成、表示画像の差し替え待ち)なら、画像を処理する操作を始めずに理由を知らせる。
    /// </summary>
    /// <remarks>
    /// <see cref="RejectWhileImageReplacing"/> は表示画像の差し替えを待っている間だけを断る(保存などは縮小表示の
    /// 作成中や、差し替えを終えた他の操作の内側でも始められる)。実行中の処理すべてを断る操作(結果で表示中の画像を
    /// 差し替えるビニング・フィルタ・画像演算・欠陥補正、実行中の処理の完了で結果が表示中の画像の
    /// ものでなくなる欠陥検出、結果で派生ビューを表示するHDR分割・合成)は、何が実行中かを
    /// 同じ形のダイアログで知らせる(黙って無視しない)。busy スコープの外の差し替え待ち(Raw表示へ戻るときの
    /// 描画の停止待ち)も断る(BusyNotice.ForBusy)。その間に始めると、直後に破棄される派生画像を処理してしまう。
    /// </remarks>
    /// <param name="operation">操作名(「ビニング」など)。</param>
    /// <returns>拒否した場合はtrue。</returns>
    private bool RejectWhileBusy(string operation)
    {
        if (CurrentBusyReason() is not { } reason)
        {
            return false;
        }

        MessageBox.Show(this, BusyNotice.ForOperation(reason, operation),
            "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    /// <summary>
    /// 実行中(読み込み・操作・縮小表示の作成、表示画像の差し替え待ち)でフレームを送れないとき、理由をステータスバーに出す。
    /// </summary>
    /// <remarks>
    /// 送り(ボタン・キー・スライダー・再生)は続けて起こり得るので、ダイアログではなく
    /// ステータスバーで知らせる。送りの途中で始まった処理に譲って送りをやめる場合は、
    /// 利用者がその処理を始めたことで分かるので知らせない。
    /// </remarks>
    /// <returns>実行中で送れない場合はtrue。</returns>
    private bool NotifySequenceBusy()
    {
        if (CurrentBusyReason() is not { } reason)
        {
            return false;
        }

        _vm.ImageInfoText = BusyNotice.ForSequence(reason);
        return true;
    }

    /// <summary>
    /// ファイル連番の送り先を表示できなかった(読めない・寸法が違う)とき、再生を止めて理由をステータスバーに出す。
    /// </summary>
    /// <remarks>
    /// 再生は毎ティック次のファイルを要求するので、止めないと同じファイルの読み込みを繰り返して先へ進まない。
    /// 送り先は飛ばさず、表示中の画像と位置はそのまま残す(TIFFのページ送りが読めないページで再生を止めるのと同じ)。
    /// </remarks>
    /// <param name="reason">送れなかった理由(<see cref="SequenceFileLoad"/>)。</param>
    private void NotifySequenceMoveFailed(string reason)
    {
        bool wasPlaying = _playTimer?.IsEnabled == true;
        StopPlayback();
        _vm.ImageInfoText = wasPlaying ? $"{reason}(再生を止めました)" : reason;
    }

    /// <summary>
    /// 実行中(<see cref="_busyDepth"/> が正、または表示画像の差し替え待ち)である理由。実行中でなければ null。
    /// </summary>
    private BusyReason? CurrentBusyReason() =>
        BusyNotice.ForBusy(_busyDepth > 0, _imageGate.IsLoadPending, _imageGate.IsOperationRunning,
            _imageGate.IsReplacementPending);

    /// <summary>重い処理の実行中スコープ。Disposeで抜ける。</summary>
    private readonly struct BusyScope : IDisposable
    {
        private readonly MainWindow _owner;
        private readonly IDisposable? _operation;

        internal BusyScope(MainWindow owner, IDisposable? operation)
        {
            _owner = owner;
            _operation = operation;
        }

        /// <summary>スコープを抜ける。</summary>
        public void Dispose()
        {
            _owner._busyDepth--;
            _operation?.Dispose();
        }
    }

    private void StopPlayback()
    {
        bool wasPlaying = _playTimer?.IsEnabled == true;
        _playTimer?.Stop();
        if (PlayToggle is not null)
        {
            PlayToggle.IsChecked = false;
            PlayToggle.Content = "▶ 再生";
        }

        if (wasPlaying)
        {
            RefreshAfterSequenceMove();
        }
    }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        if (!_sequenceBusy && _busyDepth == 0)
        {
            _ = ShowSequenceIndexAsync(_sequenceIndex + 1, refreshAnalysis: false);
        }
    }

    private void OnSeqSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSequenceUi || !_vm.HasSequence)
        {
            return;
        }

        bool playing = _playTimer?.IsEnabled == true;
        _ = ShowSequenceIndexAsync((int)Math.Round(e.NewValue), refreshAnalysis: !playing);
    }

    private void OnSeqFirstClick(object sender, RoutedEventArgs e)
    {
        _ = ShowSequenceIndexAsync(0, refreshAnalysis: true);
    }

    private void OnSeqPrevClick(object sender, RoutedEventArgs e)
    {
        _ = ShowSequenceIndexAsync(_sequenceIndex - 1, refreshAnalysis: true);
    }

    private void OnSeqNextClick(object sender, RoutedEventArgs e)
    {
        _ = ShowSequenceIndexAsync(_sequenceIndex + 1, refreshAnalysis: true);
    }

    private void OnSeqLastClick(object sender, RoutedEventArgs e)
    {
        _ = ShowSequenceIndexAsync(SequenceCount - 1, refreshAnalysis: true);
    }

    // ---- フルスクリーン ----

    private void ApplyFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            _preFullscreenState = WindowState;
            _preFullscreenStyle = WindowStyle;
            _preFullscreenResize = ResizeMode;

            MainMenuBar.Visibility = Visibility.Collapsed;
            ToolBarPanel.Visibility = Visibility.Collapsed;
            StatusBarPanel.Visibility = Visibility.Collapsed;
            UpdatePanelLayout();

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // 一度戻さないと最大化が効かない場合がある
            WindowState = WindowState.Maximized;
            _vm.LevelOverlayText = "フルスクリーン (F11 / Esc で解除)";
        }
        else
        {
            MainMenuBar.Visibility = Visibility.Visible;
            ToolBarPanel.Visibility = Visibility.Visible;
            StatusBarPanel.Visibility = Visibility.Visible;
            UpdatePanelLayout();

            WindowStyle = _preFullscreenStyle;
            ResizeMode = _preFullscreenResize;
            WindowState = _preFullscreenState;
        }

        Viewport.Focus();
    }

    // ---- 左右パネルの幅調整・表示切替 ----

    private double _leftPanelWidth = 220;
    private double _rightPanelWidth = 240;

    /// <summary>
    /// パネルの表示状態と幅をグリッドへ反映する。
    /// 非表示時は列幅を0にして端のストリップを出す(フルスクリーン時は両方隠す)。
    /// </summary>
    private void UpdatePanelLayout()
    {
        bool fullscreen = _vm.IsFullscreen;
        bool left = _vm.LeftPanelVisible && !fullscreen;
        bool right = _vm.RightPanelVisible && !fullscreen;

        LeftPanel.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitter.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        LeftColumn.Width = left ? new GridLength(_leftPanelWidth) : new GridLength(0);
        LeftColumn.MinWidth = left ? 150 : 0;
        LeftEdgeStrip.Visibility = !left && !fullscreen ? Visibility.Visible : Visibility.Collapsed;

        RightPanel.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        RightSplitter.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        RightColumn.Width = right ? new GridLength(_rightPanelWidth) : new GridLength(0);
        RightColumn.MinWidth = right ? 190 : 0;
        RightEdgeStrip.Visibility = !right && !fullscreen ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>ドラッグで変更された現在の幅を控える(折りたたみ後の復元用)。</summary>
    private void CapturePanelWidths()
    {
        if (LeftPanel.Visibility == Visibility.Visible && LeftColumn.ActualWidth > 0)
        {
            _leftPanelWidth = LeftColumn.ActualWidth;
        }

        if (RightPanel.Visibility == Visibility.Visible && RightColumn.ActualWidth > 0)
        {
            _rightPanelWidth = RightColumn.ActualWidth;
        }
    }

    private void OnHideLeftPanelClick(object sender, RoutedEventArgs e)
    {
        _vm.LeftPanelVisible = false;
    }

    private void OnShowLeftPanelClick(object sender, RoutedEventArgs e)
    {
        _vm.LeftPanelVisible = true;
    }

    private void OnHideRightPanelClick(object sender, RoutedEventArgs e)
    {
        _vm.RightPanelVisible = false;
    }

    private void OnShowRightPanelClick(object sender, RoutedEventArgs e)
    {
        _vm.RightPanelVisible = true;
    }

    // ---- フォルダツリー ----

    private const string TreeDummyChild = "…";
    private bool _syncingTree;

    private void InitFolderTree()
    {
        FolderTree.Items.Clear();
        var items = new List<(System.Windows.Controls.TreeViewItem Item, DriveInfo Drive)>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            var item = new System.Windows.Controls.TreeViewItem
            {
                Header = $"💽 {drive.Name.TrimEnd('\\')}",
                Tag = drive.RootDirectory.FullName,
            };
            item.Items.Add(TreeDummyChild);
            FolderTree.Items.Add(item);
            items.Add((item, drive));
        }

        // IsReady/VolumeLabel は切断されたネットワークドライブだと
        // タイムアウトまでブロックする。ツリーは先に出してから名前を足す
        _ = AppendDriveLabelsAsync(items);
    }

    /// <summary>ドライブのボリュームラベルを後から見出しへ足す。</summary>
    /// <param name="items">対象のツリー項目とドライブ。</param>
    /// <returns>更新の完了を表すタスク。</returns>
    private static async Task AppendDriveLabelsAsync(
        List<(System.Windows.Controls.TreeViewItem Item, DriveInfo Drive)> items)
    {
        foreach ((System.Windows.Controls.TreeViewItem item, DriveInfo drive) in items)
        {
            string label;
            try
            {
                label = await Task.Run(() => drive.IsReady ? drive.VolumeLabel : "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(label))
            {
                item.Header = $"{item.Header} ({label})";
            }
        }
    }

    private void OnFolderTreeExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TreeViewItem item)
        {
            PopulateTreeItem(item);
        }
    }

    private static void PopulateTreeItem(System.Windows.Controls.TreeViewItem item)
    {
        if (item.Items.Count != 1 || !Equals(item.Items[0], TreeDummyChild)
            || item.Tag is not string path)
        {
            return;
        }

        item.Items.Clear();
        try
        {
            // 属性は列挙結果に含まれているものを使う。ディレクトリごとに
            // File.GetAttributes を呼ぶとSMBでは1件ごとに往復が増え、
            // フォルダ数の多いネットワーク共有では展開1回で数秒固まる
            foreach (DirectoryInfo dir in new DirectoryInfo(path).EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(d => d.FullName, NaturalOrderComparer.Instance))
            {
                var child = new System.Windows.Controls.TreeViewItem
                {
                    Header = $"📁 {dir.Name}",
                    Tag = dir.FullName,
                };
                child.Items.Add(TreeDummyChild);
                item.Items.Add(child);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void OnFolderTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingTree)
        {
            return;
        }

        if (e.NewValue is System.Windows.Controls.TreeViewItem { Tag: string path }
            && Directory.Exists(path))
        {
            LoadFolder(path, selectPath: null);
        }
    }

    /// <summary>フォルダツリーを指定パスまで展開して選択する(ベストエフォート)。</summary>
    private void ExpandTreeToFolder(string folder)
    {
        if (_syncingTree)
        {
            return;
        }

        _syncingTree = true;
        try
        {
            string? root = Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            System.Windows.Controls.TreeViewItem? node = FolderTree.Items
                .OfType<System.Windows.Controls.TreeViewItem>()
                .FirstOrDefault(i => string.Equals(
                    (string)i.Tag, root, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                return;
            }

            node.IsExpanded = true;
            PopulateTreeItem(node);
            string relative = folder[root.Length..].Trim('\\');
            if (relative.Length > 0)
            {
                foreach (string segment in relative.Split('\\'))
                {
                    System.Windows.Controls.TreeViewItem? next = node.Items
                        .OfType<System.Windows.Controls.TreeViewItem>()
                        .FirstOrDefault(i => string.Equals(
                            Path.GetFileName((string)i.Tag), segment,
                            StringComparison.OrdinalIgnoreCase));
                    if (next is null)
                    {
                        return;
                    }

                    next.IsExpanded = true;
                    PopulateTreeItem(next);
                    node = next;
                }
            }

            node.IsSelected = true;
            node.BringIntoView();
        }
        finally
        {
            _syncingTree = false;
        }
    }

    // ---- 欠陥画素検出 ----

    private DefectPixelWindow? _defectWindow;

    // HDR表示(派生ビュー)中は欠陥補正しない理由と次にすること。補正を断るときと、
    // HDR表示中の検出結果に添えて欠陥ウィンドウに示すときの両方で使う
    private const string HdrDefectCorrectionRefusal =
        "HDR表示中は欠陥補正できません。Raw表示に戻してから検出し直して補正してください。";

    private void OnDefectDetectClick(object sender, RoutedEventArgs e)
    {
        if (ActiveImage is null)
        {
            return;
        }

        if (_defectWindow is null)
        {
            _defectWindow = new DefectPixelWindow { Owner = this };
            _defectWindow.RunRequested += OnDefectRunRequested;
            _defectWindow.DefectActivated += OnDefectActivated;
            _defectWindow.CorrectionRequested += OnDefectCorrectionRequested;
            _defectWindow.Closed += (_, _) =>
            {
                // 一覧はウィンドウと一緒に消えるので、検出元も手放す(開き直したウィンドウは未実行から始まる。
                // 残すと Bayer パターンの変更で、何も表示していないウィンドウに破棄の案内を出してしまう)
                _defectWindow = null;
                ClearDefectSource();
                Viewport.SetDefectMarkers(null);
            };
            _defectWindow.Show();
        }

        _defectWindow.Activate();
    }

    private void OnDefectRunRequested(double sigma, bool detectHot, bool detectDead)
    {
        if (ActiveImage is null)
        {
            _defectWindow?.ResetRunButton();
            return;
        }

        // 読み込み中・縮小表示の作成中・他の処理の実行中は始めない(ビニング・フィルタ・補正・演算と同じ)。
        // HDR分割・合成の計算中に始めると、進捗表示の中で派生ビューが表示され、検出結果は表示中の画像の
        // ものではなくなって黙って捨てられる。理由を知らせ、実行ボタンを押せる状態へ戻す
        if (RejectWhileBusy("欠陥画素検出"))
        {
            _defectWindow?.ResetRunButton();
            return;
        }

        using BusyScope busy = EnterBusy();
        RawImage image = ActiveImage;
        int frame = Viewport.Frame;
        int maxCode = (1 << image.Format.BitDepth) - 1;

        // Bayerはチャネル感度差で混合σが膨らみ閾値が値域外へ出るため、チャネル別に判定する
        BayerPattern pattern = ActiveFormat?.Bayer ?? BayerPattern.None;

        // このリストは「この画像・このフレーム・このBayerパターン」でのみ有効(採用時と補正時に検証する)
        var source = new DefectDetectionSource(image, frame, pattern);

        // 巨大画像では数十秒かかるため、進捗表示とキャンセルを付ける
        DefectDetectionResult? result = null;
        ProgressWindow progress = ProgressWindow.Run(
            this,
            "欠陥画素を検出中…",
            (report, ct) => Task.Run(
                () => result = DefectPixelDetector.Detect(
                    image, frame, sigma, detectHot, detectDead,
                    pattern: pattern, progress: report, cancellationToken: ct),
                ct));

        if (progress.Error is not null)
        {
            MessageBox.Show(this, $"検出に失敗しました: {progress.Error.Message}", "欠陥画素検出",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _defectWindow?.ResetRunButton();
            return;
        }

        // 検出中に表示中の画像・フレーム・Bayerパターンが替わっていたら、結果は表示中の画像を
        // いまの条件で検出したものではないので採用しない(旧条件の一覧で補正させない)
        if (progress.WasCanceled || result is null || _defectWindow is null
            || !source.IsCurrent(ActiveImage, Viewport.Frame, ActiveFormat?.Bayer ?? BayerPattern.None))
        {
            _defectWindow?.ResetRunButton();
            return;
        }

        // HDR表示中の検出結果は派生ビューの座標・画素のもので、補正は HDR 表示中は断る。
        // 一覧・移動・コピー・CSV は使えるようにし、断られるだけの補正ボタンは有効にせず理由を示す
        _defectWindow.ShowResult(result, maxCode,
            correctionUnavailableReason: _derivedImage is not null ? HdrDefectCorrectionRefusal : null);
        _defectSource = source;
        Viewport.SetDefectMarkers(result.Defects);
    }

    private void OnDefectActivated(DefectPixel defect)
    {
        // 欠陥は元画像の座標。チャネル分割表示ではその画素が並ぶ象限上の位置へ移動する
        Viewport.CenterOnSourcePixel(defect.X, defect.Y, Math.Max(Viewport.Zoom, 32));
    }

    // ---- ノイズ / ダイナミックレンジ測定 ----

    private NoiseMeasureDialog? _noiseWindow;

    private void OnNoiseMeasureClick(object sender, RoutedEventArgs e)
    {
        if (ActiveImage is null || ActiveFormat is null)
        {
            return;
        }

        if (_noiseWindow is null)
        {
            _noiseWindow = new NoiseMeasureDialog(
                NoiseSourceName(),
                _currentFolder,
                (1 << ActiveFormat.BitDepth) - 1,
                HasAnalyzableRoi,
                ExpectedReferenceSize())
            {
                Owner = this,
            };
            _noiseWindow.MeasureRequested += OnNoiseMeasureRequested;
            _noiseWindow.Closed += (_, _) => _noiseWindow = null;
            _noiseWindow.Show();
        }
        else
        {
            UpdateNoiseWindowSource();
        }

        _noiseWindow.Activate();
    }

    /// <summary>
    /// 「素データではない」ことを示す常設バッジを更新する。
    /// </summary>
    /// <remarks>
    /// 補正・派生の状態を ImageInfoText に埋めていたため、保存完了メッセージ等で
    /// 上書きされて消え、加工済みデータを素データと誤認したままヒストグラムを
    /// 読む危険があった。
    /// </remarks>
    private void UpdateProcessingBadge()
    {
        if (_derivedImage is not null)
        {
            _vm.IsProcessed = true;
            _vm.ProcessingStateText = _hdrFloatImage is not null ? "⚠ HDR合成" : "⚠ HDR分割";
            _vm.ProcessingStateTooltip =
                "表示中の画像はHDR処理後の派生ビューです。統計値も派生ビューに対するものです。";
            return;
        }

        if (_correctionLabel is not null)
        {
            _vm.IsProcessed = true;
            _vm.ProcessingStateText = $"⚠ 加工済: {_correctionLabel}";
            _vm.ProcessingStateTooltip =
                $"適用済み: {_correctionLabel}{Environment.NewLine}" +
                "統計値も加工後のデータに対するものです。再読込で元に戻せます。";
            return;
        }

        _vm.IsProcessed = false;
        _vm.ProcessingStateText = "";
        _vm.ProcessingStateTooltip = "";
    }

    private string NoiseSourceName()
    {
        string name = Path.GetFileName(_currentPath ?? "(画像)") + TiffPageNote;
        return _correctionLabel is null ? name : $"{name} [{_correctionLabel}]";
    }

    /// <summary>raw参照ファイルに期待するバイト数(1フレーム分)。不明なら0。</summary>
    private long ExpectedReferenceSize()
    {
        RawFormat? format = ActiveFormat;
        return format is null ? 0 : format.HeaderOffset + format.FrameSizeInBytes;
    }

    /// <summary>
    /// ノイズ測定ダイアログの対象表示・飽和コード既定値・ROI有無を現在の画像に合わせる。
    /// </summary>
    private void UpdateNoiseWindowSource()
    {
        if (_noiseWindow is null || ActiveFormat is null)
        {
            return;
        }

        _noiseWindow.UpdateSource(
            NoiseSourceName(),
            _currentFolder,
            (1 << ActiveFormat.BitDepth) - 1,
            HasAnalyzableRoi,
            ExpectedReferenceSize());
    }

    private void OnNoiseMeasureRequested(NoiseMeasureRequest request)
    {
        RawImage? image = ActiveImage;
        RawFormat? format = ActiveFormat;
        if (image is null || format is null)
        {
            _noiseWindow?.ResetRunButton();
            return;
        }

        if (RejectWhileImageReplacing("ノイズ測定"))
        {
            _noiseWindow?.ResetRunButton();
            return;
        }

        using BusyScope busy = EnterBusy();
        int frame = Viewport.Frame;

        // 2枚目は常に先頭フレーム・先頭ページを読む。対象Aと同一ファイルの先頭を表示中に
        // 指定すると完全に同一のデータ同士になり、σ_temporal=0という
        // 誤った測定値が無警告で出てしまう。TIFFの2ページ目以降を表示中なら別データ
        if (request.ReferencePath is not null
            && NoiseReference.ReadsSameDataAsTarget(
                request.ReferencePath, _currentPath, _derivedImage is not null, frame,
                _tiffPageIndex))
        {
            MessageBox.Show(this,
                "2枚目に対象Aと同じファイルが指定されています。同一データ同士の差分は" +
                "常に0になり、時間ノイズを測定できません。別撮りのフレームを指定するか、" +
                "マルチフレーム・複数ページのファイルなら表示フレーム・ページを変えてください" +
                "(2枚目は先頭フレーム・先頭ページを使います)。",
                "ノイズ測定", MessageBoxButton.OK, MessageBoxImage.Warning);
            _noiseWindow?.ResetRunButton();
            return;
        }

        RegionOfInterest? roi = request.UseRoi && Viewport.Roi is { PixelCount: > 0 } r ? r : null;

        // ROIは表示されている画素へ対応づけて測る(チャネル分割では1チャネルの格子)。
        // 対応づけられないROIを画像全体などで代用すると「ROI内」の値と誤読される
        RoiAnalysisTarget target = ResolveRoiTarget(image, roi);
        if (target is UnsupportedRoiTarget unsupported)
        {
            MessageBox.Show(this, unsupported.Reason, "ノイズ測定",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _noiseWindow?.ResetRunButton();
            return;
        }

        NoiseMeasurement measurement = default;

        ProgressWindow result = ProgressWindow.Run(
            this,
            "ノイズを測定中…",
            (progress, ct) => Task.Run(() =>
            {
                if (request.ReferencePath is null)
                {
                    measurement = RoiAnalysis.MeasureNoise(
                        image, null, frame, target, format.Bayer, request.SaturationCode, ct);
                    return;
                }

                using RawImage reference = IsRawFile(request.ReferencePath)
                    ? RawLoader.Load(request.ReferencePath, format with { FrameCount = 1 }, ct)
                    : ImageFileLoader.Load(request.ReferencePath, ct).Luminance;
                measurement = RoiAnalysis.MeasureNoise(
                    image, reference, frame, target, format.Bayer,
                    request.SaturationCode, ct);
            }, ct));

        if (result.Error is not null)
        {
            MessageBox.Show(this, $"測定に失敗しました: {result.Error.Message}", "ノイズ測定",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _noiseWindow?.ResetRunButton();
            return;
        }

        if (result.WasCanceled)
        {
            _noiseWindow?.ResetRunButton();
            return;
        }

        // 1チャネルの格子はそのチャネルのσそのもの(チャネル別に分けて合成していない)
        _noiseWindow?.ShowResult(
            measurement, format.BitDepth,
            request.ReferencePath is null ? null : Path.GetFileName(request.ReferencePath),
            perChannel: format.Bayer != BayerPattern.None && target is not ChannelRoiTarget);
    }

    private async void OnDefectCorrectionRequested(
        DefectDetectionResult detection, DefectCorrectionMethod method)
    {
        if (_currentImage is null || _currentFormat is null || _derivedImage is not null)
        {
            // HDR表示の出入りで検出結果は破棄する(派生ビューの一覧は元画像に使えない)ので、
            // Raw表示へ戻したら検出からやり直すよう案内する
            MessageBox.Show(this, HdrDefectCorrectionRefusal,
                "欠陥画素補正", MessageBoxButton.OK, MessageBoxImage.Information);
            _defectWindow?.ResetRunButton();
            return;
        }

        // HDR分割ビューで検出→Raw表示へ戻す→補正、やファイル送り後の適用は
        // 座標系/データが異なるのに配列範囲内に収まるため、例外にならず
        // 健全画素を黙って上書きしてしまう。右パネルでBayerパターンを変えた後の適用も、
        // 前のパターンで欠陥とした画素を新しいパターンの近傍で書き換える(変えた時点で一覧は
        // 破棄するが、ここでも確かめる)。検出時の画像・フレーム・パターンと一致しない
        // リストの適用は拒否する
        if (_defectSource is null
            || !_defectSource.IsCurrent(_currentImage, Viewport.Frame, _currentFormat.Bayer))
        {
            MessageBox.Show(this,
                "この検出結果は現在表示中の画像・フレーム・Bayerパターンで検出したものではないため" +
                "適用できません。再度「検出実行」を行ってください。",
                "欠陥画素補正", MessageBoxButton.OK, MessageBoxImage.Information);
            _defectWindow?.ResetRunButton();
            return;
        }

        // 読み込み中・縮小表示の作成中・他の処理の実行中は始めない(ビニング・フィルタと同じ)。
        // HDR分割・合成の計算中に始めると、進捗表示の中で派生ビューが表示され、その後で元画像だけを
        // 補正結果へ差し替えてしまう。黙って無視せず理由を知らせる
        if (RejectWhileBusy("欠陥画素補正"))
        {
            _defectWindow?.ResetRunButton();
            return;
        }

        using BusyScope busy = EnterBusy();
        RawImage source = _currentImage;

        // 近傍は検出と同じパターンで選ぶ(上で一致を確かめた)
        BayerPattern pattern = _currentFormat.Bayer;
        int frame = Viewport.Frame;
        int count = detection.Defects.Count;

        RawImage? corrected = null;
        ProgressWindow result = ProgressWindow.Run(
            this,
            $"欠陥画素を補正中 ({count} 画素)",
            (progress, ct) => Task.Run(() =>
            {
                corrected = DefectCorrector.Correct(
                    source, detection.Defects, pattern, method, frame, progress, ct);
            }, ct));

        if (result.Error is not null)
        {
            corrected?.Dispose();
            MessageBox.Show(this, $"欠陥補正に失敗しました: {result.Error.Message}", "欠陥画素補正",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _defectWindow?.ResetRunButton();
            return;
        }

        if (result.WasCanceled || corrected is null)
        {
            corrected?.Dispose();
            _defectWindow?.ResetRunButton();
            return;
        }

        string methodLabel = method == DefectCorrectionMethod.Mean ? "平均" : "メディアン";

        // 補正前の一覧は差し替えで破棄する(残すと「この欠陥を補正」を押せても表示中の画像のもの
        // ではないと断られるだけ)。欠陥ウィンドウには補正したことと、補正後の画像は検出し直して
        // 確かめることを示す(一覧がないので下の ResetRunButton でも補正ボタンは無効のまま)
        bool applied = await ApplyProcessedImageAsync(
            source, corrected, $"欠陥補正 {count}px ({methodLabel})",
            defectNotice: DefectPixelWindow.CorrectionAppliedNotice(count, methodLabel));
        _defectWindow?.ResetRunButton();
        if (!applied)
        {
            // 差し替えなかった(理由は表示済み)。一覧は前提を崩した差し替えの側で破棄されている
            return;
        }

        _vm.ImageInfoText = $"欠陥画素 {count} 個を{methodLabel}補間で補正しました" +
            "(保存すると補正後のデータが出力されます)";
    }

    // ---- フォーマットその場変更 ----

    private void OnFmtBayerChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingFormatPanel || _currentFormat is null)
        {
            return;
        }

        BayerPattern pattern = FmtBayerCombo.SelectedIndex switch
        {
            0 => BayerPattern.Rggb,
            1 => BayerPattern.Bggr,
            2 => BayerPattern.Grbg,
            3 => BayerPattern.Gbrg,
            _ => BayerPattern.None,
        };
        if (_tiffStack is not null)
        {
            _tiffStack.BayerOverride = pattern;
        }

        // ファイル連番の送りでも、次のグレーの画像ファイルへ引き継ぐ(TIFFのページ送りと同じ)
        _sequenceBayerOverride = pattern;

        // 表示中の画像へも送りと同じ規則で付ける。デコード済みのカラー画像には付けない
        // (カラー画像の表示中は右パネルで指定させないが、付けると表示はカラーのまま
        // チャネル別統計・欠陥検出・ノイズ測定が輝度へ Bayer を当てる)。指定は上で保持し、次のグレーの画像に付く
        RawFormat format = ImageFileBayer.Apply(_currentFormat, _colorImage is not null, pattern);
        if (format.Bayer == _currentFormat.Bayer)
        {
            return;
        }

        RawFormat previous = _currentFormat;
        _currentFormat = format;
        if (_derivedImage is null && _currentImage is not null)
        {
            Viewport.UpdateFormat(_currentFormat);
        }

        // カーソル位置のチャネル名を新しいパターンで出し直す
        RefreshCursorReadout();

        // 欠陥検出の結果は検出したときのパターンのもの(閾値はパターンのチャネル別の統計で決まり、
        // 補正はパターンで選んだ近傍から補う)。表示中の画像のパターンが替わったら、他の差し替え経路と
        // 同じく検出元を手放し、マーカーと欠陥ウィンドウの一覧も破棄して検出し直すよう案内する。
        // 残すと前のパターンで欠陥とした正常な画素を、新しいパターンの近傍で補正できてしまう。
        // HDR表示中は表示中の画像(派生ビュー)のパターンは替わらないので、その検出結果は残す
        if (_defectSource is { } defectSource
            && !defectSource.IsCurrent(ActiveImage, Viewport.Frame, ActiveFormat?.Bayer ?? BayerPattern.None))
        {
            ClearDefectSource();
            Viewport.SetDefectMarkers(null);
            _defectWindow?.DiscardResult(DefectPixelWindow.BayerChangedNotice);
        }

        if (_correctionLabel is null && _currentPath is not null && IsRawFile(_currentPath))
        {
            RememberFileFormat(_currentPath, _currentFormat);

            // 同じサイズの記憶にこのファイルの元の形式があれば直す(F2 で開き直したときと同じく、
            // 次に同じサイズのファイルを記憶から開くときは直した Bayer で開く)
            _formatMemory.Correct(_currentPath, SafeFileSize(_currentPath), previous, _currentFormat);
            _openedRawFormat = _currentFormat;
        }

        // チャネル別統計はパターンに依存するため作り直す。
        // 放置すると RGGB→BGGR で R と B を入れ替えた値を表示したままになる
        RefreshHistogram(Viewport.Roi is { PixelCount: > 0 } roi ? roi : null);
    }

    // ---- 右クリックメニュー ----

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        return source as T;
    }

    private void OnZoomHereClick(object sender, RoutedEventArgs e)
    {
        if (_lastCursorInside)
        {
            // カーソル位置は元画像の座標で持っている(分割表示ではタイル上の位置へ戻す)
            Viewport.CenterOnSourcePixel(_lastCursorX, _lastCursorY, Math.Max(Viewport.Zoom, 32));
        }
    }

    private void OnCopyPixelValueClick(object sender, RoutedEventArgs e)
    {
        if (ActiveImage is null || !_lastCursorInside)
        {
            return;
        }

        ushort value;
        try
        {
            value = ActiveImage.GetPixel(_lastCursorX, _lastCursorY, Viewport.Frame);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or ObjectDisposedException)
        {
            return;
        }

        ClipboardHelper.TrySetText((value >> CurrentShift).ToString(CultureInfo.InvariantCulture));
    }

    private void OnCopyPixelPosClick(object sender, RoutedEventArgs e)
    {
        if (_lastCursorInside)
        {
            ClipboardHelper.TrySetText($"{_lastCursorX}\t{_lastCursorY}");
        }
    }

    private void OnCopyViewClick(object sender, RoutedEventArgs e)
    {
        // 高さのガードが無いと ActualHeight=0 で ArgumentOutOfRangeException になる
        if (!_vm.HasImage || Viewport.ActualWidth < 1 || Viewport.ActualHeight < 1)
        {
            return;
        }

        RenderTargetBitmap bitmap;
        try
        {
            bitmap = new RenderTargetBitmap(
                (int)Viewport.ActualWidth, (int)Viewport.ActualHeight, 96, 96,
                PixelFormats.Pbgra32);
            bitmap.Render(Viewport);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException)
        {
            AppLog.Warn($"表示のコピー用ビットマップ生成に失敗: {ex.Message}");
            return;
        }

        _vm.ImageInfoText = ClipboardHelper.TrySetImage(bitmap)
            ? "表示をクリップボードへコピーしました"
            : "クリップボードを使用できませんでした(他のアプリが使用中の可能性があります)";
    }

    private void OnClearRoiClick(object sender, RoutedEventArgs e)
    {
        Viewport.ClearRoi();
    }

    private void OnFileListPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<System.Windows.Controls.ListBoxItem>(
            e.OriginalSource as DependencyObject);
        if (item is not null)
        {
            item.IsSelected = true;
        }
    }

    private void OnFileCtxOpenClick(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFile is { IsDirectory: false } entry)
        {
            OpenPath(entry.FullPath);
        }
    }

    private void OnFileCtxOpenWithFormatClick(object sender, RoutedEventArgs e)
    {
        // raw 以外(画像ファイル)・フォルダでは項目を無効にしてある(画像ファイルは理由をツールチップで示す)。
        // 以前は画像ファイルをダイアログなしで普通に開き、フォーマットを指定して開くつもりの利用者を驚かせた
        if (!_vm.CanOpenSelectedFileWithFormat || _vm.SelectedFile is not { } entry)
        {
            return;
        }

        // 表示中の raw なら F2 と同じ(開き直せたら同じサイズの記憶の元の形式を置き換える)
        if (_currentPath is not null && IsRawFile(_currentPath)
            && string.Equals(entry.FullPath, _currentPath, StringComparison.OrdinalIgnoreCase))
        {
            ChangeCurrentRawFormat(_currentPath);
            return;
        }

        // 記憶フォーマットをスキップして必ずダイアログを表示する。初期値は同じパスの記憶
        // (なければ候補一覧の先頭 → 表示中の画像のフォーマット → ダイアログ既定の推定)
        RawFormat? initial = TryGetRememberedFormat(entry.FullPath, SafeFileSize(entry.FullPath));
        OpenPathChoosingFormat(entry.FullPath, initial, correctFrom: null);
    }

    private void OnFileCtxRevealClick(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFile is { } entry && File.Exists(entry.FullPath))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{entry.FullPath}\"",
                UseShellExecute = true,
            });
        }
    }

    private void OnFileCtxCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFile is { } entry)
        {
            ClipboardHelper.TrySetText(entry.FullPath);
        }
    }

    private void OnFileCtxRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_currentFolder is not null && Directory.Exists(_currentFolder))
        {
            LoadFolder(_currentFolder, _vm.SelectedFile?.FullPath);
        }
    }

    private void OnFileFilterClearClick(object sender, RoutedEventArgs e)
    {
        _vm.ClearFileFilter();
        FileFilterCombo.Focus();
    }

    /// <summary>絞り込み欄のキー操作。Esc で消去、Enter で一覧へ移動する。</summary>
    private void OnFileFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (FileFilterCombo.IsDropDownOpen)
        {
            return; // ドロップダウン操作中は ComboBox 既定の Esc / Enter に任せる
        }

        if (e.Key == Key.Escape && _vm.FileFilterText.Length > 0)
        {
            _vm.ClearFileFilter();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            FocusFileList();
            e.Handled = true;
        }
    }

    /// <summary>ファイル一覧へフォーカスを移す。選択が無い(または隠れた)場合は先頭を選ぶ。</summary>
    private void FocusFileList()
    {
        if (_vm.SelectedFile is null || !_vm.FilteredFiles.Contains(_vm.SelectedFile))
        {
            _vm.SelectedFile = _vm.FilteredFiles.FirstOrDefault();
        }

        FileListBox.Focus();
        if (_vm.SelectedFile is { } selected)
        {
            FileListBox.ScrollIntoView(selected);
            (FileListBox.ItemContainerGenerator.ContainerFromItem(selected)
                as System.Windows.Controls.ListBoxItem)?.Focus();
        }
    }

    /// <summary>絞り込み欄へフォーカスを移す(Ctrl+F)。左パネルが隠れていれば表示する。</summary>
    private void FocusFileFilter()
    {
        if (!_vm.LeftPanelVisible)
        {
            _vm.LeftPanelVisible = true;
        }

        FileFilterCombo.Focus();
    }

    private void OnTreePreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<System.Windows.Controls.TreeViewItem>(
            e.OriginalSource as DependencyObject);
        if (item is not null)
        {
            item.IsSelected = true;
        }
    }

    private void OnTreeOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is System.Windows.Controls.TreeViewItem { Tag: string path }
            && Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
    }

    private void OnTreeRefreshClick(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is System.Windows.Controls.TreeViewItem item)
        {
            bool wasExpanded = item.IsExpanded;
            item.Items.Clear();
            item.Items.Add(TreeDummyChild);
            if (wasExpanded)
            {
                PopulateTreeItem(item);
            }
        }
    }

    // ---- ドラッグ&ドロップ ----

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
        {
            return;
        }

        string path = paths[0];
        if (Directory.Exists(path))
        {
            LoadFolder(path, selectPath: null);
        }
        else if (File.Exists(path))
        {
            // 連番判定はファイル一覧を見るので、一覧が揃ってから開く
            await LoadFolderAsync(Path.GetDirectoryName(path)!, path);
            OpenPath(path);
        }
    }
}
