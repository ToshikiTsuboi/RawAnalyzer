using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using RawViewer.Core;

namespace RawViewer.App.Views;

/// <summary>ノイズ測定の実行要求。</summary>
/// <param name="ReferencePath">2枚目フレームのパス(単一測定ならnull)。</param>
/// <param name="SaturationCode">飽和信号レベル(raw code)。</param>
/// <param name="UseRoi">ROI内のみで測定するか。</param>
public sealed record NoiseMeasureRequest(
    string? ReferencePath, double SaturationCode, bool UseRoi);

/// <summary>
/// 時間ノイズ・FPN・ダイナミックレンジの測定ダイアログ。
/// </summary>
public partial class NoiseMeasureDialog : Window
{
    private string _lastResultText = "";

    /// <summary>ダイアログを生成する。</summary>
    /// <param name="sourceName">対象画像(A)の表示名。</param>
    /// <param name="initialFolder">参照ファイル選択の初期フォルダ。</param>
    /// <param name="defaultSaturation">飽和信号レベルの初期値(通常はビット深度の最大code)。</param>
    /// <param name="hasRoi">ROIが選択されているか。</param>
    public NoiseMeasureDialog(
        string sourceName, string? initialFolder, int defaultSaturation, bool hasRoi)
    {
        InitializeComponent();
        SourceText.Text = $"対象 A: {sourceName}";
        Tag = initialFolder;
        SaturationBox.Text = defaultSaturation.ToString(CultureInfo.InvariantCulture);
        RoiCheck.IsEnabled = hasRoi;
        RoiCheck.IsChecked = hasRoi;
    }

    /// <summary>「測定実行」が押されたときに発火する。</summary>
    public event Action<NoiseMeasureRequest>? MeasureRequested;

    /// <summary>測定結果を表示する。</summary>
    /// <param name="measurement">測定結果。</param>
    /// <param name="bitDepth">ビット深度(表示用)。</param>
    /// <param name="pairPath">2枚目に使ったファイル名(単一測定ならnull)。</param>
    public void ShowResult(NoiseMeasurement measurement, int bitDepth, string? pairPath)
    {
        var sb = new StringBuilder();
        sb.Append("評価画素数: ").Append(measurement.SampleCount.ToString("N0"))
            .Append("    平均: ").AppendLine(measurement.Mean.ToString("F2"));
        sb.Append("測定方法: ").AppendLine(pairPath is null
            ? "単一フレーム(時間ノイズとFPNは分離できません)"
            : $"2枚差分 (B = {pairPath})");
        sb.AppendLine();

        sb.Append("σ_total  (時間+FPN) : ")
            .Append(measurement.SigmaTotal.ToString("F3")).AppendLine(" LSB");
        if (!double.IsNaN(measurement.SigmaTemporal))
        {
            sb.Append("σ_temporal (σ_diff/√2): ")
                .Append(measurement.SigmaTemporal.ToString("F3")).AppendLine(" LSB");
            sb.Append("σ_FPN    (固定パターン) : ")
                .Append(measurement.SigmaFpn.ToString("F3")).AppendLine(" LSB");
        }

        sb.AppendLine();
        sb.Append("飽和信号レベル: ")
            .Append(measurement.SaturationCode.ToString("F0"))
            .Append(" LSB (").Append(bitDepth).AppendLine("bit)");

        if (!double.IsNaN(measurement.DynamicRangeTemporalDb))
        {
            sb.Append("DR (時間ノイズ基準) : ")
                .Append(measurement.DynamicRangeTemporalDb.ToString("F2")).Append(" dB  /  ")
                .Append(measurement.DynamicRangeTemporalStops.ToString("F2")).AppendLine(" stop");
        }

        sb.Append("DR (FPN込み総ノイズ): ")
            .Append(measurement.DynamicRangeTotalDb.ToString("F2")).Append(" dB  /  ")
            .Append(measurement.DynamicRangeTotalStops.ToString("F2")).AppendLine(" stop");

        if (double.IsNaN(measurement.SigmaTemporal))
        {
            sb.AppendLine();
            sb.Append("※ 2枚目を指定すると時間ノイズ基準の正しいDRを算出できます");
        }

        _lastResultText = sb.ToString();
        ResultText.Text = _lastResultText;
        RunButton.IsEnabled = true;
    }

    /// <summary>実行失敗時にボタンを戻す。</summary>
    public void ResetRunButton()
    {
        RunButton.IsEnabled = true;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Raw (*.raw;*.bin)|*.raw;*.bin|すべてのファイル (*.*)|*.*",
        };
        if (Tag is string folder && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            ReferenceBox.Text = dialog.FileName;
        }
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(SaturationBox.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double saturation) || saturation <= 0)
        {
            MessageBox.Show(this, "飽和信号レベルは正の数値で指定してください。", "ノイズ測定",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string reference = ReferenceBox.Text.Trim();
        if (reference.Length > 0 && !File.Exists(reference))
        {
            MessageBox.Show(this, "2枚目のファイルが見つかりません。", "ノイズ測定",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RunButton.IsEnabled = false;
        MeasureRequested?.Invoke(new NoiseMeasureRequest(
            reference.Length > 0 ? reference : null,
            saturation,
            RoiCheck.IsChecked == true));
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_lastResultText.Length > 0)
        {
            Clipboard.SetText(_lastResultText);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
