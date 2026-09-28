using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Views;

/// <summary>
/// Rawファイルの読込フォーマットを指定するダイアログ(ImageJ Raw import相当)。
/// ファイルサイズ整合チェックとプリセット選択・保存を備える。
/// ファイルサイズに合う形式の候補一覧と、サイズ別の記憶(次回から自動で開くか・記憶の削除)も扱う。
/// </summary>
public partial class RawImportDialog : Window
{
    private static readonly int[] BitDepths = { 12, 10, 14, 16, 8 };
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x7F, 0xBF, 0x7F));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x9B, 0x5B));

    private readonly string _filePath;
    private readonly long _fileSize;
    private readonly string _extension;
    private readonly FormatPresetStore _presetStore;
    private readonly FormatMemory? _formatMemory;
    private readonly RawFormat? _displayedFormat;
    private Dictionary<string, RawFormat> _presets;
    private IReadOnlyList<FormatCandidate> _candidates = Array.Empty<FormatCandidate>();
    private bool _initializing = true;
    private bool _syncingCandidates;

    /// <summary>
    /// ファイルサイズを取得する。取得できない場合は -1(サイズ不明)。
    /// </summary>
    /// <remarks>
    /// コンストラクタで例外を投げると、呼び出し側の「読み込みに失敗しました」
    /// ではなく未処理例外ダイアログになってしまう。
    /// </remarks>
    /// <param name="path">対象ファイル。</param>
    /// <returns>バイト数。不明なら -1。</returns>
    private static long SafeFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException)
        {
            return -1;
        }
    }

    /// <summary>ダイアログを生成する。</summary>
    /// <param name="filePath">開こうとしているRawファイルのパス。</param>
    /// <param name="presetStore">プリセットストア。</param>
    /// <param name="initialFormat">
    /// 初期値として表示するフォーマット(nullなら候補一覧の先頭、候補もなければ既定+サイズ推定)。
    /// </param>
    /// <param name="formatMemory">
    /// サイズ別フォーマット記憶。候補一覧・「次回からこの形式で開く」の初期値・記憶の削除に使う
    /// (nullなら記憶なしで候補を作り、削除はできない)。
    /// </param>
    /// <param name="displayedFormat">表示中の画像のフォーマット(候補一覧に使う)。</param>
    internal RawImportDialog(
        string filePath, FormatPresetStore presetStore, RawFormat? initialFormat = null,
        FormatMemory? formatMemory = null, RawFormat? displayedFormat = null)
    {
        InitializeComponent();
        _filePath = filePath;
        _fileSize = SafeFileSize(filePath);
        _extension = FormatHistory.ExtensionOf(filePath);
        _presetStore = presetStore;
        _formatMemory = formatMemory;
        _displayedFormat = displayedFormat;
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

        // ベンダー名(DOL/Staggered)ではなく、ファイル上の並びで指定する
        HdrCombo.Items.Add("なし");
        HdrCombo.Items.Add("自動 (フレーム数から推定)");
        HdrCombo.Items.Add("行交互 (L,S,L,S…)");
        HdrCombo.Items.Add("フレーム連結 (長→短)");
        HdrStagesCombo.Items.Add("2段");
        HdrStagesCombo.Items.Add("3段");

        RefreshPresetCombo();
        RefreshCandidates();

        RawFormat format = initialFormat
            ?? (_candidates.Count > 0 ? _candidates[0].Format : GuessInitialFormat());
        ApplyFormat(format);

        // 初期値が自動適用オフの記憶なら、その状態から始める(既定はオン)
        AutoOpenCheck.IsChecked = RememberedEntry(format)?.AutoOpen ?? true;
        UpdateMemoryNote(removed: null);
        _initializing = false;
        UpdateSizeNote();
    }

    /// <summary>「開く」で確定されたフォーマット。</summary>
    public RawFormat? Result { get; private set; }

    /// <summary>
    /// 「同じサイズのファイルは次回からこの形式で開く」の選択。確定後に呼び出し側が記憶へ反映する
    /// (オフでもフォーマットは記憶し、候補には出す)。
    /// </summary>
    public bool AutoOpenNextTime => AutoOpenCheck.IsChecked == true;

    /// <summary>候補一覧の中身(先頭ほど有力。一覧の2行目以降に対応する)。</summary>
    internal IReadOnlyList<FormatCandidate> Candidates => _candidates;

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
                "RawAnalyzer", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        HdrCombo.SelectedIndex = format.Hdr switch
        {
            HdrMode.Auto => 1,
            HdrMode.LineInterleaved => 2,
            HdrMode.FrameSequential => 3,
            _ => 0,
        };
        HdrStagesCombo.SelectedIndex = format.HdrStages == 3 ? 1 : 0;
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

        if (!NumericInput.TryParsePositive(ExposureRatioBox.Text, out double exposureRatio))
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
                1 => HdrMode.Auto,
                2 => HdrMode.LineInterleaved,
                3 => HdrMode.FrameSequential,
                _ => HdrMode.None,
            },
            HdrStages = HdrStagesCombo.SelectedIndex == 1 ? 3 : 2,
            ExposureRatio = exposureRatio,
            HdrLineBlock = lineBlock,
            HdrRowOffset = rowOffset,
        };
    }

    private void UpdateSizeNote()
    {
        RawFormat? format = TryBuildFormat(out string error);
        SyncCandidateSelection(format);
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

    /// <summary>
    /// 候補一覧を作り直す(先頭の行は案内。候補がなければ一覧を無効にする)。
    /// </summary>
    private void RefreshCandidates()
    {
        _candidates = FormatCandidates.Build(
            _fileSize, _extension, _filePath, _formatMemory?.History ?? new FormatHistory(),
            _displayedFormat);
        _syncingCandidates = true;
        try
        {
            CandidateCombo.Items.Clear();
            CandidateCombo.Items.Add(_candidates.Count > 0
                ? $"候補から選ぶ…({_candidates.Count} 件)"
                : "候補なし(ファイルサイズに合う形式が見つかりません)");
            foreach (FormatCandidate candidate in _candidates)
            {
                CandidateCombo.Items.Add(FormatCandidateText.Display(candidate));
            }

            CandidateCombo.SelectedIndex = 0;
            CandidateCombo.IsEnabled = _candidates.Count > 0;
            CandidateCombo.ToolTip =
                "ファイルサイズにちょうど合う形式を、同じサイズの記憶 → 表示中の画像 → 記憶から推定 → " +
                "ファイル名の「幅x高さ」 → 解像度表 の順に並べます。選ぶと全項目に入ります。";
        }
        finally
        {
            _syncingCandidates = false;
        }
    }

    /// <summary>入力中の値と同じ候補を一覧で選択状態にする(なければ先頭の案内の行)。</summary>
    /// <param name="current">入力中の値(不正なら null)。</param>
    private void SyncCandidateSelection(RawFormat? current)
    {
        int index = 0;
        for (int i = 0; current is not null && i < _candidates.Count; i++)
        {
            if (_candidates[i].Format == current)
            {
                index = i + 1;
                break;
            }
        }

        _syncingCandidates = true;
        CandidateCombo.SelectedIndex = index;
        _syncingCandidates = false;
    }

    private void OnCandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingCandidates || CandidateCombo.SelectedIndex <= 0
            || CandidateCombo.SelectedIndex > _candidates.Count)
        {
            return;
        }

        // プリセットの選択と同じく、全項目にその形式を入れる
        _initializing = true;
        ApplyFormat(_candidates[CandidateCombo.SelectedIndex - 1].Format);
        _initializing = false;
        UpdateSizeNote();
    }

    /// <summary>このファイルのキー(サイズ・拡張子)でフォーマットが記憶されていればその記憶。</summary>
    private FormatHistoryEntry? RememberedEntry(RawFormat format)
    {
        return _formatMemory is null || _fileSize < 0
            ? null
            : _formatMemory.History.Find(_fileSize, _extension, format);
    }

    /// <summary>このサイズの記憶の件数(と削除した件数)を示し、削除ボタンの有効・無効を決める。</summary>
    /// <param name="removed">削除した件数(削除の直後だけ)。</param>
    private void UpdateMemoryNote(int? removed)
    {
        int count = _formatMemory is null || _fileSize < 0
            ? 0
            : _formatMemory.History.Find(_fileSize, _extension).Count;
        string key = _fileSize < 0
            ? "サイズ不明"
            : $"{_fileSize.ToString("N0", CultureInfo.InvariantCulture)} バイト・" +
              (_extension.Length > 0 ? _extension : "拡張子なし");
        ForgetSizeButton.IsEnabled = count > 0;
        ForgetSizeButton.ToolTip = $"このサイズ({key})のファイルを開いたときの形式の記憶をすべて削除します";
        MemoryNoteText.ToolTip = $"キー: {key}";
        if (removed is { } n)
        {
            MemoryNoteText.Text = $"✓ このサイズの記憶 {n} 件を削除しました";
            MemoryNoteText.Foreground = OkBrush;
        }
        else
        {
            MemoryNoteText.Text = count > 0
                ? $"このサイズの記憶: {count} 件"
                : "このサイズの記憶はありません";
            MemoryNoteText.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private void OnForgetSizeClick(object sender, RoutedEventArgs e)
    {
        if (_formatMemory is null || _fileSize < 0)
        {
            return;
        }

        int removed = _formatMemory.Forget(_fileSize, _extension);

        // 入力中の値はそのまま残し、候補一覧だけを作り直す
        RefreshCandidates();
        SyncCandidateSelection(TryBuildFormat(out _));
        UpdateMemoryNote(removed);
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
