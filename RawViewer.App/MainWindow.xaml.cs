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
using RawViewer.App.Controls;
using RawViewer.App.Rendering;
using RawViewer.App.Services;
using RawViewer.App.ViewModels;
using RawViewer.App.Views;
using RawViewer.Core;

namespace RawViewer.App;

/// <summary>
/// メインウィンドウ。ファイル読込・ピラミッド生成・LUT更新・解析のオーケストレーションを行う。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] SupportedExtensions = { ".raw", ".bin", ".tif", ".tiff" };

    private readonly MainViewModel _vm = new();
    private readonly FormatPresetStore _presetStore = new();
    private readonly RecentFilesStore _recentFiles = new();
    private readonly SessionStore _sessionStore = new();
    private readonly SessionState _session;
    private ColorMatrix _colorMatrix = ColorMatrix.Identity;
    private bool _updatingMatrixBoxes;

    private RawImage? _currentImage;
    private RawFormat? _currentFormat;
    private string? _currentPath;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _analysisCts;
    private HistogramResult? _histogram;
    private LineProfileWindow? _profileWindow;
    private ushort _blackPoint;
    private ushort _whitePoint = 65535;
    private bool _updatingSliders;

    // シーケンス再生
    private enum SequenceMode
    {
        None,
        Frames,
        Files,
    }

    private static readonly int[] PlaybackFpsValues = { 5, 10, 15, 24, 30 };
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
        InputBindings.Add(new KeyBinding(
            new Mvvm.RelayCommand(_ => OnOpenFileClick(this, new RoutedEventArgs())),
            new KeyGesture(Key.O, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(
            new Mvvm.RelayCommand(_ => OnSaveClick(this, new RoutedEventArgs())),
            new KeyGesture(Key.S, ModifierKeys.Control)));
        RebuildRecentMenu();
        Loaded += (_, _) =>
        {
            if (_vm.Files.Count == 0 && _session.LastFolder is { } folder
                && Directory.Exists(folder))
            {
                LoadFolder(folder, selectPath: null);
            }
        };
        Closing += (_, _) => SaveWindowPlacement();
        Closed += async (_, _) =>
        {
            _loadCts?.Cancel();
            _analysisCts?.Cancel();
            _profileWindow?.Close();
            await Viewport.ClearImageAsync();
            _derivedImage?.Dispose();
            _currentImage?.Dispose();
        };
    }

    /// <summary>表示中の画像(HDR派生ビューがあればそちら)。</summary>
    private RawImage? ActiveImage => _derivedImage ?? _currentImage;

    /// <summary>表示中の画像のフォーマット。</summary>
    private RawFormat? ActiveFormat => ActiveImage?.Format;

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
    }

    private void SaveWindowPlacement()
    {
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
        string key = SessionStore.NormalizeKey(path);
        _session.FileFormats.Remove(key);
        _session.FileFormats[key] = format;
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

    private void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Raw/TIFF (*.raw;*.bin;*.tif;*.tiff)|*.raw;*.bin;*.tif;*.tiff|すべてのファイル (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            LoadFolder(Path.GetDirectoryName(dialog.FileName)!, dialog.FileName);
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

    private void LoadFolder(string folder, string? selectPath)
    {
        folder = Path.GetFullPath(folder);
        var entries = new List<FileEntry>();
        try
        {
            // 上位ディレクトリへ移動する「..」+ サブディレクトリ + 対応ファイル
            string? parent = Path.GetDirectoryName(folder);
            if (!string.IsNullOrEmpty(parent))
            {
                entries.Add(new FileEntry("📁 ..", parent, IsDirectory: true));
            }

            foreach (string dir in Directory.EnumerateDirectories(folder)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new FileEntry(
                    $"📁 {Path.GetFileName(dir)}", dir, IsDirectory: true));
            }

            foreach (string path in Directory.EnumerateFiles(folder)
                .Where(p => SupportedExtensions.Contains(
                    Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new FileEntry(Path.GetFileName(path), path));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"フォルダを読み込めません: {ex.Message}", "RawViewer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _vm.FolderPath = $"📂 {folder}";
        _vm.Files.Clear();
        foreach (FileEntry entry in entries)
        {
            _vm.Files.Add(entry);
        }

        _session.LastFolder = folder;
        _sessionStore.Save(_session);

        if (selectPath is not null)
        {
            _vm.SelectedFile = _vm.Files.FirstOrDefault(
                f => string.Equals(f.FullPath, selectPath, StringComparison.OrdinalIgnoreCase));
        }
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
        if (_currentPath is not null && !IsTiff(_currentPath))
        {
            OpenPath(_currentPath, _currentFormat);
        }
    }

    private static bool IsTiff(string path)
    {
        string ext = Path.GetExtension(path);
        return string.Equals(ext, ".tif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".tiff", StringComparison.OrdinalIgnoreCase);
    }

    private async void OpenPath(string path, RawFormat? initialFormat = null)
    {
        RawFormat? format = null;
        if (!IsTiff(path))
        {
            // 同じファイルを開き直すときは記憶したフォーマットでダイアログをスキップ
            // (「変更…」から開いた場合 initialFormat が渡されるためダイアログを出す)
            RawFormat? remembered = initialFormat is null
                ? TryGetRememberedFormat(path, new FileInfo(path).Length)
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

        _loadCts?.Cancel();
        _analysisCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        _vm.ImageInfoText = "読込中…";

        RawImage image;
        try
        {
            image = await Task.Run(
                () => IsTiff(path) ? TiffLoader.Load(path) : RawLoader.Load(path, format!),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _vm.ImageInfoText = "読込失敗";
            MessageBox.Show(this, $"読み込みに失敗しました: {ex.Message}", "RawViewer",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (cts.IsCancellationRequested)
        {
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
        _vm.HdrTargetVisible = false;
        _currentImage?.Dispose();
        _currentImage = image;
        _currentFormat = image.Format;
        _currentPath = path;
        _histogram = null;
        _vm.HasRoi = false;
        _vm.BlackLevelMax = (1 << image.Format.BitDepth) - 1;
        ResetDisplayParameters();

        Title = $"RawViewer — {Path.GetFileName(path)}";
        _recentFiles.Add(path);
        RebuildRecentMenu();
        if (!IsTiff(path))
        {
            RememberFileFormat(path, image.Format);
        }

        UpdateFormatPanel(image.Format);
        long fileSize = new FileInfo(path).Length;
        _vm.ImageInfoText =
            $"{image.Width}×{image.Height} · {image.Format.BitDepth}bit · {fileSize / (1024.0 * 1024.0):F1} MB"
            + (image.FrameCount > 1 ? $" · {image.FrameCount}fr" : "");
        _vm.HasImage = true;

        DisplayModeCombo.SelectedIndex = 0;
        Viewport.SetDisplayMode(ViewportDisplayMode.Raw);
        Viewport.SetImage(image, image.Format);
        Viewport.SetLut(BuildLut());
        UpdateDevelopLuts();
        DetectSequence();

        RefreshHistogram(roi: null);
        await BuildPyramidAsync(image, cts.Token);
    }

    private async Task BuildPyramidAsync(RawImage image, CancellationToken ct)
    {
        TilePyramid pyramid;
        try
        {
            pyramid = await TilePyramid.CreateAsync(image, 0, cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested || !ReferenceEquals(image, _currentImage))
        {
            return;
        }

        _mainPyramid = pyramid;
        if (_derivedImage is null)
        {
            Viewport.SetPyramid(pyramid);
        }
    }

    private void UpdateFormatPanel(RawFormat format)
    {
        string packing = format.Packing == BitPacking.Lsb ? "下詰め" : "上詰め";
        _vm.FmtBitDepthText = $"{format.BitDepth}bit {packing}";
        _vm.FmtEndianText = format.Endianness == Endianness.Little ? "Little" : "Big";
        _vm.FmtBayerText = format.Bayer == BayerPattern.None
            ? "なし"
            : format.Bayer.ToString().ToUpperInvariant();
        _vm.FmtHdrText = format.Hdr switch
        {
            HdrMode.Dol => $"DOL {format.HdrStages}段 (露光比 {format.ExposureRatio:F0})",
            HdrMode.Staggered =>
                $"Staggered {format.HdrStages}段 (露光比 {format.ExposureRatio:F0})",
            _ => "なし",
        };
    }

    // ---- ヒストグラム・ROI解析 ----

    private async void RefreshHistogram(RegionOfInterest? roi)
    {
        if (ActiveImage is null)
        {
            return;
        }

        _analysisCts?.Cancel();
        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        RawImage image = ActiveImage;
        int frame = Viewport.Frame;

        HistogramResult result;
        RegionStatistics? exactStats = null;
        try
        {
            result = await Task.Run(
                () => ImageAnalysis.ComputeHistogram(image, frame, roi, cancellationToken: cts.Token),
                cts.Token);
            if (roi is { } r)
            {
                exactStats = await Task.Run(
                    () => ImageAnalysis.ComputeStatistics(image, frame, r, cts.Token), cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (cts.IsCancellationRequested || !ReferenceEquals(image, ActiveImage))
        {
            return;
        }

        _histogram = result;
        _vm.HistogramIsSampled = result.IsSampled;
        RegionStatistics stats = exactStats ?? result.Statistics;
        _vm.HistMeanSigmaText = $"{stats.Mean:F1} / {stats.Sigma:F1}";
        _vm.HistMinMaxText = $"{stats.Min} / {stats.Max}";
        RedrawHistogram();

        if (roi is { } roiRect && exactStats is { } es)
        {
            _vm.RoiOverlayText =
                $"ROI: {roiRect.Width}×{roiRect.Height}  mean {es.Mean:F1}  σ {es.Sigma:F1}";
            _vm.HasRoi = true;
        }
    }

    private void RedrawHistogram()
    {
        if (_histogram is null)
        {
            _vm.HistogramSource = null;
            return;
        }

        _vm.HistogramSource = RenderHistogram(_histogram.Bins, _vm.HistogramIsLog);
    }

    private static ImageSource RenderHistogram(uint[] bins, bool logScale)
    {
        const int width = 210;
        const int height = 70;
        var columns = new uint[width];
        int binsPerColumn = bins.Length / width + 1;
        for (int i = 0; i < bins.Length; i++)
        {
            int column = Math.Min(i / binsPerColumn, width - 1);
            columns[column] += bins[i];
        }

        uint maxCount = columns.Max();
        double maxScale = logScale ? Math.Log(1 + maxCount) : maxCount;
        var pixels = new byte[width * height * 4];
        for (int x = 0; x < width; x++)
        {
            double value = logScale ? Math.Log(1 + columns[x]) : columns[x];
            int barHeight = maxScale > 0 ? (int)(value / maxScale * (height - 2)) : 0;
            for (int y = height - barHeight; y < height; y++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = 0x85;
                pixels[offset + 1] = 0x8A;
                pixels[offset + 2] = 0x8A;
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
        else
        {
            if (Viewport.InteractionMode == ViewportInteractionMode.RoiSelect)
            {
                Viewport.InteractionMode = ViewportInteractionMode.Pan;
            }

            Viewport.ClearRoi();
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

    private async void OnProfilePointClicked(object? sender, CursorPixelEventArgs e)
    {
        if (ActiveImage is null || ActiveFormat is null)
        {
            return;
        }

        RawImage image = ActiveImage;
        int frame = Viewport.Frame;
        ushort[] row;
        ushort[] column;
        try
        {
            (row, column) = await Task.Run(() => (
                ImageAnalysis.ExtractRowProfile(image, frame, e.Y),
                ImageAnalysis.ExtractColumnProfile(image, frame, e.X)));
        }
        catch (Exception)
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
            _profileWindow.Closed += (_, _) => _profileWindow = null;
            _profileWindow.Show();
        }

        int maxCode = (1 << ActiveFormat!.BitDepth) - 1;
        _profileWindow.SetProfiles(row, column, e.X, e.Y, maxCode);
        _profileWindow.Activate();
    }

    // ---- 表示調整 (LUT) ----

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.HistogramIsLog))
        {
            RedrawHistogram();
            return;
        }

        if (_updatingSliders)
        {
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.Gain)
            or nameof(MainViewModel.Gamma)
            or nameof(MainViewModel.Contrast)
            or nameof(MainViewModel.BlackLevel))
        {
            if (e.PropertyName == nameof(MainViewModel.BlackLevel))
            {
                _blackPoint = (ushort)Math.Min(65535, (long)_vm.BlackLevel << CurrentShift);
            }

            if (_vm.HasImage)
            {
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
                if (e.PropertyName is nameof(MainViewModel.Gamma)
                    or nameof(MainViewModel.BlackLevel))
                {
                    UpdateDevelopLuts();
                }
            }
        }

        if (e.PropertyName is nameof(MainViewModel.WbGainR) or nameof(MainViewModel.WbGainB)
            && _vm.HasImage)
        {
            UpdateDevelopLuts();
        }
    }

    private void UpdateDevelopLuts()
    {
        double gamma = _vm.Gamma > 0 ? _vm.Gamma : 1.0;
        Viewport.SetDevelopLuts(DevelopLuts.Create(new DevelopParameters(
            _blackPoint, _vm.WbGainR, _vm.WbGainB, gamma,
            _colorMatrix.IsIdentity ? null : _colorMatrix)));
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
            if (!double.TryParse(boxes[i].Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out values[i]))
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
        List<string> recent = _recentFiles.Load().Where(File.Exists).ToList();
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
            item.Click += (_, _) =>
            {
                LoadFolder(Path.GetDirectoryName(captured)!, captured);
                OpenPath(captured);
            };
            RecentMenu.Items.Add(item);
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
        if (_histogram is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("raw_code").Append(separator).Append("count").AppendLine();
        uint[] bins = _histogram.Bins;
        for (int i = 0; i < bins.Length; i++)
        {
            sb.Append(i).Append(separator).Append(bins[i]).AppendLine();
        }

        return sb.ToString();
    }

    private void OnHistogramCopyClick(object sender, RoutedEventArgs e)
    {
        string? table = BuildHistogramTable('\t');
        if (table is not null)
        {
            Clipboard.SetText(table);
            _vm.ImageInfoText = "ヒストグラムをクリップボードへコピーしました";
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
        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, table, Encoding.UTF8);
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

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        ShowInfoWindow("バージョン情報",
            $"RawViewer {version}\n\n" +
            "イメージセンサRaw画像評価アプリ\n" +
            "10億画素(2GB)対応 / DOL・Staggered HDR / Bayer現像\n\n" +
            $".NET {Environment.Version} / WPF");
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

        var dialog = new SaveDialog(
            ActiveImage.Format.TotalPixels, allowFloatRaw: _hdrFloatImage is not null)
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
            FileName = Path.GetFileNameWithoutExtension(_currentPath ?? "image") + extension,
        };
        if (fileDialog.ShowDialog(this) != true)
        {
            return;
        }

        ExecuteSave(choice, fileDialog.FileName);
    }

    private void ExecuteSave(SaveChoice choice, string path)
    {
        RawImage image = ActiveImage!;
        HdrImage? hdrFloat = _hdrFloatImage;
        DisplayLut lut = BuildLut();
        ViewportDisplayMode mode = Viewport.DisplayMode;
        BayerPattern pattern = image.Format.Bayer;
        double gamma = _vm.Gamma > 0 ? _vm.Gamma : 1.0;
        var devLuts = DevelopLuts.Create(new DevelopParameters(
            _blackPoint, _vm.WbGainR, _vm.WbGainB, gamma));

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
                        if (image.Format.TotalPixels > RawLoader.DefaultInMemoryPixelThreshold)
                        {
                            // 巨大画像は自前ライタで行単位ストリーミング
                            TiffWriter.SaveGray16(image, 0, path, progress, ct);
                        }
                        else
                        {
                            SaveWithWic(image, path, choice.Format, mode, pattern,
                                lut, devLuts, progress, ct);
                        }

                        break;
                    default:
                        SaveWithWic(image, path, choice.Format, mode, pattern,
                            lut, devLuts, progress, ct);
                        break;
                }
            }, ct));

        if (result.Error is not null)
        {
            MessageBox.Show(this, $"保存に失敗しました: {result.Error.Message}", "RawViewer",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else if (!result.WasCanceled)
        {
            _vm.ImageInfoText = $"保存完了: {Path.GetFileName(path)}";
        }
    }

    private static void SaveWithWic(
        RawImage image, string path, SaveFormat format, ViewportDisplayMode mode,
        BayerPattern pattern, DisplayLut lut, DevelopLuts devLuts,
        IProgress<double> progress, CancellationToken ct)
    {
        int width = image.Width;
        int height = image.Height;
        try
        {
            BitmapSource source;
            switch (format)
            {
                case SaveFormat.Tiff16:
                case SaveFormat.Png16:
                {
                    var pixels = new ushort[(long)width * height];
                    for (int y = 0; y < height; y++)
                    {
                        ct.ThrowIfCancellationRequested();
                        image.CopyRegion(0, 0, y, width, 1, pixels.AsSpan(y * width, width));
                        if ((y & 511) == 0)
                        {
                            progress.Report(0.5 * y / height);
                        }
                    }

                    source = BitmapSource.Create(
                        width, height, 96, 96, PixelFormats.Gray16, null, pixels, width * 2);
                    break;
                }

                default:
                {
                    // 8bit系は現在の表示(現像モードなら現像結果)を焼き込む
                    bool color = mode is ViewportDisplayMode.ColorDevelop
                        or ViewportDisplayMode.BayerColor
                        && pattern != BayerPattern.None;
                    if (color)
                    {
                        byte[] rgb = ImageExport.DevelopRgb24(
                            image, 0, pattern, devLuts,
                            new Progress<double>(p => progress.Report(p * 0.7)), ct);
                        source = BitmapSource.Create(
                            width, height, 96, 96, PixelFormats.Rgb24, null, rgb, width * 3);
                    }
                    else
                    {
                        byte[] gray = ImageExport.RenderGray8(image, 0, lut, ct);
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
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            encoder.Save(stream);
            progress.Report(1.0);
        }
        catch (Exception)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    // ---- バッチ現像 / 動画書き出し ----

    private void OnBatchExportClick(object sender, RoutedEventArgs e)
    {
        if (_currentImage is null || _currentFormat is null || _currentPath is null)
        {
            return;
        }

        if (IsTiff(_currentPath))
        {
            MessageBox.Show(this, "バッチ書き出しはrawファイルを開いた状態で実行してください。",
                "バッチ書き出し", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RawFormat format = _currentFormat;
        long size = SafeFileSize(_currentPath);
        string extension = Path.GetExtension(_currentPath);
        List<string> targets = _vm.Files
            .Where(f => !f.IsDirectory && string.Equals(
                Path.GetExtension(f.FullPath), extension, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FullPath)
            .Where(p => SafeFileSize(p) == size)
            .OrderBy(p => p, NaturalOrderComparer.Instance)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        string folder = Path.GetDirectoryName(_currentPath)!;
        var dialog = new BatchExportDialog(targets.Count, Path.Combine(folder, "export"))
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        BatchChoice choice = dialog.Result;
        if (choice.Format != BatchFormat.Tiff16
            && format.TotalPixels > RawLoader.DefaultInMemoryPixelThreshold)
        {
            MessageBox.Show(this, "1億画素を超える画像の現像バッチはサポートされていません(TIFF16は可)。",
                "バッチ書き出し", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExecuteBatch(targets, format, choice);
    }

    private void ExecuteBatch(List<string> targets, RawFormat format, BatchChoice choice)
    {
        BayerPattern pattern = format.Bayer;
        bool color = pattern != BayerPattern.None;
        DisplayLut lut = BuildLut();
        double gamma = _vm.Gamma > 0 ? _vm.Gamma : 1.0;
        var devLuts = DevelopLuts.Create(new DevelopParameters(
            _blackPoint, _vm.WbGainR, _vm.WbGainB, gamma,
            _colorMatrix.IsIdentity ? null : _colorMatrix));
        int width = format.Width;
        int height = format.Height;
        string aviPath = Path.Combine(
            choice.OutputFolder,
            Path.GetFileNameWithoutExtension(targets[0]) + "_seq.avi");

        ProgressWindow result = ProgressWindow.Run(
            this,
            $"バッチ書き出し中 ({targets.Count}件)",
            (progress, ct) => Task.Run(() =>
            {
                Directory.CreateDirectory(choice.OutputFolder);
                AviMjpegWriter? avi = null;
                try
                {
                    if (choice.Format == BatchFormat.AviMjpeg)
                    {
                        avi = new AviMjpegWriter(aviPath, width, height, choice.Fps);
                    }

                    for (int i = 0; i < targets.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string file = targets[i];
                        string baseName = Path.GetFileNameWithoutExtension(file);
                        using RawImage image = RawLoader.Load(file, format);
                        switch (choice.Format)
                        {
                            case BatchFormat.Tiff16:
                                TiffWriter.SaveGray16(
                                    image, 0,
                                    Path.Combine(choice.OutputFolder, baseName + ".tif"),
                                    null, ct);
                                break;
                            case BatchFormat.AviMjpeg:
                                // マルチフレームファイルは全フレームを動画化する
                                for (int frame = 0; frame < image.FrameCount; frame++)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    avi!.AddFrame(EncodeJpegFrame(
                                        image, frame, color, pattern, devLuts, lut, ct));
                                }

                                break;
                            default:
                                SaveBakedImage(
                                    image, color, pattern, devLuts, lut,
                                    Path.Combine(choice.OutputFolder,
                                        baseName + (choice.Format == BatchFormat.Jpeg8 ? ".jpg" : ".png")),
                                    choice.Format == BatchFormat.Jpeg8, ct);
                                break;
                        }

                        progress.Report((double)(i + 1) / targets.Count);
                    }

                    avi?.Finish();
                }
                finally
                {
                    avi?.Dispose();
                }
            }, ct));

        if (result.WasCanceled && choice.Format == BatchFormat.AviMjpeg)
        {
            try
            {
                if (File.Exists(aviPath))
                {
                    File.Delete(aviPath);
                }
            }
            catch (IOException)
            {
            }
        }

        if (result.Error is not null)
        {
            MessageBox.Show(this, $"バッチ書き出しに失敗しました: {result.Error.Message}",
                "バッチ書き出し", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else if (!result.WasCanceled)
        {
            _vm.ImageInfoText = choice.Format == BatchFormat.AviMjpeg
                ? $"動画書き出し完了: {Path.GetFileName(aviPath)}"
                : $"バッチ書き出し完了: {targets.Count}件 → {choice.OutputFolder}";
        }
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
        DevelopLuts devLuts, DisplayLut lut, CancellationToken ct)
    {
        BitmapSource source = BakeFrame(image, frame, color, pattern, devLuts, lut,
            forceRgb: true, ct);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void SaveBakedImage(
        RawImage image, bool color, BayerPattern pattern, DevelopLuts devLuts,
        DisplayLut lut, string path, bool jpeg, CancellationToken ct)
    {
        BitmapSource source = BakeFrame(image, 0, color, pattern, devLuts, lut,
            forceRgb: false, ct);
        BitmapEncoder encoder = jpeg
            ? new JpegBitmapEncoder { QualityLevel = 95 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);
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
                    "RawViewer", MessageBoxButton.OK, MessageBoxImage.Information);
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

        // 通常モード: HDR派生ビューから復帰
        if (_derivedImage is not null)
        {
            RestoreMainImage();
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
            MessageBox.Show(this, "この表示モードにはBayerパターンの指定が必要です。",
                "RawViewer", MessageBoxButton.OK, MessageBoxImage.Information);
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        Viewport.SetDisplayMode(mode);
    }

    private async Task EnterHdrSplitAsync()
    {
        RawImage image = _currentImage!;
        IReadOnlyList<RawImage> frames;
        try
        {
            frames = await Task.Run(() => HdrSplitter.Split(image));
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "HDR分割", MessageBoxButton.OK, MessageBoxImage.Warning);
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        if (!ReferenceEquals(image, _currentImage))
        {
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
            RawFormat format = image.Format with
            {
                Width = compositeWidth,
                Height = subHeight,
                FrameCount = 1,
                Hdr = HdrMode.None,
            };
            return RawImage.FromPixels(format, pixels);
        });
        foreach (RawImage frame in frames)
        {
            frame.Dispose();
        }

        if (!ReferenceEquals(image, _currentImage))
        {
            composite.Dispose();
            return;
        }

        ApplyDerivedView(composite);
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
        RawImage image = _currentImage!;
        RawFormat format = _currentFormat!;
        HdrImage merged;
        RawImage quantized;
        try
        {
            (merged, quantized) = await Task.Run(() =>
            {
                IReadOnlyList<RawImage> frames = HdrSplitter.Split(image);
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
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "HDR合成", MessageBoxButton.OK, MessageBoxImage.Warning);
            DisplayModeCombo.SelectedIndex = 0;
            return;
        }

        if (!ReferenceEquals(image, _currentImage))
        {
            quantized.Dispose();
            return;
        }

        ApplyDerivedView(quantized);
        _hdrFloatImage = merged;
        _vm.HdrTargetVisible = false;
        _vm.LevelOverlayText =
            $"HDR合成表示 (フルスケール {merged.FullScale:F0}, ゲイン=露出)";
    }

    private void ApplyDerivedView(RawImage derived)
    {
        StopPlayback();
        _vm.HasSequence = false;
        _derivedImage?.Dispose();
        _derivedImage = derived;
        _hdrFloatImage = null;
        _hdrFrameParams = null;
        _vm.HasRoi = false;
        Viewport.SetDisplayMode(ViewportDisplayMode.Raw);
        Viewport.SetImage(derived, derived.Format);
        Viewport.SetLut(BuildLut());
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

    private void RestoreMainImage()
    {
        _derivedImage?.Dispose();
        _derivedImage = null;
        _hdrFloatImage = null;
        _hdrFrameParams = null;
        _vm.HdrTargetVisible = false;
        _vm.HasRoi = false;
        Viewport.SetImage(_currentImage!, _currentFormat!);
        Viewport.SetPyramid(_mainPyramid);
        Viewport.SetLut(BuildLut());
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
        if (_currentImage is null || _currentFormat is null
            || _currentFormat.Bayer == BayerPattern.None)
        {
            return;
        }

        RawImage image = _currentImage;
        BayerPattern pattern = _currentFormat.Bayer;
        WhiteBalanceGains gains;
        try
        {
            gains = await Task.Run(() => WhiteBalance.ComputeGrayWorld(image, 0, pattern));
        }
        catch (Exception)
        {
            return;
        }

        if (!ReferenceEquals(image, _currentImage))
        {
            return;
        }

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
        if (_currentImage is null || _currentFormat is null
            || _currentFormat.Bayer == BayerPattern.None)
        {
            return;
        }

        WhiteBalanceGains gains = WhiteBalance.ComputeSpotGains(
            _currentImage, 0, _currentFormat.Bayer, e.X, e.Y);
        _vm.WbGainR = Math.Clamp(gains.GainR, 0.5, 4.0);
        _vm.WbGainB = Math.Clamp(gains.GainB, 0.5, 4.0);
    }

    private DisplayLut BuildLut()
    {
        return DisplayLut.Create(new DisplayParameters(
            _blackPoint, _whitePoint, _vm.Gain, _vm.Gamma, _vm.Contrast));
    }

    private void ResetDisplayParameters()
    {
        _updatingSliders = true;
        _vm.Gain = 1.0;
        _vm.Gamma = 1.0;
        _vm.Contrast = 1.0;
        _vm.BlackLevel = 0;
        _updatingSliders = false;
        _blackPoint = 0;
        _whitePoint = 65535;
    }

    private void OnResetDisplayClick(object sender, RoutedEventArgs e)
    {
        ResetDisplayParameters();
        if (_vm.HasImage)
        {
            Viewport.SetLut(BuildLut());
        }
    }

    private void OnAutoContrastClick(object sender, RoutedEventArgs e)
    {
        if (_histogram is null || _currentFormat is null)
        {
            return;
        }

        uint[] bins = _histogram.Bins;
        long total = bins.Sum(c => (long)c);
        if (total == 0)
        {
            return;
        }

        long clip = (long)(total * 0.0035);
        long acc = 0;
        int blackCode = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            acc += bins[i];
            if (acc > clip)
            {
                blackCode = i;
                break;
            }
        }

        acc = 0;
        int whiteCode = bins.Length - 1;
        for (int i = bins.Length - 1; i >= 0; i--)
        {
            acc += bins[i];
            if (acc > clip)
            {
                whiteCode = i;
                break;
            }
        }

        if (whiteCode <= blackCode)
        {
            return;
        }

        int shift = CurrentShift;
        _blackPoint = (ushort)(blackCode << shift);
        _whitePoint = (ushort)Math.Min(65535, ((long)whiteCode << shift) | ((1L << shift) - 1));
        _updatingSliders = true;
        _vm.BlackLevel = blackCode;
        _updatingSliders = false;
        Viewport.SetLut(BuildLut());
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
            _vm.CursorStatusText = "";
            _vm.CursorOverlayText = "";
            return;
        }

        // チャネル分割表示ではタイル座標を元画像座標へ写像する
        int sourceX = e.X;
        int sourceY = e.Y;
        if (Viewport.DisplayMode == ViewportDisplayMode.ChannelSplit)
        {
            int evenW = image.Width & ~1;
            int evenH = image.Height & ~1;
            if (e.X >= evenW || e.Y >= evenH)
            {
                return;
            }

            (sourceX, sourceY) = BayerSplit.MapTiledToSource(e.X, e.Y, evenW, evenH);
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

        int code = value >> CurrentShift;
        int maxCode = (1 << format.BitDepth) - 1;
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
        _ => 0,
    };

    private void DetectSequence()
    {
        StopPlayback();
        _sequenceMode = SequenceMode.None;
        _sequenceFiles = new List<string>();
        _sequenceIndex = 0;

        if (_currentImage is not null && _derivedImage is null)
        {
            if (_currentImage.FrameCount > 1)
            {
                _sequenceMode = SequenceMode.Frames;
                _sequenceIndex = Viewport.Frame;
            }
            else if (_currentPath is not null && !IsTiff(_currentPath))
            {
                // 同一フォルダ・同一拡張子・同一サイズのファイル群をバーチャルスタックとみなす
                long size = SafeFileSize(_currentPath);
                string extension = Path.GetExtension(_currentPath);
                List<string> files = _vm.Files
                    .Where(f => !f.IsDirectory && string.Equals(
                        Path.GetExtension(f.FullPath), extension, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.FullPath)
                    .Where(p => SafeFileSize(p) == size)
                    .OrderBy(p => p, NaturalOrderComparer.Instance)
                    .ToList();
                if (files.Count > 1 && size > 0)
                {
                    _sequenceMode = SequenceMode.Files;
                    _sequenceFiles = files;
                    _sequenceIndex = Math.Max(0, files.FindIndex(
                        p => string.Equals(p, _currentPath, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }

        UpdateSequenceUi();
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
        _vm.SequenceLabel = count > 1 ? $"{_sequenceIndex + 1} / {count}" : "";
        _updatingSequenceUi = false;
    }

    private async Task ShowSequenceIndexAsync(int index, bool refreshAnalysis)
    {
        int count = SequenceCount;
        if (count <= 1 || _sequenceBusy)
        {
            return;
        }

        index = ((index % count) + count) % count;
        if (index == _sequenceIndex)
        {
            return;
        }

        _sequenceBusy = true;
        try
        {
            if (_sequenceMode == SequenceMode.Frames)
            {
                Viewport.SetFrame(index);
                _sequenceIndex = index;
            }
            else
            {
                string path = _sequenceFiles[index];
                RawFormat format = _currentFormat!;
                RawImage image;
                try
                {
                    image = await Task.Run(() => RawLoader.Load(path, format));
                }
                catch (Exception)
                {
                    return; // 消えた/読めないファイルはスキップ
                }

                RawImage? old = await Viewport.ReplaceImageAsync(image, format);
                _currentImage = image;
                _currentPath = path;
                _mainPyramid = null;
                _sequenceIndex = index;
                old?.Dispose();
                Title = $"RawViewer — {Path.GetFileName(path)}";
                _vm.SelectedFile = _vm.Files.FirstOrDefault(f => string.Equals(
                    f.FullPath, path, StringComparison.OrdinalIgnoreCase));
            }

            UpdateSequenceUi();
        }
        finally
        {
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

        if (_sequenceMode == SequenceMode.Files && _mainPyramid is null)
        {
            _ = BuildPyramidAsync(_currentImage, _loadCts?.Token ?? CancellationToken.None);
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
            int fps = PlaybackFpsValues[Math.Clamp(FpsCombo.SelectedIndex, 0, 4)];
            _playTimer.Interval = TimeSpan.FromSeconds(1.0 / fps);
            _playTimer.Start();
        }
        else
        {
            StopPlayback();
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
        if (!_sequenceBusy)
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

    // ---- ゼブラ・ドラッグ&ドロップ ----

    private void OnZebraToggleChanged(object sender, RoutedEventArgs e)
    {
        Viewport.SetZebra(ZebraToggle.IsChecked == true);
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
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
            LoadFolder(Path.GetDirectoryName(path)!, path);
            OpenPath(path);
        }
    }
}
