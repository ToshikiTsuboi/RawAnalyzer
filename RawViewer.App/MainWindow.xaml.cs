using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RawViewer.App.Controls;
using RawViewer.App.Rendering;
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

    /// <summary>メインウィンドウを生成する。</summary>
    public MainWindow()
    {
        InitializeComponent();
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
        Closed += async (_, _) =>
        {
            _loadCts?.Cancel();
            _analysisCts?.Cancel();
            _profileWindow?.Close();
            await Viewport.ClearImageAsync();
            _currentImage?.Dispose();
        };
    }

    private int CurrentShift => 16 - (_currentFormat?.BitDepth ?? 16);

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
        _vm.FolderPath = $"📂 {folder}";
        _vm.Files.Clear();
        try
        {
            foreach (string path in Directory.EnumerateFiles(folder)
                .Where(p => SupportedExtensions.Contains(
                    Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                _vm.Files.Add(new FileEntry(Path.GetFileName(path), path));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"フォルダを読み込めません: {ex.Message}", "RawViewer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (selectPath is not null)
        {
            _vm.SelectedFile = _vm.Files.FirstOrDefault(
                f => string.Equals(f.FullPath, selectPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void OnFileListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedFile is not null)
        {
            OpenPath(_vm.SelectedFile.FullPath);
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
        _currentImage?.Dispose();
        _currentImage = image;
        _currentFormat = image.Format;
        _currentPath = path;
        _histogram = null;
        _vm.HasRoi = false;
        _vm.BlackLevelMax = (1 << image.Format.BitDepth) - 1;
        ResetDisplayParameters();

        Title = $"RawViewer — {Path.GetFileName(path)}";
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

        Viewport.SetPyramid(pyramid);
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
            HdrMode.Dol => $"DOL (露光比 {format.ExposureRatio:F0})",
            HdrMode.Staggered => $"Staggered (露光比 {format.ExposureRatio:F0})",
            _ => "なし",
        };
    }

    // ---- ヒストグラム・ROI解析 ----

    private async void RefreshHistogram(RegionOfInterest? roi)
    {
        if (_currentImage is null)
        {
            return;
        }

        _analysisCts?.Cancel();
        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        RawImage image = _currentImage;

        HistogramResult result;
        RegionStatistics? exactStats = null;
        try
        {
            result = await Task.Run(
                () => ImageAnalysis.ComputeHistogram(image, 0, roi, cancellationToken: cts.Token),
                cts.Token);
            if (roi is { } r)
            {
                exactStats = await Task.Run(
                    () => ImageAnalysis.ComputeStatistics(image, 0, r, cts.Token), cts.Token);
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

        if (cts.IsCancellationRequested || !ReferenceEquals(image, _currentImage))
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
        if (_currentImage is null || _currentFormat is null)
        {
            return;
        }

        RawImage image = _currentImage;
        ushort[] row;
        ushort[] column;
        try
        {
            (row, column) = await Task.Run(() => (
                ImageAnalysis.ExtractRowProfile(image, 0, e.Y),
                ImageAnalysis.ExtractColumnProfile(image, 0, e.X)));
        }
        catch (Exception)
        {
            return;
        }

        if (!ReferenceEquals(image, _currentImage))
        {
            return;
        }

        if (_profileWindow is null)
        {
            _profileWindow = new LineProfileWindow { Owner = this };
            _profileWindow.Closed += (_, _) => _profileWindow = null;
            _profileWindow.Show();
        }

        int maxCode = (1 << _currentFormat.BitDepth) - 1;
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
            _blackPoint, _vm.WbGainR, _vm.WbGainB, gamma)));
    }

    // ---- 表示モード・ホワイトバランス ----

    private void OnDisplayModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Viewport is null || _currentFormat is null)
        {
            return;
        }

        ViewportDisplayMode mode = DisplayModeCombo.SelectedIndex switch
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
        if (_currentImage is null || _currentFormat is null || !e.IsInsideImage)
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
            int evenW = _currentImage.Width & ~1;
            int evenH = _currentImage.Height & ~1;
            if (e.X >= evenW || e.Y >= evenH)
            {
                return;
            }

            (sourceX, sourceY) = BayerSplit.MapTiledToSource(e.X, e.Y, evenW, evenH);
        }

        ushort value;
        try
        {
            value = _currentImage.GetPixel(sourceX, sourceY);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        int code = value >> CurrentShift;
        int maxCode = (1 << _currentFormat.BitDepth) - 1;
        string channel = BayerHelper.GetLabel(
            BayerHelper.GetChannel(_currentFormat.Bayer, sourceX, sourceY));
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
}
