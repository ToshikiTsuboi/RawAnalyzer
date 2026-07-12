using System.Windows;
using RawViewer.Core;

namespace RawViewer.App.Views;

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
public sealed record SaveChoice(SaveFormat Format, BitPacking Packing, Endianness Endianness);

/// <summary>
/// 保存形式とrawオプションを選択するダイアログ。
/// </summary>
public partial class SaveDialog : Window
{
    private readonly long _totalPixels;
    private readonly bool _allowFloatRaw;

    /// <summary>ダイアログを生成する。</summary>
    /// <param name="totalPixels">対象画像の総画素数(巨大画像の形式制限判定用)。</param>
    /// <param name="allowFloatRaw">HDR合成中のfloat raw保存を選択肢に含めるか。</param>
    public SaveDialog(long totalPixels, bool allowFloatRaw = false)
    {
        InitializeComponent();
        _totalPixels = totalPixels;
        _allowFloatRaw = allowFloatRaw;
        if (allowFloatRaw)
        {
            FormatCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = "float raw (32bit・HDR合成結果)",
            });
        }

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

    private void OnFormatChanged(object sender, RoutedEventArgs e)
    {
        if (RawOptions is null)
        {
            return;
        }

        SaveFormat format = SelectedFormat;
        RawOptions.Visibility = format == SaveFormat.Raw ? Visibility.Visible : Visibility.Collapsed;

        // PNG/JPEGはWICで全画素をメモリ上に構築するため巨大画像では使えない
        bool tooBig = format is SaveFormat.Png16 or SaveFormat.Png8 or SaveFormat.Jpeg8
            && _totalPixels > RawLoader.DefaultInMemoryPixelThreshold;
        NoteText.Visibility = tooBig ? Visibility.Visible : Visibility.Collapsed;
        NoteText.Text = tooBig
            ? "⚠ 1億画素を超える画像はPNG/JPEGに保存できません(raw/TIFFを使用してください)"
            : "";
        SaveButton.IsEnabled = !tooBig;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Result = new SaveChoice(
            SelectedFormat,
            PackingCombo.SelectedIndex == 1 ? BitPacking.Msb : BitPacking.Lsb,
            EndianCombo.SelectedIndex == 1 ? Endianness.Big : Endianness.Little);
        DialogResult = true;
    }
}
