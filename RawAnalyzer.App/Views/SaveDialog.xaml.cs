using System.Windows;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Views;

/// <summary>保存形式。</summary>
public enum SaveFormat
{
    /// <summary>16bitコンテナのrawバイナリ。</summary>
    Raw,

    /// <summary>16bitグレースケールTIFF。</summary>
    Tiff16,

    /// <summary>16bitグレースケールPNG。</summary>
    Png16,

    /// <summary>8bit PNG(表示LUT/現像焼き込み)。</summary>
    Png8,

    /// <summary>8bit JPEG(表示LUT/現像焼き込み)。</summary>
    Jpeg8,

    /// <summary>float32 raw(HDR合成結果)。</summary>
    FloatRaw,
}

/// <summary>保存ダイアログの選択結果。</summary>
/// <param name="Format">保存形式。</param>
/// <param name="Packing">raw出力の詰め方向。</param>
/// <param name="Endianness">raw出力のバイト順。</param>
/// <param name="ApplyDisplayLut">表示LUT(黒/白点・ゲイン・ガンマ・コントラスト)を適用するか。</param>
/// <param name="ApplyWhiteBalance">WBゲインを適用するか。</param>
/// <param name="ApplyMatrix">カラーマトリクスを適用するか。</param>
/// <param name="ApplyDemosaic">デモザイク(カラー現像)を行うか。</param>
/// <param name="WriteSidecar">処理内容のテキストを併せて保存するか。</param>
/// <param name="Summary">出力内容の要約(サイドカーにも記録する)。</param>
public sealed record SaveChoice(
    SaveFormat Format,
    BitPacking Packing,
    Endianness Endianness,
    bool ApplyDisplayLut,
    bool ApplyWhiteBalance,
    bool ApplyMatrix,
    bool ApplyDemosaic,
    bool WriteSidecar,
    string Summary)
{
    /// <summary>画素値に何らかの処理が焼き込まれるか。</summary>
    public bool IsProcessed => Format is SaveFormat.Png8 or SaveFormat.Jpeg8
        && (ApplyDisplayLut || ApplyWhiteBalance || ApplyMatrix || ApplyDemosaic);
}

/// <summary>
/// 保存形式と「画素値に適用される処理」を選択するダイアログ。
/// 16bit/raw系では処理が一切適用されないことを明示し、8bit系では段階ごとに切り替えられる。
/// </summary>
public partial class SaveDialog : Window
{
    private readonly long _totalPixels;
    private readonly bool _allowFloatRaw;
    private readonly bool _hasBayer;

