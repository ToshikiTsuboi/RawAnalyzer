using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RawViewer.App.Services;
using RawViewer.Core;

namespace RawViewer.App.Views;

/// <summary>
/// Rawファイルの読込フォーマットを指定するダイアログ(ImageJ Raw import相当)。
/// ファイルサイズ整合チェックとプリセット選択・保存を備える。
/// </summary>
public partial class RawImportDialog : Window
{
    private static readonly int[] BitDepths = { 12, 10, 14, 16, 8 };
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x7F, 0xBF, 0x7F));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x9B, 0x5B));

    private readonly string _filePath;
    private readonly long _fileSize;
    private readonly FormatPresetStore _presetStore;
    private Dictionary<string, RawFormat> _presets;
    private bool _initializing = true;

    /// <summary>ダイアログを生成する。</summary>
    /// <param name="filePath">開こうとしているRawファイルのパス。</param>
    /// <param name="presetStore">プリセットストア。</param>
    /// <param name="initialFormat">初期値として表示するフォーマット(nullなら既定+サイズ推定)。</param>
    public RawImportDialog(string filePath, FormatPresetStore presetStore, RawFormat? initialFormat = null)
    {
        InitializeComponent();
        _filePath = filePath;
        _fileSize = new FileInfo(filePath).Length;
        _presetStore = presetStore;
        _presets = new Dictionary<string, RawFormat>(LoadPresetsSafe());

        FileNameText.Text = Path.GetFileName(filePath);
        Title = $"Rawファイルを開く — {Path.GetFileName(filePath)}";

        foreach (int depth in BitDepths)
        {
            BitDepthCombo.Items.Add($"{depth}bit");
        }

        PackingCombo.Items.Add("下詰め (LSB align)");
        PackingCombo.Items.Add("上詰め (MSB align)");
        EndianCombo.Items.Add("Little");
        EndianCombo.Items.Add("Big");
        foreach (string b in new[] { "RGGB", "BGGR", "GRBG", "GBRG", "なし (モノクロ)" })
        {
            BayerCombo.Items.Add(b);
        }

        HdrCombo.Items.Add("なし");
        HdrCombo.Items.Add("DOL 2段");
        HdrCombo.Items.Add("DOL 3段");
        HdrCombo.Items.Add("Staggered 2段");
        HdrCombo.Items.Add("Staggered 3段");

        RefreshPresetCombo();

        RawFormat format = initialFormat ?? GuessInitialFormat();
        ApplyFormat(format);
        _initializing = false;
        UpdateSizeNote();
    }

    /// <summary>「開く」で確定されたフォーマット。</summary>
    public RawFormat? Result { get; private set; }

    private IReadOnlyDictionary<string, RawFormat> LoadPresetsSafe()
    {
        // 破損を握りつぶすと、次に1件保存したときに全プリセットが消える。
        // .bak へ退避したことをユーザーへ知らせる。
        IReadOnlyDictionary<string, RawFormat> presets =
            _presetStore.LoadOrQuarantine(out bool corrupted);
        if (corrupted)
        {
            AppLog.Warn($"プリセットファイルが破損していたため退避: {_presetStore.BackupPath}");
            MessageBox.Show(
                this,
                "プリセットファイルが読み込めなかったため退避しました。" +
                $"{Environment.NewLine}{_presetStore.BackupPath}",
                "RawViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return presets;
    }

    private RawFormat GuessInitialFormat()
    {
        var probe = new RawFormat { Width = 1, Height = 1, BitDepth = 12 };
        IReadOnlyList<DimensionCandidate> candidates = RawLoader.GuessDimensions(_fileSize, probe);
        if (candidates.Count > 0)
        {
            return probe with { Width = candidates[0].Width, Height = candidates[0].Height };
        }

        return new RawFormat { Width = 1920, Height = 1080, BitDepth = 12 };
    }

    private void ApplyFormat(RawFormat format)
    {
        WidthBox.Text = format.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = format.Height.ToString(CultureInfo.InvariantCulture);
        int depthIndex = Array.IndexOf(BitDepths, format.BitDepth);
        BitDepthCombo.SelectedIndex = depthIndex >= 0 ? depthIndex : 0;
        PackingCombo.SelectedIndex = format.Packing == BitPacking.Lsb ? 0 : 1;
        EndianCombo.SelectedIndex = format.Endianness == Endianness.Little ? 0 : 1;
        BayerCombo.SelectedIndex = format.Bayer switch
        {
            BayerPattern.Rggb => 0,
            BayerPattern.Bggr => 1,
            BayerPattern.Grbg => 2,
            BayerPattern.Gbrg => 3,
            _ => 4,
        };
        HeaderOffsetBox.Text = format.HeaderOffset.ToString(CultureInfo.InvariantCulture);
        FrameCountBox.Text = format.FrameCount.ToString(CultureInfo.InvariantCulture);
        HdrCombo.SelectedIndex = (format.Hdr, format.HdrStages) switch
        {
            (HdrMode.Dol, 3) => 2,
            (HdrMode.Dol, _) => 1,
            (HdrMode.Staggered, 3) => 4,
            (HdrMode.Staggered, _) => 3,
            _ => 0,
        };
        ExposureRatioBox.Text = format.ExposureRatio.ToString(CultureInfo.InvariantCulture);
        HdrLineBlockBox.Text = format.HdrLineBlock.ToString(CultureInfo.InvariantCulture);
        HdrRowOffsetBox.Text = format.HdrRowOffset.ToString(CultureInfo.InvariantCulture);
    }

    private RawFormat? TryBuildFormat(out string error)
    {
        error = "";
        if (!int.TryParse(WidthBox.Text, out int width) || width <= 0)
        {
            error = "幅が不正です";
            return null;
        }

        if (!int.TryParse(HeightBox.Text, out int height) || height <= 0)
        {
            error = "高さが不正です";
            return null;
        }

        if (!long.TryParse(HeaderOffsetBox.Text, out long headerOffset) || headerOffset < 0)
        {
            error = "ヘッダオフセットが不正です";
            return null;
        }

        if (!int.TryParse(FrameCountBox.Text, out int frameCount) || frameCount <= 0)
        {
            error = "フレーム数が不正です";
            return null;
        }

        if (!double.TryParse(ExposureRatioBox.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double exposureRatio) || exposureRatio <= 0)
        {
            error = "露光比が不正です";
            return null;
        }

        if (!int.TryParse(HdrLineBlockBox.Text, out int lineBlock) || lineBlock < 0)
        {
            error = "ライン単位が不正です(0以上)";
            return null;
        }

        if (!int.TryParse(HdrRowOffsetBox.Text, out int rowOffset))
        {
            error = "行オフセットが不正です";
            return null;
        }

        return new RawFormat
        {
            Width = width,
            Height = height,
            BitDepth = BitDepths[Math.Max(0, BitDepthCombo.SelectedIndex)],
            Packing = PackingCombo.SelectedIndex == 1 ? BitPacking.Msb : BitPacking.Lsb,
            Endianness = EndianCombo.SelectedIndex == 1 ? Endianness.Big : Endianness.Little,
            Bayer = BayerCombo.SelectedIndex switch
            {
                0 => BayerPattern.Rggb,
                1 => BayerPattern.Bggr,
                2 => BayerPattern.Grbg,
                3 => BayerPattern.Gbrg,
                _ => BayerPattern.None,
            },
            HeaderOffset = headerOffset,
            FrameCount = frameCount,
            Hdr = HdrCombo.SelectedIndex switch
            {
                1 or 2 => HdrMode.Dol,
                3 or 4 => HdrMode.Staggered,
                _ => HdrMode.None,
            },
            HdrStages = HdrCombo.SelectedIndex is 2 or 4 ? 3 : 2,
            ExposureRatio = exposureRatio,
            HdrLineBlock = lineBlock,
            HdrRowOffset = rowOffset,
        };
    }

    private void UpdateSizeNote()
    {
        RawFormat? format = TryBuildFormat(out string error);
        if (format is null)
        {
            SizeNoteText.Text = $"✕ {error}";
            SizeNoteText.Foreground = WarnBrush;
            OpenButton.IsEnabled = false;
            return;
        }

        long expected = format.HeaderOffset + format.FrameSizeInBytes * format.FrameCount;
        double expectedMb = expected / (1024.0 * 1024.0);
        double actualMb = _fileSize / (1024.0 * 1024.0);
        string expr = $"{format.Width}×{format.Height}×{format.BytesPerPixel}byte"
            + (format.FrameCount > 1 ? $"×{format.FrameCount}fr" : "");
        if (expected == _fileSize)
        {
            SizeNoteText.Text = $"✓ 推定サイズ {expr} = {expectedMb:F2} MB — ファイルサイズと一致";
            SizeNoteText.Foreground = OkBrush;
            OpenButton.IsEnabled = true;
        }
        else
        {
            SizeNoteText.Text =
                $"⚠ 推定サイズ {expr} = {expectedMb:F2} MB ≠ ファイル {actualMb:F2} MB";
            SizeNoteText.Foreground = WarnBrush;
            // 末尾に余剰データがあるだけなら開ける
            OpenButton.IsEnabled = expected <= _fileSize;
        }
    }

    private void RefreshPresetCombo()
    {
        PresetCombo.Items.Clear();
        PresetCombo.Items.Add("プリセットを選択…");
        foreach (string name in _presets.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            PresetCombo.Items.Add(name);
        }

        PresetCombo.SelectedIndex = 0;
    }

    private void OnFieldChanged(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            UpdateSizeNote();
        }
    }

    private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || PresetCombo.SelectedIndex <= 0)
        {
            return;
        }

        string name = (string)PresetCombo.SelectedItem;
        if (_presets.TryGetValue(name, out RawFormat? format))
        {
            _initializing = true;
            ApplyFormat(format);
            _initializing = false;
            UpdateSizeNote();
        }
    }

    private void OnGuessClick(object sender, RoutedEventArgs e)
    {
        RawFormat? format = TryBuildFormat(out _);
        if (format is null)
        {
            return;
        }

        IReadOnlyList<DimensionCandidate> candidates = RawLoader.GuessDimensions(_fileSize, format);
        if (candidates.Count == 0)
        {
            SizeNoteText.Text = "⚠ 候補テーブルに一致する解像度がありません";
            SizeNoteText.Foreground = WarnBrush;
            return;
        }

        // 現在の値の次の候補へ巡回(再クリックで次候補)
        int current = candidates.ToList().FindIndex(
            c => c.Width.ToString(CultureInfo.InvariantCulture) == WidthBox.Text
                && c.Height.ToString(CultureInfo.InvariantCulture) == HeightBox.Text);
        DimensionCandidate next = candidates[(current + 1) % candidates.Count];
        WidthBox.Text = next.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = next.Height.ToString(CultureInfo.InvariantCulture);
    }

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        RawFormat? format = TryBuildFormat(out string error);
        if (format is null)
        {
            MessageBox.Show(this, error, "プリセット保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string defaultName = Path.GetFileNameWithoutExtension(_filePath);
        string? name = PromptPresetName(defaultName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _presets[name] = format;
        try
        {
            _presetStore.Save(_presets);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存に失敗しました: {ex.Message}", "プリセット保存",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        RefreshPresetCombo();
        PresetCombo.SelectedItem = name;
    }

    private string? PromptPresetName(string defaultName)
    {
        var box = new TextBox { Text = defaultName, Margin = new Thickness(12, 4, 12, 0) };
        var ok = new Button
        {
            Content = "保存",
            IsDefault = true,
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(0, 0, 8, 0),
        };
        var cancel = new Button
        {
            Content = "キャンセル",
            IsCancel = true,
            Padding = new Thickness(14, 4, 14, 4),
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "プリセット名:",
            Margin = new Thickness(12, 10, 12, 0),
        });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = "新規プリセットとして保存",
            Content = panel,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        box.SelectAll();
        box.Focus();
        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        RawFormat? format = TryBuildFormat(out string error);
        if (format is null)
        {
            MessageBox.Show(this, error, "Rawファイルを開く", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = format;
        DialogResult = true;
    }
}
