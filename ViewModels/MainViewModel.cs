// ViewModels/MainViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgProcessWpfApp.Helpers;
using ImgProcessWpfApp.Models;
using Microsoft.Win32;

namespace ImgProcessWpfApp.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        // ───────── 左ペイン ─────────
        public ObservableCollection<FileSystemItem> DirectoryRoots { get; } = new();
        public ObservableCollection<FileItem> Files { get; } = new();

        private FileItem? _selectedFile;
        public FileItem? SelectedFile
        {
            get => _selectedFile;
            set
            {
                if (SetProperty(ref _selectedFile, value))
                {
                    PreviewCommand.RaiseCanExecuteChanged();
                    ExportCurrentCommand.RaiseCanExecuteChanged();
                    if (LivePreview) UpdatePreview();
                }
            }
        }

        // ───────── Ctor ─────────
        public MainViewModel()
        {
            // 前回保存(初回は 1920x1080 / header 0)
            var prefs = SettingsStore.Load();
            _rawWidth = prefs.RawWidth;
            _rawHeight = prefs.RawHeight;
            _headerBytes = prefs.HeaderBytes;

            BuildInitialRoots();

            // コマンド
            NavigateAddressCommand = new RelayCommand(_ => NavigateFromAddress());
            BrowseBackCommand = new RelayCommand(_ => BrowseBack(), _ => _backStack.Count > 0);
            BrowseForwardCommand = new RelayCommand(_ => BrowseForward(), _ => _forwardStack.Count > 0);

            ZoomInCommand = new RelayCommand(_ => { IsFitToScreen = false; ZoomPercent = Math.Min(ZoomPercent + 10, 400); });
            ZoomOutCommand = new RelayCommand(_ => { IsFitToScreen = false; ZoomPercent = Math.Max(ZoomPercent - 10, 10); });
            ZoomResetCommand = new RelayCommand(_ => { IsFitToScreen = false; ZoomPercent = 100; });

            PreviewCommand = new RelayCommand(_ => UpdatePreview(), _ => SelectedFile != null);

            ExportCurrentCommand = new RelayCommand(_ => ExportCurrent(), _ => SelectedFile != null && !IsExporting);
            ExportBatchCommand = new RelayCommand(_ => ExportBatch(), _ => Files.Any() && !IsExporting);
            BrowseExportFolderCommand = new RelayCommand(_ => BrowseExportFolder());
            Files.CollectionChanged += (_, __) => ExportBatchCommand.RaiseCanExecuteChanged();

            ColorizeCommand = new RelayCommand(_ => { });
            ConditionCommand = new RelayCommand(_ => { });
        }

        // ───────── アドレスバー ─────────
        private string? _addressPath;
        public string? AddressPath
        {
            get => _addressPath;
            set => SetProperty(ref _addressPath, value);
        }
        public ICommand NavigateAddressCommand { get; }
        private void NavigateFromAddress() => TryNavigateTo(AddressPath ?? string.Empty);

        // ───────── フォルダ / ファイル ─────────
        private string? _selectedFolderPath;
        public string? SelectedFolderPath
        {
            get => _selectedFolderPath;
            set
            {
                if (SetProperty(ref _selectedFolderPath, value))
                {
                    AddressPath = value;
                    RefreshFiles();
                }
            }
        }

        public enum FileFilterMode { RawOnly, ImagesOnly, All }
        public Array FilterModes => Enum.GetValues(typeof(FileFilterMode));

        private FileFilterMode _currentFilter = FileFilterMode.RawOnly;
        public FileFilterMode CurrentFilter
        {
            get => _currentFilter;
            set { if (SetProperty(ref _currentFilter, value)) RefreshFiles(); }
        }

        private string? _fileSearchText;
        public string? FileSearchText
        {
            get => _fileSearchText;
            set { if (SetProperty(ref _fileSearchText, value)) RefreshFiles(); }
        }

        public void RefreshFiles()
        {
            Files.Clear();
            if (string.IsNullOrWhiteSpace(SelectedFolderPath) || !Directory.Exists(SelectedFolderPath)) return;

            try
            {
                foreach (var path in Directory.EnumerateFiles(SelectedFolderPath))
                {
                    var fi = new FileItem(path);
                    if (!PassesFilter(fi)) continue;
                    if (!PassesSearch(fi)) continue;
                    Files.Add(fi);
                }
            }
            catch { /* ignore */ }
        }

        private bool PassesFilter(FileItem item) =>
            CurrentFilter switch
            {
                FileFilterMode.RawOnly => item.Kind == FileKind.Raw,
                FileFilterMode.ImagesOnly => item.Kind == FileKind.Image,
                _ => true
            };

        private bool PassesSearch(FileItem item)
        {
            if (string.IsNullOrWhiteSpace(FileSearchText)) return true;
            return item.Name.IndexOf(FileSearchText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ───────── 履歴 ─────────
        private readonly Stack<string> _backStack = new();
        private readonly Stack<string> _forwardStack = new();

        public RelayCommand BrowseBackCommand { get; }
        public RelayCommand BrowseForwardCommand { get; }

        private void BrowseBack()
        {
            if (_backStack.Count == 0) return;
            var dest = _backStack.Pop();
            if (!Directory.Exists(dest)) { UpdateNavCommands(); return; }
            if (!string.IsNullOrEmpty(SelectedFolderPath)) _forwardStack.Push(SelectedFolderPath);
            SelectedFolderPath = dest;
            UpdateNavCommands();
        }

        private void BrowseForward()
        {
            if (_forwardStack.Count == 0) return;
            var dest = _forwardStack.Pop();
            if (!Directory.Exists(dest)) { UpdateNavCommands(); return; }
            if (!string.IsNullOrEmpty(SelectedFolderPath)) _backStack.Push(SelectedFolderPath);
            SelectedFolderPath = dest;
            UpdateNavCommands();
        }

        private void PushHistory(string? previous, string next)
        {
            if (!string.IsNullOrEmpty(previous)) _backStack.Push(previous);
            _forwardStack.Clear();
            UpdateNavCommands();
        }

        private void UpdateNavCommands()
        {
            BrowseBackCommand.RaiseCanExecuteChanged();
            BrowseForwardCommand.RaiseCanExecuteChanged();
        }

        // ───────── RAW 読込設定 ─────────
        private int _rawWidth = 1024;
        public int RawWidth { get => _rawWidth; set { if (SetProperty(ref _rawWidth, value) && LivePreview) UpdatePreview(); } }

        private int _rawHeight = 1024;
        public int RawHeight { get => _rawHeight; set { if (SetProperty(ref _rawHeight, value) && LivePreview) UpdatePreview(); } }

        private int _headerBytes = 0;
        public int HeaderBytes { get => _headerBytes; set { if (SetProperty(ref _headerBytes, value) && LivePreview) UpdatePreview(); } }

        private int _fileBits = 16; // 8/16/32
        public int FileBits { get => _fileBits; set { if (SetProperty(ref _fileBits, value) && LivePreview) UpdatePreview(); } }

        private int _usedBits = 12; // 8/10/12/14/16
        public int UsedBits { get => _usedBits; set { if (SetProperty(ref _usedBits, value) && LivePreview) UpdatePreview(); } }

        private BitAlignment _bitAlignment = BitAlignment.MSB;
        public BitAlignment BitAlignment { get => _bitAlignment; set { if (SetProperty(ref _bitAlignment, value) && LivePreview) UpdatePreview(); } }

        private Endianness _endian = Endianness.Little;
        public Endianness Endian { get => _endian; set { if (SetProperty(ref _endian, value) && LivePreview) UpdatePreview(); } }

        // Bayer CFA
        private CfaPattern _cfa = CfaPattern.RGGB;
        public CfaPattern Cfa { get => _cfa; set { if (SetProperty(ref _cfa, value) && LivePreview) UpdatePreview(); } }
        public Array CfaOptions => Enum.GetValues(typeof(CfaPattern));

        public IReadOnlyList<KeyValuePair<BitAlignment, string>> AlignOptions { get; } =
            new[]
            {
                new KeyValuePair<BitAlignment, string>(BitAlignment.MSB, "MSB (left-aligned)"),
                new KeyValuePair<BitAlignment, string>(BitAlignment.LSB, "LSB (right-aligned)")
            };

        public IReadOnlyList<KeyValuePair<Endianness, string>> EndianOptions { get; } =
            new[]
            {
                new KeyValuePair<Endianness, string>(Endianness.Little, "Little Endian"),
                new KeyValuePair<Endianness, string>(Endianness.Big,    "Big Endian")
            };

        public IReadOnlyList<int> FileBitsOptions { get; } = new[] { 8, 16, 32 };
        public IReadOnlyList<int> UsedBitsOptions { get; } = new[] { 8, 10, 12, 14, 16 };

        // ───────── Develop パラメータ ─────────
        private double _gainR = 1.0, _gainG = 1.0, _gainB = 1.0;
        public double GainR { get => _gainR; set { if (SetProperty(ref _gainR, value) && LivePreview) UpdatePreview(); } }
        public double GainG { get => _gainG; set { if (SetProperty(ref _gainG, value) && LivePreview) UpdatePreview(); } }
        public double GainB { get => _gainB; set { if (SetProperty(ref _gainB, value) && LivePreview) UpdatePreview(); } }

        private int _blackLevelOffset = 0;
        public int BlackLevelOffset { get => _blackLevelOffset; set { if (SetProperty(ref _blackLevelOffset, Math.Max(0, value)) && LivePreview) UpdatePreview(); } }

        private bool _useColorMatrix = false;
        public bool UseColorMatrix { get => _useColorMatrix; set { if (SetProperty(ref _useColorMatrix, value) && LivePreview) UpdatePreview(); } }

        private double _m11 = 1, _m12 = 0, _m13 = 0;
        private double _m21 = 0, _m22 = 1, _m23 = 0;
        private double _m31 = 0, _m32 = 0, _m33 = 1;
        public double M11 { get => _m11; set { if (SetProperty(ref _m11, value) && LivePreview) UpdatePreview(); } }
        public double M12 { get => _m12; set { if (SetProperty(ref _m12, value) && LivePreview) UpdatePreview(); } }
        public double M13 { get => _m13; set { if (SetProperty(ref _m13, value) && LivePreview) UpdatePreview(); } }
        public double M21 { get => _m21; set { if (SetProperty(ref _m21, value) && LivePreview) UpdatePreview(); } }
        public double M22 { get => _m22; set { if (SetProperty(ref _m22, value) && LivePreview) UpdatePreview(); } }
        public double M23 { get => _m23; set { if (SetProperty(ref _m23, value) && LivePreview) UpdatePreview(); } }
        public double M31 { get => _m31; set { if (SetProperty(ref _m31, value) && LivePreview) UpdatePreview(); } }
        public double M32 { get => _m32; set { if (SetProperty(ref _m32, value) && LivePreview) UpdatePreview(); } }
        public double M33 { get => _m33; set { if (SetProperty(ref _m33, value) && LivePreview) UpdatePreview(); } }

        private bool _clipSaturatedToWhite = true;
        public bool ClipSaturatedToWhite { get => _clipSaturatedToWhite; set { if (SetProperty(ref _clipSaturatedToWhite, value) && LivePreview) UpdatePreview(); } }

        private DemosaicAlgorithm _demosaic = DemosaicAlgorithm.Bilinear;
        public DemosaicAlgorithm Demosaic { get => _demosaic; set { if (SetProperty(ref _demosaic, value) && LivePreview) UpdatePreview(); } }
        public Array DemosaicOptions => Enum.GetValues(typeof(DemosaicAlgorithm));

        private double _gamma = 1.0;
        public double Gamma { get => _gamma; set { var g = Math.Clamp(value, 0.10, 3.00); if (SetProperty(ref _gamma, g) && LivePreview) UpdatePreview(); } }

        private double _contrast = 0.0;
        public double Contrast { get => _contrast; set { var c = Math.Clamp(value, -100, 100); if (SetProperty(ref _contrast, c) && LivePreview) UpdatePreview(); } }

        // ───────── プレビュー画像 ─────────
        private ImageSource? _imageProcessed;
        public ImageSource? ImageSource { get => _imageProcessed; set => SetProperty(ref _imageProcessed, value); }

        private ImageSource? _imageOriginal;
        public ImageSource? OriginalImageSource { get => _imageOriginal; set => SetProperty(ref _imageOriginal, value); }

        private bool _isCompare;
        public bool IsCompare { get => _isCompare; set => SetProperty(ref _isCompare, value); }

        private bool _livePreview = true;
        public bool LivePreview { get => _livePreview; set => SetProperty(ref _livePreview, value); }

        // ───────── ズーム ─────────
        private bool _isFitToScreen;
        public bool IsFitToScreen { get => _isFitToScreen; set => SetProperty(ref _isFitToScreen, value); }

        private double _zoomPercent = 100;
        public double ZoomPercent
        {
            get => _zoomPercent;
            set { if (SetProperty(ref _zoomPercent, value)) OnPropertyChanged(nameof(ZoomScale)); }
        }
        public double ZoomScale => ZoomPercent / 100.0;

        public ICommand ZoomInCommand { get; }
        public ICommand ZoomOutCommand { get; }
        public ICommand ZoomResetCommand { get; }

        public ICommand ColorizeCommand { get; }
        public ICommand ConditionCommand { get; }
        public RelayCommand PreviewCommand { get; }

        // ───────── Export ─────────
        private ExportFormat _exportFormat = ExportFormat.Png;
        public ExportFormat ExportFormat { get => _exportFormat; set { if (SetProperty(ref _exportFormat, value)) OnPropertyChanged(nameof(IsJpeg)); } }
        public Array ExportFormats => Enum.GetValues(typeof(ExportFormat));
        public bool IsJpeg => ExportFormat == ExportFormat.Jpeg;

        private int _jpegQuality = 90;
        public int JpegQuality { get => _jpegQuality; set => SetProperty(ref _jpegQuality, Math.Clamp(value, 1, 100)); }

        private bool _exportToSameFolder = true;
        public bool ExportToSameFolder { get => _exportToSameFolder; set => SetProperty(ref _exportToSameFolder, value); }

        private string _exportFolderPath = "";
        public string ExportFolderPath { get => _exportFolderPath; set => SetProperty(ref _exportFolderPath, value); }

        private string _fileNameSuffix = "_dev";
        public string FileNameSuffix { get => _fileNameSuffix; set => SetProperty(ref _fileNameSuffix, value ?? ""); }

        private bool _overwriteExisting = false;
        public bool OverwriteExisting { get => _overwriteExisting; set => SetProperty(ref _overwriteExisting, value); }

        private bool _isExporting;
        public bool IsExporting
        {
            get => _isExporting;
            set
            {
                if (SetProperty(ref _isExporting, value))
                {
                    ExportCurrentCommand.RaiseCanExecuteChanged();
                    ExportBatchCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private double _exportProgress;
        public double ExportProgress { get => _exportProgress; set => SetProperty(ref _exportProgress, value); }

        public RelayCommand ExportCurrentCommand { get; }
        public RelayCommand ExportBatchCommand { get; }
        public RelayCommand BrowseExportFolderCommand { get; }

        private void BrowseExportFolder()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choose export folder",
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "__select_folder__"
            };
            if (dlg.ShowDialog() == true)
            {
                var dir = Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrEmpty(dir)) ExportFolderPath = dir!;
            }
        }

        private string BuildOutputPath(string inputPath)
        {
            var dir = ExportToSameFolder
                ? Path.GetDirectoryName(inputPath)!
                : (!string.IsNullOrWhiteSpace(ExportFolderPath) ? ExportFolderPath : Path.GetDirectoryName(inputPath)!);

            var name = Path.GetFileNameWithoutExtension(inputPath);
            var ext = ExportFormat switch
            {
                ExportFormat.Png => ".png",
                ExportFormat.Jpeg => ".jpg",
                ExportFormat.Tiff => ".tif",
                _ => ".bmp",
            };
            return Path.Combine(dir, name + FileNameSuffix + ext);
        }

        private void ExportCurrent()
        {
            if (SelectedFile == null) return;

            var sfd = new SaveFileDialog()
            {
                Title = "Export current image",
                Filter = "PNG (*.png)|*.png|JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg|TIFF (*.tif;*.tiff)|*.tif;*.tiff|BMP (*.bmp)|*.bmp",
                FileName = Path.GetFileName(BuildOutputPath(SelectedFile.FullPath)),
                InitialDirectory = ExportToSameFolder || string.IsNullOrWhiteSpace(ExportFolderPath)
                    ? Path.GetDirectoryName(SelectedFile.FullPath)
                    : ExportFolderPath
            };
            if (sfd.ShowDialog() != true) return;

            try
            {
                var bmp = ProcessFileToBitmap(SelectedFile);
                SaveBitmap(bmp, sfd.FileName);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Export failed.\n{ex.Message}", "Export",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void ExportBatch()
        {
            if (!Files.Any()) return;
            IsExporting = true;
            ExportProgress = 0;

            int total = Files.Count;
            int ok = 0, skip = 0, ng = 0;

            for (int i = 0; i < total; i++)
            {
                var item = Files[i];
                var outPath = BuildOutputPath(item.FullPath);

                try
                {
                    if (!OverwriteExisting && File.Exists(outPath))
                    {
                        skip++;
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                        var bmp = ProcessFileToBitmap(item);
                        SaveBitmap(bmp, outPath);
                        ok++;
                    }
                }
                catch
                {
                    ng++;
                }

                ExportProgress = (i + 1) * 100.0 / total;
            }

            IsExporting = false;
            System.Windows.MessageBox.Show($"Batch export finished.\nSuccess: {ok}\nSkipped: {skip}\nFailed: {ng}",
                "Export", System.Windows.MessageBoxButton.OK,
                ng == 0 ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        }

        // ───────── プレビュー生成 ─────────
        private void UpdatePreview()
        {
            if (SelectedFile == null) return;

            try
            {
                if (SelectedFile.Kind == FileKind.Image)
                {
                    var src = LoadStandardImage(SelectedFile.FullPath);
                    OriginalImageSource = src;

                    ImageSource = ApplyGainMatrixClipAndTone(
                        src, blackOffset8: BlackLevelOffset,
                        gr: GainR, gg: GainG, gb: GainB,
                        useMat: UseColorMatrix,
                        m11: M11, m12: M12, m13: M13, m21: M21, m22: M22, m23: M23, m31: M31, m32: M32, m33: M33,
                        clipWhite: ClipSaturatedToWhite, gamma: Gamma, contrast: Contrast);
                }
                else
                {
                    int maxVal;
                    var raw = ReadRawToUShort(SelectedFile.FullPath, RawWidth, RawHeight,
                                              HeaderBytes, FileBits, UsedBits, BitAlignment, Endian, out maxVal);

                    var baseBgr = DemosaicToBgr32(raw, RawWidth, RawHeight, maxVal,
                                                  blackOffsetRaw: BlackLevelOffset,
                                                  gr: 1.0, gg: 1.0, gb: 1.0,
                                                  useMat: false,
                                                  m11: 1, m12: 0, m13: 0, m21: 0, m22: 1, m23: 0, m31: 0, m32: 0, m33: 1,
                                                  ClipSaturation: false, algo: Demosaic, pattern: Cfa);
                    OriginalImageSource = MakeBitmapFromBgr32(baseBgr, RawWidth, RawHeight);

                    var procBgr = DemosaicToBgr32(raw, RawWidth, RawHeight, maxVal,
                                                  blackOffsetRaw: BlackLevelOffset,
                                                  gr: GainR, gg: GainG, gb: GainB,
                                                  useMat: UseColorMatrix,
                                                  m11: M11, m12: M12, m13: M13, m21: M21, m22: M22, m23: M23, m31: M31, m32: M32, m33: M33,
                                                  ClipSaturation: ClipSaturatedToWhite, algo: Demosaic, pattern: Cfa);
                    ApplyGammaContrast(procBgr, Gamma, Contrast);

                    ImageSource = MakeBitmapFromBgr32(procBgr, RawWidth, RawHeight);
                }
            }
            catch
            {
                ImageSource = null;
                OriginalImageSource = null;
            }
        }

        // ───────── 画像 I/O / 変換ユーティリティ ─────────
        private static BitmapSource LoadStandardImage(string path)
        {
            var bi = new BitmapImage();
            using var fs = File.OpenRead(path);
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        // 標準画像: 黒引き→WB→行列→白クリップ→ガンマ/コントラスト
        private static BitmapSource ApplyGainMatrixClipAndTone(
            BitmapSource src, int blackOffset8,
            double gr, double gg, double gb,
            bool useMat,
            double m11, double m12, double m13, double m21, double m22, double m23, double m31, double m32, double m33,
            bool clipWhite, double gamma, double contrast)
        {
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var buf = new byte[stride * h];
            conv.CopyPixels(buf, stride, 0);

            for (int y = 0, p = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++, p += 4)
                {
                    int b8 = Math.Max(0, buf[p + 0] - blackOffset8);
                    int g8 = Math.Max(0, buf[p + 1] - blackOffset8);
                    int r8 = Math.Max(0, buf[p + 2] - blackOffset8);

                    double B = b8 / 255.0;
                    double G = g8 / 255.0;
                    double R = r8 / 255.0;

                    R *= gr; G *= gg; B *= gb;

                    if (useMat)
                    {
                        double r2 = m11 * R + m12 * G + m13 * B;
                        double g2 = m21 * R + m22 * G + m23 * B;
                        double b2 = m31 * R + m32 * G + m33 * B;
                        R = r2; G = g2; B = b2;
                    }

                    if (clipWhite && (R >= 1.0 || G >= 1.0 || B >= 1.0)) { R = G = B = 1.0; }

                    R = Math.Clamp(R, 0, 1);
                    G = Math.Clamp(G, 0, 1);
                    B = Math.Clamp(B, 0, 1);

                    buf[p + 0] = (byte)Math.Round(B * 255.0);
                    buf[p + 1] = (byte)Math.Round(G * 255.0);
                    buf[p + 2] = (byte)Math.Round(R * 255.0);
                }
            }

            ApplyGammaContrast(buf, gamma, contrast);
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, buf, stride);
            bmp.Freeze();
            return bmp;
        }

        // RAW → ushort[]
        private static ushort[] ReadRawToUShort(
            string path, int width, int height, int headerBytes,
            int fileBits, int usedBits, BitAlignment align, Endianness endian, out int maxVal)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("Invalid size");
            if (fileBits != 8 && fileBits != 16 && fileBits != 32) throw new NotSupportedException("fileBits must be 8/16/32.");
            if (usedBits <= 0 || usedBits > fileBits) throw new ArgumentException("UsedBits must be <= fileBits.");

            maxVal = (1 << usedBits) - 1;
            int n = checked(width * height);
            ushort[] dst = new ushort[n];

            byte[] input;
            using (var fs = File.OpenRead(path))
            {
                if (headerBytes > 0 && headerBytes < fs.Length) fs.Seek(headerBytes, SeekOrigin.Begin);
                long remain = fs.Length - fs.Position;
                int bps = fileBits / 8;
                long need = (long)n * bps;
                int toRead = (int)Math.Min(remain, need);
                input = new byte[toRead];
                fs.Read(input, 0, toRead);
            }

            switch (fileBits)
            {
                case 8:
                    for (int i = 0; i < n && i < input.Length; i++)
                    {
                        int raw = input[i];
                        int val = (align == BitAlignment.MSB) ? (raw >> (8 - usedBits))
                                                              : (raw & ((1 << usedBits) - 1));
                        dst[i] = (ushort)val;
                    }
                    break;

                case 16:
                    for (int i = 0, s = 0; i < n && s + 1 < input.Length; i++, s += 2)
                    {
                        int b0 = input[s + 0], b1 = input[s + 1];
                        int word = (endian == Endianness.Little) ? (b0 | (b1 << 8)) : ((b0 << 8) | b1);
                        int val = (align == BitAlignment.MSB) ? (word >> (16 - usedBits))
                                                              : (word & ((1 << usedBits) - 1));
                        dst[i] = (ushort)val;
                    }
                    break;

                case 32:
                    for (int i = 0, s = 0; i < n && s + 3 < input.Length; i++, s += 4)
                    {
                        uint d = (endian == Endianness.Little)
                            ? (uint)(input[s] | (input[s + 1] << 8) | (input[s + 2] << 16) | (input[s + 3] << 24))
                            : (uint)((input[s] << 24) | (input[s + 1] << 16) | (input[s + 2] << 8) | input[s + 3]);

                        uint mask = (usedBits == 32) ? 0xFFFFFFFFu : ((1u << usedBits) - 1u);
                        uint val = (align == BitAlignment.MSB) ? (d >> (32 - usedBits)) : (d & mask);
                        dst[i] = (ushort)val; // usedBits<=16 前提
                    }
                    break;
            }
            return dst;
        }

        // Bayer (RGGB/GRBG/GBRG/BGGR) bilinear
        private static byte[] DemosaicToBgr32(
            ushort[] raw, int w, int h, int maxVal,
            int blackOffsetRaw,
            double gr, double gg, double gb,
            bool useMat,
            double m11, double m12, double m13, double m21, double m22, double m23, double m31, double m32, double m33,
            bool ClipSaturation, DemosaicAlgorithm algo, CfaPattern pattern)
        {
            int n = w * h;
            byte[] bgr = new byte[n * 4];

            double denom = Math.Max(1.0, maxVal - blackOffsetRaw);
            double inv = 1.0 / denom;

            int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
            int Idx(int x, int y) => y * w + x;
            double s(int xx, int yy) => Math.Max(0, raw[Idx(xx, yy)] - blackOffsetRaw);

            static char Cfa(bool xOdd, bool yOdd, CfaPattern p) => p switch
            {
                CfaPattern.RGGB => (!xOdd && !yOdd) ? 'R' : (xOdd && yOdd) ? 'B' : 'G',
                CfaPattern.GRBG => (!xOdd && !yOdd) ? 'G' : (xOdd && !yOdd) ? 'R' : (!xOdd && yOdd) ? 'B' : 'G',
                CfaPattern.GBRG => (!xOdd && !yOdd) ? 'G' : (xOdd && !yOdd) ? 'B' : (!xOdd && yOdd) ? 'R' : 'G',
                CfaPattern.BGGR => (!xOdd && !yOdd) ? 'B' : (xOdd && !yOdd) ? 'G' : (!xOdd && yOdd) ? 'G' : 'R',
                _ => 'G'
            };

            for (int y = 0; y < h; y++)
            {
                bool yOdd = (y & 1) == 1;
                for (int x = 0; x < w; x++)
                {
                    bool xOdd = (x & 1) == 1;

                    int xm1 = Clamp(x - 1, 0, w - 1);
                    int xp1 = Clamp(x + 1, 0, w - 1);
                    int ym1 = Clamp(y - 1, 0, h - 1);
                    int yp1 = Clamp(y + 1, 0, h - 1);

                    char c = Cfa(xOdd, yOdd, pattern);
                    double R, G, B;

                    if (c == 'R')
                    {
                        R = s(x, y);
                        G = (s(xm1, y) + s(xp1, y) + s(x, ym1) + s(x, yp1)) * 0.25;
                        B = (s(xm1, ym1) + s(xp1, ym1) + s(xm1, yp1) + s(xp1, yp1)) * 0.25;
                    }
                    else if (c == 'B')
                    {
                        B = s(x, y);
                        G = (s(xm1, y) + s(xp1, y) + s(x, ym1) + s(x, yp1)) * 0.25;
                        R = (s(xm1, ym1) + s(xp1, ym1) + s(xm1, yp1) + s(xp1, yp1)) * 0.25;
                    }
                    else // G
                    {
                        // 近傍の偶奇を bool で明確に計算
                        bool xOddLeft = (((x - 1) & 1) == 1);
                        bool xOddRight = (((x + 1) & 1) == 1);
                        bool yOddUp = (((y - 1) & 1) == 1);
                        bool yOddDown = (((y + 1) & 1) == 1);

                        char left = Cfa(xOddLeft, yOdd, pattern);
                        char right = Cfa(xOddRight, yOdd, pattern);
                        char up = Cfa(xOdd, yOddUp, pattern);
                        char down = Cfa(xOdd, yOddDown, pattern);

                        G = s(x, y);
                        bool horizR = (left == 'R') || (right == 'R') || !((up == 'R') || (down == 'R'));
                        if (horizR)
                        {
                            R = (s(xm1, y) + s(xp1, y)) * 0.5;
                            B = (s(x, ym1) + s(x, yp1)) * 0.5;
                        }
                        else
                        {
                            R = (s(x, ym1) + s(x, yp1)) * 0.5;
                            B = (s(xm1, y) + s(xp1, y)) * 0.5;
                        }
                    }

                    // 正規化・ゲイン
                    R = (R * inv) * gr;
                    G = (G * inv) * gg;
                    B = (B * inv) * gb;

                    // 行列
                    if (useMat)
                    {
                        double r2 = m11 * R + m12 * G + m13 * B;
                        double g2 = m21 * R + m22 * G + m23 * B;
                        double b2 = m31 * R + m32 * G + m33 * B;
                        R = r2; G = g2; B = b2;
                    }

                    if (ClipSaturation && (R >= 1.0 || G >= 1.0 || B >= 1.0)) { R = G = B = 1.0; }

                    R = Math.Clamp(R, 0, 1);
                    G = Math.Clamp(G, 0, 1);
                    B = Math.Clamp(B, 0, 1);

                    int p = (y * w + x) * 4;
                    bgr[p + 0] = (byte)Math.Round(B * 255.0);
                    bgr[p + 1] = (byte)Math.Round(G * 255.0);
                    bgr[p + 2] = (byte)Math.Round(R * 255.0);
                }
            }

            return bgr;
        }

        private static void ApplyGammaContrast(byte[] bgr, double gamma, double contrastPercent)
        {
            if (bgr == null || bgr.Length == 0) return;

            double e = 1.0 / Math.Clamp(gamma, 0.10, 3.00);
            double k = 1.0 + (contrastPercent / 100.0);

            byte[] lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double x = i / 255.0;
                x = Math.Pow(x, e);
                x = (x - 0.5) * k + 0.5;
                x = Math.Clamp(x, 0.0, 1.0);
                lut[i] = (byte)Math.Round(x * 255.0);
            }

            for (int p = 0; p < bgr.Length; p += 4)
            {
                bgr[p + 0] = lut[bgr[p + 0]];
                bgr[p + 1] = lut[bgr[p + 1]];
                bgr[p + 2] = lut[bgr[p + 2]];
            }
        }

        private static BitmapSource MakeBitmapFromBgr32(byte[] bgr, int w, int h)
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bgr, w * 4);
            bmp.Freeze();
            return bmp;
        }

        // ───────── ナビ / 初期化 ─────────
        public bool TryNavigateTo(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            var s = input.Trim();

            if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
                s = s[1..^1];

            s = Environment.ExpandEnvironmentVariables(s);

            if (s.StartsWith("~"))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                s = Path.Combine(home, s.TrimStart('~', '\\', '/'));
            }

            if (!Path.IsPathRooted(s))
            {
                var baseDir = SelectedFolderPath;
                if (string.IsNullOrEmpty(baseDir)) baseDir = Environment.CurrentDirectory;
                s = Path.GetFullPath(Path.Combine(baseDir, s));
            }

            if (File.Exists(s)) s = Path.GetDirectoryName(s)!;
            if (!Directory.Exists(s)) return false;

            PushHistory(SelectedFolderPath, s);
            SelectedFolderPath = s;
            return true;
        }

        private void BuildInitialRoots()
        {
            var quick = FileSystemItem.Group("Quick Access");
            AddIfExists(quick, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop");
            AddIfExists(quick, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pictures");
            AddIfExists(quick, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents");
            if (quick.Children.Count > 0) DirectoryRoots.Add(quick);

            var thisPc = FileSystemItem.Group("This PC");
            foreach (var d in DriveInfo.GetDrives())
            {
                var label = SafeDriveLabel(d);
                thisPc.Children.Add(new FileSystemItem(label, d.RootDirectory.FullName, isDrive: true));
            }
            DirectoryRoots.Add(thisPc);
        }

        private static void AddIfExists(FileSystemItem group, string path, string? displayName = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            var name = displayName ?? new DirectoryInfo(path).Name;
            group.Children.Add(new FileSystemItem(name, path));
        }

        private static string SafeDriveLabel(DriveInfo d)
        {
            var name = d.Name.TrimEnd(Path.DirectorySeparatorChar);
            try
            {
                if (d.IsReady && !string.IsNullOrWhiteSpace(d.VolumeLabel))
                    return $"{d.VolumeLabel} ({name})";
            }
            catch { }
            return name;
        }

        // ───────── Export 用処理 ─────────
        private BitmapSource ProcessFileToBitmap(FileItem fi)
        {
            if (fi.Kind == FileKind.Image)
            {
                var src = LoadStandardImage(fi.FullPath);
                return ApplyGainMatrixClipAndTone(
                    src, blackOffset8: BlackLevelOffset,
                    gr: GainR, gg: GainG, gb: GainB,
                    useMat: UseColorMatrix,
                    m11: M11, m12: M12, m13: M13, m21: M21, m22: M22, m23: M23, m31: M31, m32: M32, m33: M33,
                    clipWhite: ClipSaturatedToWhite, gamma: Gamma, contrast: Contrast);
            }
            else
            {
                int maxVal;
                var raw = ReadRawToUShort(fi.FullPath, RawWidth, RawHeight,
                                          HeaderBytes, FileBits, UsedBits, BitAlignment, Endian, out maxVal);

                var procBgr = DemosaicToBgr32(raw, RawWidth, RawHeight, maxVal,
                                              blackOffsetRaw: BlackLevelOffset,
                                              gr: GainR, gg: GainG, gb: GainB,
                                              useMat: UseColorMatrix,
                                              m11: M11, m12: M12, m13: M13, m21: M21, m22: M22, m23: M23, m31: M31, m32: M32, m33: M33,
                                              ClipSaturation: ClipSaturatedToWhite, algo: Demosaic, pattern: Cfa);
                ApplyGammaContrast(procBgr, Gamma, Contrast);

                return MakeBitmapFromBgr32(procBgr, RawWidth, RawHeight);
            }
        }

        private void SaveBitmap(BitmapSource bmp, string outPath)
        {
            BitmapEncoder enc = ExportFormat switch
            {
                ExportFormat.Png => new PngBitmapEncoder(),
                ExportFormat.Jpeg => new JpegBitmapEncoder() { QualityLevel = JpegQuality },
                ExportFormat.Tiff => new TiffBitmapEncoder(),
                _ => new BmpBitmapEncoder(),
            };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(outPath);
            enc.Save(fs);
        }
    }
}