    /// <summary>ダイアログを生成する。</summary>
    /// <param name="totalPixels">対象画像の総画素数(巨大画像の形式制限判定用)。</param>
    /// <param name="allowFloatRaw">HDR合成中のfloat raw保存を選択肢に含めるか。</param>
    /// <param name="hasBayer">Bayerパターンが指定されている(デモザイク可能)か。</param>
    public SaveDialog(long totalPixels, bool allowFloatRaw = false, bool hasBayer = false)
    {
        InitializeComponent();
        _totalPixels = totalPixels;
        _allowFloatRaw = allowFloatRaw;
        _hasBayer = hasBayer;
        if (allowFloatRaw)
        {
            FormatCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = "float raw (32bit・HDR合成結果・無処理)",
            });
        }

        DemosaicCheck.IsChecked = hasBayer;
        FormatCombo.SelectedIndex = 0;
    }

    /// <summary>「保存」で確定された選択。</summary>
    public SaveChoice? Result { get; private set; }

    private SaveFormat SelectedFormat => FormatCombo.SelectedIndex switch
    {
        1 => SaveFormat.Tiff16,
        2 => SaveFormat.Png16,
        3 => SaveFormat.Png8,
        4 => SaveFormat.Jpeg8,
        5 when _allowFloatRaw => SaveFormat.FloatRaw,
        _ => SaveFormat.Raw,
    };

    /// <summary>8bit出力(処理が焼き込まれる形式)か。</summary>
    private bool IsEightBitOutput => SelectedFormat is SaveFormat.Png8 or SaveFormat.Jpeg8;

    private void OnFormatChanged(object sender, RoutedEventArgs e)
    {
        if (RawOptions is null)
        {
            return;
        }

        SaveFormat format = SelectedFormat;
        RawOptions.Visibility = format == SaveFormat.Raw ? Visibility.Visible : Visibility.Collapsed;

        // 16bit/raw系は無処理固定。チェックを外して操作不可にする
        bool processed = IsEightBitOutput;
        LutCheck.IsEnabled = processed;
        WbCheck.IsEnabled = processed && _hasBayer;
        MatrixCheck.IsEnabled = processed && _hasBayer;
        DemosaicCheck.IsEnabled = processed && _hasBayer;
        if (!processed)
        {
            LutCheck.IsChecked = false;
            WbCheck.IsChecked = false;
            MatrixCheck.IsChecked = false;
            DemosaicCheck.IsChecked = false;
        }
        else
        {
            LutCheck.IsChecked = true;
            WbCheck.IsChecked = _hasBayer;
            MatrixCheck.IsChecked = _hasBayer;
            DemosaicCheck.IsChecked = _hasBayer;
        }

        // PNG/JPEGはWICで全画素をメモリ上に構築するため巨大画像では使えない
        bool tooBig = format is SaveFormat.Png16 or SaveFormat.Png8 or SaveFormat.Jpeg8
            && _totalPixels > RawLoader.DefaultInMemoryPixelThreshold;
        NoteText.Visibility = tooBig ? Visibility.Visible : Visibility.Collapsed;
        NoteText.Text = tooBig
            ? "⚠ 1億画素を超える画像はPNG/JPEGに保存できません(raw/TIFFを使用してください)"
            : "";
        SaveButton.IsEnabled = !tooBig;
        UpdateSummary();
    }

    private void OnProcessingChanged(object sender, RoutedEventArgs e)
    {
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (SummaryText is null)
        {
            return;
        }

        SummaryText.Text = BuildSummary();
        SummaryText.Foreground = System.Windows.Media.Brushes.LightGreen;
        if (IsEightBitOutput)
        {
            SummaryText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xD9, 0x9B, 0x5B));
        }
    }

    private string BuildSummary()
    {
        switch (SelectedFormat)
        {
            case SaveFormat.Raw:
                return "✓ 無処理: センサ出力そのままの画素値を16bitコンテナへ出力します"
                    + "(表示調整・WB・現像は一切反映されません)";
            case SaveFormat.Tiff16:
            case SaveFormat.Png16:
                return "✓ 無処理: raw値(16bitフルスケール)をそのまま出力します"
                    + "(表示調整・WB・現像は一切反映されません)";
            case SaveFormat.FloatRaw:
                return "✓ 無処理: HDR合成結果(float32・線形)をそのまま出力します";
            default:
                var stages = new List<string>();
                if (DemosaicCheck.IsChecked == true)
                {
                    if (WbCheck.IsChecked == true)
                    {
                        stages.Add("WBゲイン");
                    }

                    if (MatrixCheck.IsChecked == true)
                    {
                        stages.Add("カラーマトリクス");
                    }

                    stages.Add("デモザイク");
                    if (LutCheck.IsChecked == true)
                    {
                        stages.Add("黒レベル/ガンマ");
                    }
                }
                else if (LutCheck.IsChecked == true)
                {
                    stages.Add("表示LUT(黒/白点・ゲイン・ガンマ・コントラスト)");
                }

                return stages.Count == 0
                    ? "⚠ 8bit出力: 処理なしで単純に上位8bitへ丸めます(リニア)"
                    : "⚠ 8bit出力: " + string.Join(" → ", stages) + " を焼き込みます";
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Result = new SaveChoice(
            SelectedFormat,
            PackingCombo.SelectedIndex == 1 ? BitPacking.Msb : BitPacking.Lsb,
            EndianCombo.SelectedIndex == 1 ? Endianness.Big : Endianness.Little,
            LutCheck.IsChecked == true,
            WbCheck.IsChecked == true,
            MatrixCheck.IsChecked == true,
            DemosaicCheck.IsChecked == true,
            SidecarCheck.IsChecked == true,
            BuildSummary());
        DialogResult = true;
    }
}
