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

    // 現像LUTがパラメータ変更で古くなっているか(カラー現像表示に入るまで再生成を遅延)
    private bool _developLutsDirty = true;
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

    // 欠陥検出リストの座標系はこの画像・フレームでのみ有効。
    // 別画像(HDR派生ビュー・ファイル送り後)へ適用すると無関係な画素を壊すため、
    // 補正時に一致を検証する
    private RawImage? _defectSourceImage;
    private int _defectSourceFrame;

    /// <summary>
    /// 欠陥検出の検出元画像への参照を手放す。
    /// </summary>
    /// <remarks>
    /// 表示画像を差し替えたら検出結果は流用できない(補正適用時は
    /// ReferenceEquals で弾かれる)。参照を残すと破棄済み画像のヒープ側画素配列
    /// (最大約200MB)が回収されないため、差し替えのたびに明示的に切る。
    /// </remarks>
    private void ClearDefectSource()
    {
        _defectSourceImage = null;
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
            // ビニング後の縮小寸法を元ファイルの寸法として提案しない。
            RawFormat? initial = _correctionLabel is null ? _currentFormat
                : TryGetRememberedFormat(_currentPath, SafeFileSize(_currentPath));
            OpenPath(_currentPath, initial);
        }
    }

    /// <summary>フォーマット指定が必要な生バイナリ(.raw/.bin)かどうか。</summary>
    private static bool IsRawFile(string path)
    {
        return Compare.ComparePane.IsRawFile(path);
    }

    private async void OpenPath(string path, RawFormat? initialFormat = null)
    {
        // 読み込み中に再生タイマーや保留中の連番送りが画像を差し替えないようにする
        using BusyScope busy = EnterLoadBusy();

        // 開始から確定(または破棄)までは、保存・演算など表示画像を使う操作を始めさせない。
        // 確定がそれらのダイアログ・進捗表示(入れ子ポンプ)の中で走ると、操作のために
        // 作った画面が別の画像を処理し、処理中の画像も破棄されてしまう
        using IDisposable pendingLoad = _imageGate.BeginLoad();
        int generation = ++_openGeneration;

        RawFormat? format = null;
        if (IsRawFile(path))
        {
            // 同じファイルを開き直すときは記憶したフォーマットでダイアログをスキップ
            // (「変更…」から開いた場合 initialFormat が渡されるためダイアログを出す)
            RawFormat? remembered = initialFormat is null
                ? TryGetRememberedFormat(path, SafeFileSize(path))
                : null;
            if (remembered is not null)
            {
                format = remembered;
            }
            else
            {
                var dialog = new RawImportDialog(path, _presetStore, initialFormat ?? _currentFormat)
                {
                    Owner = this,
                };
                if (dialog.ShowDialog() != true || dialog.Result is null)
                {
                    return;
                }

                format = dialog.Result;
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
        _hdrFloatImage = null;
        _hdrFrameParams = null;
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
        _tiffStack = pageCount > 1
            ? new TiffStackSource(path, pageCount) { BayerOverride = image.Format.Bayer } : null;
        _tiffPageIndex = 0;
        _valueNote = valueNote;
        _histogram = null;
        _vm.HasRoi = false;
        _vm.BlackLevelMax = (1 << image.Format.BitDepth) - 1;
        ResetDisplayParameters();

        Title = $"RawAnalyzer — {Path.GetFileName(path)}{TiffPageNote}";
        Viewport.SetDefectMarkers(null);
        _defectWindow?.Close();
        UpdateNoiseWindowSource();
        _recentFiles.Add(path);
        RebuildRecentMenu();
        if (IsRawFile(path))
        {
            RememberFileFormat(path, image.Format);
        }

        UpdateFormatPanel(image.Format);
        long fileSize = SafeFileSize(path);
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
        UpdateDevelopLuts();
        DetectSequence();

        RefreshHistogram(roi: null);

        // 差し替えを終えたので、以降の操作は新しい画像を対象に始めてよい
        // (ピラミッド生成は差し替え後の画像を検証してから取り付ける。二重の Dispose は無視される)
        pendingLoad.Dispose();
        await BuildPyramidAsync(image, cts.Token);
    }

    private async Task BuildPyramidAsync(RawImage image, CancellationToken ct, int frame = 0)
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
        BayerPyramid bayer;
        try
        {
            bayer = await BayerPyramid.CreateAsync(
                image, format, frame, cancellationToken: _loadCts?.Token ?? default);
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            // キャンセル、または画像切替と競合して生成元が破棄された。
            // 作り直しは次の表示切替に任せる
            return;
        }

        if (!ReferenceEquals(image, ActiveImage) || frame != (derived ? 0 : Viewport.Frame))
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

        if (cts.IsCancellationRequested || !ReferenceEquals(image, ActiveImage))
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

    // チャネル分割表示でプロファイルマーカーをクリック位置に出し続けるための表示座標
    private (int X, int Y) _profilePickDisplayPoint;

    private async void OnProfilePointClicked(object? sender, CursorPixelEventArgs e)
    {
        if (ActiveImage is null || ActiveFormat is null)
        {
            return;
        }

        RawImage image = ActiveImage;
        if (!TryMapToSourceCoordinates(image, e.X, e.Y, out int sourceX, out int sourceY))
        {
            return;
        }

        int frame = Viewport.Frame;
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
        double[] row;
        double[] column;
        double[] horizontalProjection = Array.Empty<double>();
        double[] verticalProjection = Array.Empty<double>();

        // 全面ROIの巨大画像では射影に時間がかかる。次のクリックや
        // 画像切替で確実に打ち切れるようにトークンを渡す
        var cts = new CancellationTokenSource();
        ReplaceProfileCts(cts);
        CancellationToken token = cts.Token;
        try
        {
            (row, column, horizontalProjection, verticalProjection) = await Task.Run(() =>
            {
                double[] rowValues = Array.ConvertAll(
                    ImageAnalysis.ExtractRowProfile(image, frame, sourceY), v => (double)v);
                double[] columnValues = Array.ConvertAll(
                    ImageAnalysis.ExtractColumnProfile(image, frame, sourceX), v => (double)v);

                // 水平・垂直を別々に呼ぶとROIを2回走査することになる
                (double[] hp, double[] vp) =
                    RoiAnalysis.ComputeProjections(image, frame, target, token);
                return (rowValues, columnValues, hp, vp);
            }, token);
        }
        catch (Exception)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (!ReferenceEquals(image, ActiveImage))
        {
            return;
        }

        if (_profileWindow is null)
        {
            _profileWindow = new LineProfileWindow { Owner = this };
            _profileWindow.DirectionChanged += horizontal =>
            {
                if (_profileWindow is not null)
                {
                    // マーカーは表示座標に出す(チャネル分割では元座標とずれるため)
                    (int px, int py) = _profilePickDisplayPoint;
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
        _profileWindow.SetProfiles(
            row, column, horizontalProjection, verticalProjection, projectionRoi,
            sourceX, sourceY, maxCode);
        _profilePickDisplayPoint = (e.X, e.Y);
        Viewport.SetProfileMarker(e.X, e.Y, _profileWindow.IsHorizontal);
        _profileWindow.Activate();
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
    /// 現像LUTの再生成を予約する。カラー現像表示中でなければダーティ印だけ付け、
    /// 実際の生成(65536×3回のMath.Powを含む)はモード切替まで遅らせる。
    /// 表示中でもスライダー連続操作で毎ティック作り直さないよう間引く。
    /// </summary>
    private void UpdateDevelopLuts()
    {
        _developLutsDirty = true;
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

    /// <summary>遅延していた現像LUT生成を確定させる(モード切替・保存直前)。</summary>
    private void EnsureDevelopLuts()
    {
        _developLutTimer?.Stop();
        if (!_developLutsDirty)
        {
            return;
        }

        _developLutsDirty = false;
        Viewport.SetDevelopLuts(DevelopLuts.Create(CurrentDevelopParameters()));
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
    /// 指定パスから比較ペイン資源を読み込む。rawは記憶フォーマットを使い、
    /// なければインポートダイアログで確認する。
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
            if (Compare.ComparePane.IsRawFile(path))
            {
                format = TryGetRememberedFormat(path, SafeFileSize(path));
                if (format is null)
                {
                    var dialog = new RawImportDialog(path, _presetStore, _currentFormat)
                    {
                        Owner = this,
                    };
                    if (dialog.ShowDialog() != true || dialog.Result is null)
                    {
                        return null;
                    }

                    format = dialog.Result;
                    RememberFileFormat(path, format);
                }
            }

            return await Compare.ComparePane.LoadAsync(path, format, cancellationToken);
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

        if (RejectWhileOpening("保存"))
        {
            return;
        }

        // ダイアログ表示中も再生タイマーは動くため、ここから保存完了まで差し替えを止める
        using BusyScope busy = EnterBusy();

        // WIC 経路は表示中の1フレームのみ扱うため、判定も1フレームの画素数で行う
        var dialog = new SaveDialog(
            (long)ActiveImage.Width * ActiveImage.Height,
            allowFloatRaw: _hdrFloatImage is not null,
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
                    case SaveFormat.Tiff16:
                        if ((long)image.Width * image.Height
                            > RawLoader.DefaultInMemoryPixelThreshold)
                        {
                            // 巨大画像は自前ライタで行単位ストリーミング
                            TiffWriter.SaveGray16(image, frame, path, progress, ct);
                        }
                        else
                        {
                            SaveWithWic(image, frame, path, choice.Format, mode, pattern,
                                lut, devLuts, colorImage, progress, ct);
                        }

                        break;
                    default:
                        SaveWithWic(image, frame, path, choice.Format, mode, pattern,
                            lut, devLuts, colorImage, progress, ct);
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
                WriteProcessingSidecar(path, choice, developParameters);
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
    private void WriteProcessingSidecar(
        string imagePath, SaveChoice choice, DevelopParameters developParameters)
    {
        try
        {
            RawFormat? format = ActiveFormat;
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
            sb.Append("  表示LUT: ").AppendLine(choice.ApplyDisplayLut ? "適用" : "なし");
            if (choice.ApplyDisplayLut)
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

    private static void SaveWithWic(
        RawImage image, int frame, string path, SaveFormat format, ViewportDisplayMode mode,
        BayerPattern pattern, DisplayLut lut, DevelopLuts devLuts, ColorImage? trueColor,
        IProgress<double> progress, CancellationToken ct)
    {
        int width = image.Width;
        int height = image.Height;
        BitmapSource source;
        switch (format)
        {
            case SaveFormat.Tiff16:
            case SaveFormat.Png16:
                {
                    // 読み込んだRGB画像は輝度化せずチャネルを保って書き出す
                    if (trueColor is not null)
                    {
                        ushort[] rgb48 = ImageExport.RenderColorRgb48(trueColor, ct);
                        progress.Report(0.7);
                        source = BitmapSource.Create(
                            trueColor.Width, trueColor.Height, 96, 96, PixelFormats.Rgb48, null,
                            rgb48, trueColor.Width * 6);
                        break;
                    }

                    {
                        var pixels = new ushort[(long)width * height];
                        for (int y = 0; y < height; y++)
                        {
                            ct.ThrowIfCancellationRequested();
                            image.CopyRegion(frame, 0, y, width, 1, pixels.AsSpan(y * width, width));
                            if ((y & 511) == 0)
                            {
                                progress.Report(0.5 * y / height);
                            }
                        }

                        source = BitmapSource.Create(
                            width, height, 96, 96, PixelFormats.Gray16, null, pixels, width * 2);
                    }

                    break;
                }

            default:
                {
                    // 8bit系は選択された処理を焼き込む
                    if (trueColor is not null)
                    {
                        // 読み込んだRGB画像はデモザイクせずLUTだけ適用する
                        byte[] rgb = ImageExport.RenderColorRgb24(trueColor, lut, ct);
                        progress.Report(0.7);
                        source = BitmapSource.Create(
                            trueColor.Width, trueColor.Height, 96, 96, PixelFormats.Rgb24, null,
                            rgb, trueColor.Width * 3);
                        break;
                    }

                    bool color = mode == ViewportDisplayMode.ColorDevelop
                        && pattern != BayerPattern.None;
                    if (color)
                    {
                        byte[] rgb = ImageExport.DevelopRgb24(
                            image, frame, pattern, devLuts,
                            new Progress<double>(p => progress.Report(p * 0.7)), ct);
                        source = BitmapSource.Create(
                            width, height, 96, 96, PixelFormats.Rgb24, null, rgb, width * 3);
                    }
                    else
                    {
                        byte[] gray = ImageExport.RenderGray8(image, frame, lut, ct);
                        progress.Report(0.7);
                        source = BitmapSource.Create(
                            width, height, 96, 96, PixelFormats.Gray8, null, gray, width);
                    }

                    break;
                }
        }

        ct.ThrowIfCancellationRequested();
        BitmapEncoder encoder = format switch
        {
            SaveFormat.Tiff16 => new TiffBitmapEncoder { Compression = TiffCompressOption.None },
            SaveFormat.Jpeg8 => new JpegBitmapEncoder { QualityLevel = 95 },
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(source));

        // 一時ファイルへ書き切ってから置換する。直接書くと、失敗・キャンセル時に
        // 上書き対象だった既存ファイルを失う
        AtomicFileWriter.Write(path, encoder.Save);
        progress.Report(1.0);
    }

    // ---- 画像演算 (ダーク減算/フラット補正) ----

    private void OnImageCalculatorClick(object sender, RoutedEventArgs e)
    {
        if (_currentImage is null || _currentFormat is null || _currentPath is null)
        {
            return;
        }

        if (RejectWhileOpening("画像演算"))
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
            corrected, $"{opLabel} {Path.GetFileName(choice.ReferencePath)}", closeDefectWindow: true);
    }

    /// <summary>
    /// 加工済み画像を現在の画像として差し替える(以後の解析・現像・保存すべてに反映)。
    /// </summary>
    /// <param name="processed">差し替える画像。</param>
    /// <param name="label">タイトル等に表示する処理ラベル。</param>
    /// <param name="closeDefectWindow">欠陥画素ウィンドウを閉じるか。</param>
    /// <param name="color">RGBの処理結果。processedは同じ画像の輝度であること。</param>
    private async Task ApplyProcessedImageAsync(
        RawImage processed, string label, bool closeDefectWindow, ColorImage? color = null)
    {
        RawImage? expectedSource = _currentImage;
        int displayIndex = color is null && processed.Format.Bayer != BayerPattern.None
            ? Math.Clamp(DisplayModeCombo.SelectedIndex, 0, 3) : 0;
        // 旧画像を読んでいる解析タスクを止めてから破棄する
        CancelAnalysis();
        await Viewport.ClearImageAsync();
        if (!ReferenceEquals(expectedSource, _currentImage))
        {
            processed.Dispose();
            return;
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
        _lastCursorInside = false;
        _profileWindow?.Close();
        // 演算結果の16bit化や寸法変更に合わせてUIを更新する。
        // 表示LUTの内部値は維持し、コード値だけ新しいビット深度へ換算する。
        _updatingSliders = true;
        _vm.BlackLevelMax = (1 << processed.Format.BitDepth) - 1;
        _vm.BlackLevel = _blackPoint >> CurrentShift;
        _vm.WhiteLevel = _whitePoint >> CurrentShift;
        _updatingSliders = false;
        UpdateFormatPanel(processed.Format);
        Viewport.SetDefectMarkers(null);
        if (closeDefectWindow)
        {
            _defectWindow?.Close();
        }

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
        DisplayModeCombo.SelectedIndex = displayIndex;
        Viewport.SetDisplayMode(displayIndex switch
        {
            1 => ViewportDisplayMode.BayerColor,
            2 => ViewportDisplayMode.ColorDevelop,
            3 => ViewportDisplayMode.ChannelSplit,
            _ => ViewportDisplayMode.Raw,
        });
        Viewport.SetLut(BuildLut());
        UpdateDevelopLuts();
        DisplayModeCombo.IsEnabled = color is null;

        // 加工結果はディスク上のファイルと一致しないためシーケンス再生は無効化
        StopPlayback();
        _sequenceMode = SequenceMode.None;
        UpdateSequenceUi();

        RefreshHistogram(roi: null);
        _mainPyramid = null;
        await BuildPyramidAsync(processed, _loadCts?.Token ?? CancellationToken.None);
        if (displayIndex != 0 && ReferenceEquals(processed, _currentImage))
        {
            await EnsureBayerPyramidAsync();
        }
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

        if (RejectWhileOpening("バッチ書き出し"))
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
        // バッチも1フレーム単位で現像するため、判定はフレーム画素数で行う
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
        BayerPattern pattern = format.Bayer;
        bool color = pattern != BayerPattern.None;

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

        var devLuts = DevelopLuts.Create(developParameters);
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
                var frameBuffers = new FrameBuffers();
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
                            ColorImage? trueColor = entry.Color;
                            int frame = entry.Frame;
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
                                    avi.AddFrame(EncodeJpegFrame(
                                        image, frame, color, pattern, devLuts, lut,
                                        trueColor, jpegQuality, ct));
                                }
                                else
                                {
                                    mp4!.AddFrameRgb24(RenderRgb24Frame(
                                        image, frame, color, pattern, devLuts, lut,
                                        trueColor, ct, frameBuffers));
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
                                        image, frame,
                                        Path.Combine(choice.OutputFolder, stem + ".tif"),
                                        null, ct);
                                }
                                else
                                {
                                    bool jpeg = choice.Format == BatchFormat.Jpeg8;
                                    SaveBakedImage(
                                        image, frame, color, pattern, devLuts, lut,
                                        Path.Combine(choice.OutputFolder,
                                            stem + (jpeg ? ".jpg" : ".png")),
                                        jpeg, trueColor, ct);
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

    /// <summary>
    /// 1フレームぶんのRGB24バッファを生成する(MP4エンコード用)。
    /// カラー画像はLUTのみ、Bayerは現像、モノクロはLUT適用後にRGBへ展開する。
    /// </summary>
    /// <summary>
    /// モノクロ経路で使い回すバッファ。フレームごとに確保するとLOHを圧迫する。
    /// </summary>
    private sealed class FrameBuffers
    {
        internal byte[]? Gray;
        internal byte[]? Rgb;

        /// <summary>
        /// 必要な大きさのバッファを用意する(同じ大きさなら使い回す)。
        /// </summary>
        /// <remarks>
        /// 動画ライタは「長さがフレームぴったり」であることを要求するので、
        /// 余りのある使い回しはせず、長さが違えば作り直す。
        /// </remarks>
        /// <param name="pixels">1フレームの画素数。</param>
        internal void Ensure(long pixels)
        {
            if (Gray is null || Gray.LongLength != pixels)
            {
                Gray = new byte[pixels];
            }

            if (Rgb is null || Rgb.LongLength != pixels * 3)
            {
                Rgb = new byte[pixels * 3];
            }
        }
    }

    private static byte[] RenderRgb24Frame(
        RawImage image, int frame, bool color, BayerPattern pattern,
        DevelopLuts devLuts, DisplayLut lut, ColorImage? trueColor, CancellationToken ct,
        FrameBuffers? buffers = null)
    {
        buffers ??= new FrameBuffers();
        if (trueColor is not null)
        {
            buffers.Ensure((long)trueColor.Width * trueColor.Height);
            ImageExport.RenderColorRgb24(trueColor, lut, buffers.Rgb!, ct);
            return buffers.Rgb!;
        }

        long pixels = (long)image.Width * image.Height;
        buffers.Ensure(pixels);
        if (color)
        {
            return ImageExport.DevelopRgb24(
                image, frame, pattern, devLuts, null, ct, buffers.Rgb);
        }

        byte[] gray = buffers.Gray!;
        byte[] rgb = buffers.Rgb!;
        ImageExport.RenderGray8(image, frame, lut, gray, ct);
        for (long i = 0; i < pixels; i++)
        {
            byte v = gray[i];
            rgb[i * 3] = v;
            rgb[(i * 3) + 1] = v;
            rgb[(i * 3) + 2] = v;
        }

        return rgb;
    }

    /// <summary>デコード済みカラー画像に表示LUTを焼き込んでRGB24を作る。</summary>
    private static BitmapSource BakeColorFrame(
        ColorImage color, DisplayLut lut, CancellationToken ct)
    {
        byte[] rgb = ImageExport.RenderColorRgb24(color, lut, ct);
        return BitmapSource.Create(
            color.Width, color.Height, 96, 96, PixelFormats.Rgb24, null, rgb,
            color.Width * 3);
    }

    private static BitmapSource BakeFrame(
        RawImage image, int frame, bool color, BayerPattern pattern,
        DevelopLuts devLuts, DisplayLut lut, bool forceRgb, CancellationToken ct)
    {
        int width = image.Width;
        int height = image.Height;
        if (color)
        {
            byte[] rgb = ImageExport.DevelopRgb24(image, frame, pattern, devLuts, null, ct);
            return BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Rgb24, null, rgb, width * 3);
        }

        byte[] gray = ImageExport.RenderGray8(image, frame, lut, ct);
        if (!forceRgb)
        {
            return BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Gray8, null, gray, width);
        }

        // MJPEGはグレースケールJPEG非対応のプレーヤがあるためRGB化する
        var rgbGray = new byte[(long)width * height * 3];
        Parallel.For(0, height, y =>
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                byte v = gray[rowOffset + x];
                long o = ((long)rowOffset + x) * 3;
                rgbGray[o] = v;
                rgbGray[o + 1] = v;
                rgbGray[o + 2] = v;
            }
        });
        return BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Rgb24, null, rgbGray, width * 3);
    }

    private static byte[] EncodeJpegFrame(
        RawImage image, int frame, bool color, BayerPattern pattern,
        DevelopLuts devLuts, DisplayLut lut, ColorImage? trueColor, int jpegQuality,
        CancellationToken ct)
    {
        BitmapSource source = trueColor is not null
            ? BakeColorFrame(trueColor, lut, ct)
            : BakeFrame(image, frame, color, pattern, devLuts, lut, forceRgb: true, ct);
        var encoder = new JpegBitmapEncoder { QualityLevel = jpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void SaveBakedImage(
        RawImage image, int frame, bool color, BayerPattern pattern, DevelopLuts devLuts,
        DisplayLut lut, string path, bool jpeg, ColorImage? trueColor, CancellationToken ct)
    {
        BitmapSource source = trueColor is not null
            ? BakeColorFrame(trueColor, lut, ct)
            : BakeFrame(image, frame, color, pattern, devLuts, lut, forceRgb: false, ct);
        BitmapEncoder encoder = jpeg
            ? new JpegBitmapEncoder { QualityLevel = 95 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        // 途中で失敗しても壊れたファイルを出力フォルダに残さない
        // (同名の前回出力も、書き切るまでは差し替えない)
        AtomicFileWriter.Write(path, encoder.Save);
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
            if (_currentFormat.Hdr == HdrMode.None)
            {
                MessageBox.Show(this, "この表示モードにはHDR方式の指定が必要です(フォーマット変更…から設定)。",
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

        ViewportDisplayMode mode = index switch
        {
            1 => ViewportDisplayMode.BayerColor,
            2 => ViewportDisplayMode.ColorDevelop,
            3 => ViewportDisplayMode.ChannelSplit,
            _ => ViewportDisplayMode.Raw,
        };

        if (mode != ViewportDisplayMode.Raw && _currentFormat.Bayer == BayerPattern.None)
        {
            MessageBox.Show(this,
                "この表示モードにはBayerパターンの指定が必要です。\n" +
                "右パネルの「フォーマット」→「Bayer」でパターン(RGGB等)を選択してください。",
                "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        if (mode == ViewportDisplayMode.ColorDevelop)
        {
            EnsureDevelopLuts();
        }

        Viewport.SetDisplayMode(mode);
        if (mode != ViewportDisplayMode.Raw)
        {
            await EnsureBayerPyramidAsync();
        }
    }

    /// <summary>
    /// HDR派生ビューの計算結果を適用してよいか判定する。
    /// </summary>
    /// <remarks>
    /// 分割・合成は数秒かかり、その間も表示モードは操作できる。元画像の差し替えだけでなく
    /// モード変更も見ないと、ユーザーが選び直した表示を計算結果が後から上書きしてしまう。
    /// </remarks>
    /// <param name="expectedModeIndex">開始時の表示モード(分割=4、合成=5)。</param>
    /// <param name="source">計算元の画像。</param>
    /// <returns>適用してよければtrue。</returns>
    private bool CanApplyHdrView(int expectedModeIndex, RawImage source)
    {
        return DisplayModeCombo.SelectedIndex == expectedModeIndex
            && ReferenceEquals(source, _currentImage);
    }

    // HDR派生ビュー(分割・合成)の元にした元画像のフレーム。派生ビューがある間だけ参照する
    private int _hdrSourceFrame;

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
        if (_derivedImage is null)
        {
            _hdrSourceFrame = Viewport.Frame;
        }

        return _hdrSourceFrame;
    }

    private async Task EnterHdrSplitAsync()
    {
        if (RejectWhileOpening("HDR分割表示"))
        {
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        // 再生タイマーの連番送りと競合すると、Split中の画像が背後で破棄される
        using BusyScope busy = EnterBusy();
        RawImage image = _currentImage!;
        int sourceFrame = CaptureHdrSourceFrame();

        // フォーマットパネルで変更したBayerパターンやHDR方式を反映する
        // (image.Format は読み込み時のまま固定なので _currentFormat を渡す)
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

        if (!CanApplyHdrView(4, image))
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

        if (!CanApplyHdrView(4, image))
        {
            composite.Dispose();
            return;
        }

        await ApplyDerivedViewAsync(composite);
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

        if (RejectWhileOpening("HDR合成表示"))
        {
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        // 再生タイマーの連番送りと競合すると、Split/Merge中の画像が背後で破棄される
        using BusyScope busy = EnterBusy();
        RawImage image = _currentImage!;
        RawFormat format = _currentFormat!;
        int sourceFrame = CaptureHdrSourceFrame();
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
                        format.ExposureRatio, _blackPoint));
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

        if (!CanApplyHdrView(5, image))
        {
            quantized.Dispose();
            return;
        }

        await ApplyDerivedViewAsync(quantized);
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

    private async Task ApplyDerivedViewAsync(RawImage derived)
    {
        StopPlayback();
        _vm.HasSequence = false;

        // 旧派生画像を読んでいる描画・解析を止めてから破棄する
        CancelAnalysis();
        await Viewport.ClearImageAsync();

        // 旧派生画像のBayerピラミッドを残すと、EnsureBayerPyramidAsyncが
        // 非nullを見て早期returnし、新しい派生画像のピラミッドが二度と作られない
        _derivedBayerPyramid?.Dispose();
        _derivedBayerPyramid = null;
        _derivedImage?.Dispose();
        _derivedImage = derived;
        ClearDefectSource();
        _hdrFloatImage = null;
        _hdrFrameParams = null;
        _vm.HasRoi = false;

        // HDR合成は16bitになる。表示LUTの内部値は保ち、黒/白レベルの上限とコード値を換算し直す
        SyncLevelControlsToActiveBitDepth();
        Viewport.SetDisplayMode(ViewportDisplayMode.Raw);
        Viewport.SetImage(derived, derived.Format);
        Viewport.SetLut(BuildLut());
        UpdateNoiseWindowSource();
        UpdateProcessingBadge();
        RefreshHistogram(roi: null);
        _ = BuildDerivedPyramidAsync(derived);
    }

    private async Task BuildDerivedPyramidAsync(RawImage derived)
    {
        TilePyramid pyramid;
        try
        {
            pyramid = await TilePyramid.CreateAsync(derived);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ReferenceEquals(derived, _derivedImage))
        {
            Viewport.SetPyramid(pyramid);
        }
    }

    private async Task RestoreMainImageAsync()
    {
        // 派生画像を読んでいる描画・解析を止めてから破棄する
        CancelAnalysis();
        await Viewport.ClearImageAsync();
        _derivedImage?.Dispose();
        _derivedImage = null;
        ClearDefectSource();
        _hdrFloatImage = null;
        _hdrFrameParams = null;
        _vm.HdrTargetVisible = false;
        _vm.HasRoi = false;

        // HDR合成(16bit)から元画像のビット深度へ戻す(表示LUTの内部値は保つ)
        SyncLevelControlsToActiveBitDepth();
        _derivedBayerPyramid?.Dispose();
        _derivedBayerPyramid = null;
        Viewport.SetImage(_currentImage!, _currentFormat!);

        // 生成元フレームを偽らずに渡す(別フレーム産はレンダラ側で使われない)
        Viewport.SetPyramid(_mainPyramid, _mainPyramidFrame);
        Viewport.SetBayerPyramid(_mainBayerPyramid, _mainBayerPyramidFrame);
        Viewport.SetLut(BuildLut());
        UpdateNoiseWindowSource();
        UpdateProcessingBadge();
        DetectSequence();
        RefreshHistogram(roi: null);
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

        if (!ReferenceEquals(image, ActiveImage) || frame != Viewport.Frame
            || blackLevel != _blackPoint)
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

        ushort value;
        try
        {
            value = image.GetPixel(sourceX, sourceY, Viewport.Frame);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        _lastCursorX = sourceX;
        _lastCursorY = sourceY;
        _lastCursorInside = true;

        int code = value >> CurrentShift;
        int maxCode = (1 << format.BitDepth) - 1;

        // カラー画像はRGBとYCbCrを表示する
        if (_colorImage is { } color && sourceX < color.Width && sourceY < color.Height)
        {
            color.GetPixel(sourceX, sourceY, out ushort r16, out ushort g16, out ushort b16);
            int shift = 16 - color.BitDepth;
            int r = r16 >> shift;
            int g = g16 >> shift;
            int b = b16 >> shift;
            (int y, int cb, int cr) = ColorConvert.RgbToYCbCr(r, g, b, (1 << color.BitDepth) - 1);
            _vm.CursorStatusText =
                $"({sourceX}, {sourceY}) RGB=({r}, {g}, {b}) YCbCr=({y}, {cb}, {cr})";
            _vm.CursorOverlayText =
                $"({sourceX}, {sourceY})  RGB: {r} {g} {b}  YCbCr: {y} {cb} {cr}";
            return;
        }

        string channel = BayerHelper.GetLabel(
            BayerHelper.GetChannel(format.Bayer, sourceX, sourceY));
        _vm.CursorStatusText = $"({sourceX}, {sourceY}) raw={code}";
        _vm.CursorOverlayText = $"({sourceX}, {sourceY})  raw: {code} / {maxCode}  {channel}";
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
        _vm.HasSequence = count > 1;
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

        // 重い処理の実行中は画像を差し替えない(処理対象が背後で破棄されるため)。
        // スライダーが先に動いてしまっているので、表示中のフレームへ戻す
        if (count <= 1 || _sequenceBusy || _busyDepth > 0)
        {
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
            Viewport.SetDefectMarkers(null);
            if (_sequenceMode == SequenceMode.Frames)
            {
                // ピラミッドは生成元フレーム専用なので、フレームを移ったら捨てる。
                // 進行中の描画が読んでいる可能性があるため、切り離して描画停止を
                // 待ってからDisposeする(即Disposeすると読み出しがODEになり、
                // 後続のawaitで未処理例外としてUIまで届く)
                if (_mainBayerPyramid is not null)
                {
                    // フィールドはawait前にローカルへ退避して切っておく。
                    // await中に別経路が同じインスタンスを掴むとDisposeが二重になる
                    BayerPyramid pyramid = _mainBayerPyramid;
                    _mainBayerPyramid = null;
                    await Viewport.DetachBayerPyramidAsync();
                    pyramid.Dispose();
                }

                // await中にモーダル(保存・測定・演算)が開いたら差し替えない
                // (ファイル連番側と同じガード)
                if (_busyDepth > 0)
                {
                    return;
                }

                Viewport.SetFrame(index);
                _sequenceIndex = index;
            }
            else
            {
                string path = _sequenceFiles[index];
                bool isRaw = IsRawFile(path);
                RawFormat expectedFormat = _currentFormat!;
                RawImage image;
                ColorImage? color = null;
                int pageCount = 1;
                try
                {
                    if (isRaw)
                    {
                        image = await Task.Run(() => RawLoader.Load(path, expectedFormat));
                    }
                    else
                    {
                        // TIFF等の連番。フォーマットはファイル自身が持っている
                        DecodedImage decoded = await Task.Run(() => ImageFileLoader.Load(path));
                        image = decoded.Luminance;
                        color = decoded.Color;
                        pageCount = decoded.PageCount;
                        _valueNote = decoded.ValueNote;
                    }
                }
                catch (Exception)
                {
                    return; // 消えた/読めないファイルはスキップ
                }

                // await中にモーダル(保存・測定・演算)が開いていたら差し替えない。
                // モーダルのディスパッチャポンプ内でここが再開すると、処理対象の
                // 画像を背後で破棄してしまう
                if (_busyDepth > 0 || !ReferenceEquals(expectedFormat, _currentFormat))
                {
                    image.Dispose();
                    return;
                }

                // 通常のファイル連番では既存どおり同一サイズだけを送る。
                if (_currentImage is not null
                    && (image.Width != _currentImage.Width
                        || image.Height != _currentImage.Height))
                {
                    image.Dispose();
                    return;
                }

                RawFormat format = isRaw ? expectedFormat : image.Format;

                // ビット深度やカラー/グレーが変わると、黒レベル上限・画像情報・
                // フォーマットパネル・表示モードの前提が崩れる。追従させる
                bool layoutChanged = format.BitDepth != _currentFormat!.BitDepth
                    || (color is not null) != (_colorImage is not null)
                    || format.Bayer != _currentFormat.Bayer;

                // カラー画像は輝度と一緒に差し替える。片方だけだと前フレームの色が残る
                _colorImage = color;
                _vm.IsColorImage = color is not null;
                Viewport.SetColorImage(color);

                RawImage? old = await Viewport.ReplaceImageAsync(image, format, color: color);
                _currentImage = image;
                _currentFormat = format;
                _currentPath = path;
                _tiffStack = pageCount > 1
                    ? new TiffStackSource(path, pageCount, pageNavigationEnabled: false)
                    { BayerOverride = format.Bayer } : null;
                _tiffPageIndex = 0;
                _mainPyramid = null;
                _mainBayerPyramid?.Dispose();
                _mainBayerPyramid = null;
                _sequenceIndex = index;

                // 旧画像を読んでいる解析を止めてから破棄する
                // (走行中だとParallel.For内でObjectDisposedExceptionになる)
                CancelAnalysis();
                old?.Dispose();
                Title = $"RawAnalyzer — {Path.GetFileName(path)}{TiffPageNote}";
                if (layoutChanged)
                {
                    // 上限だけ変えると旧ビット深度のコード値が残り、次の操作で白点が飛ぶ
                    SyncLevelControlsToActiveBitDepth();
                    UpdateFormatPanel(format);
                    _histogram = null;
                    _channelHistograms = null;
                }

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
            }

        }
        finally
        {
            // 読み込み失敗・サイズ不一致などで送れなかった場合も、
            // スライダーとフレーム番号を実際の表示内容へ戻す
            UpdateSequenceUi();
            _sequenceBusy = false;
        }

        if (refreshAnalysis)
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
        StopPlayback();
        _busyDepth++;
        return new BusyScope(this, operation);
    }

    /// <summary>
    /// 通常の読み込みが確定待ちなら、表示画像を使う操作を始めずに理由を知らせる。
    /// </summary>
    /// <remarks>
    /// 確定待ちの間に操作を始めると、操作のダイアログ・進捗表示(Dispatcher の入れ子ポンプ)の
    /// 中で読み込みが確定し、操作の対象画像が背後で差し替え・破棄される。
    /// </remarks>
    /// <param name="operation">操作名(「保存」など)。</param>
    /// <returns>拒否した場合はtrue。</returns>
    private bool RejectWhileOpening(string operation)
    {
        if (!_imageGate.IsLoadPending)
        {
            return false;
        }

        MessageBox.Show(this,
            $"画像の読み込み中は{operation}を開始できません。読み込みの完了後にもう一度実行してください。",
            "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

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
                _defectWindow = null;
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

        if (RejectWhileOpening("欠陥画素検出"))
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

        if (progress.WasCanceled || result is null
            || _defectWindow is null || !ReferenceEquals(image, ActiveImage))
        {
            _defectWindow?.ResetRunButton();
            return;
        }

        _defectWindow.ShowResult(result, maxCode);

        // このリストの座標は「この画像・このフレーム」でのみ有効(補正時に検証する)
        _defectSourceImage = image;
        _defectSourceFrame = frame;
        Viewport.SetDefectMarkers(result.Defects);
    }

    private void OnDefectActivated(DefectPixel defect)
    {
        Viewport.CenterOn(defect.X, defect.Y, Math.Max(Viewport.Zoom, 32));
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

        if (RejectWhileOpening("ノイズ測定"))
        {
            _noiseWindow?.ResetRunButton();
            return;
        }

        using BusyScope busy = EnterBusy();
        int frame = Viewport.Frame;

        // 2枚目は常にフレーム0を読む。対象Aと同一ファイルをフレーム0表示中に
        // 指定すると完全に同一のデータ同士になり、σ_temporal=0という
        // 誤った測定値が無警告で出てしまう
        if (request.ReferencePath is not null && _currentPath is not null
            && _derivedImage is null && frame == 0
            && string.Equals(request.ReferencePath, _currentPath,
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "2枚目に対象Aと同じファイルが指定されています。同一データ同士の差分は" +
                "常に0になり、時間ノイズを測定できません。別撮りのフレームを指定するか、" +
                "マルチフレームファイルなら表示フレームを変えてください(2枚目はフレーム0を使います)。",
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
            MessageBox.Show(this, "HDR表示中は欠陥補正できません。Raw表示に戻してから実行してください。",
                "欠陥画素補正", MessageBoxButton.OK, MessageBoxImage.Information);
            _defectWindow?.ResetRunButton();
            return;
        }

        // HDR分割ビューで検出→Raw表示へ戻す→補正、やファイル送り後の適用は
        // 座標系/データが異なるのに配列範囲内に収まるため、例外にならず
        // 健全画素を黙って上書きしてしまう。検出時の画像・フレームと一致しない
        // リストの適用は拒否する
        if (!ReferenceEquals(_defectSourceImage, _currentImage)
            || _defectSourceFrame != Viewport.Frame)
        {
            MessageBox.Show(this,
                "この検出結果は現在表示中の画像・フレームのものではないため適用できません。" +
                "再度「検出実行」を行ってください。",
                "欠陥画素補正", MessageBoxButton.OK, MessageBoxImage.Information);
            _defectWindow?.ResetRunButton();
            return;
        }

        if (RejectWhileOpening("欠陥画素補正"))
        {
            _defectWindow?.ResetRunButton();
            return;
        }

        using BusyScope busy = EnterBusy();
        RawImage source = _currentImage;
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
        await ApplyProcessedImageAsync(
            corrected, $"欠陥補正 {count}px ({methodLabel})", closeDefectWindow: false);
        _defectWindow?.ResetRunButton();
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

        if (pattern == _currentFormat.Bayer)
        {
            return;
        }

        _currentFormat = _currentFormat with { Bayer = pattern };
        if (_derivedImage is null && _currentImage is not null)
        {
            Viewport.UpdateFormat(_currentFormat);
        }

        if (_correctionLabel is null && _currentPath is not null && IsRawFile(_currentPath))
        {
            RememberFileFormat(_currentPath, _currentFormat);
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
            Viewport.CenterOn(_lastCursorX, _lastCursorY, Math.Max(Viewport.Zoom, 32));
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
        if (_vm.SelectedFile is not { IsDirectory: false } entry)
        {
            return;
        }

        if (!IsRawFile(entry.FullPath))
        {
            OpenPath(entry.FullPath);
            return;
        }

        // 記憶フォーマットをスキップして必ずダイアログを表示する
        RawFormat? initial = TryGetRememberedFormat(
            entry.FullPath, SafeFileSize(entry.FullPath)) ?? _currentFormat;
        OpenPath(entry.FullPath, initial ?? new RawFormat { Width = 1920, Height = 1080 });
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
