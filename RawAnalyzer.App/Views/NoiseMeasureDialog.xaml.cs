using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Views;

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
    private long _expectedReferenceSize;

    // 前回の UpdateSource で受け取ったビット深度の最大code(初回は0)。飽和コードの換算に使う
    private int _maxCode;

    // 対象画像の同一性と表示名。結果の破棄と、結果の本文に出典を書くのに使う
    private object? _source;
    private string _sourceName = "";

    // 結果欄の最初の案内(「測定実行」を押してください。)。対象が替わったら結果をこれに戻す
    private readonly string _initialResultText;

    /// <summary>ダイアログを生成する。</summary>
    /// <param name="sourceName">対象画像(A)の表示名。</param>
    /// <param name="initialFolder">参照ファイル選択の初期フォルダ。</param>
    /// <param name="maxCode">ビット深度の最大code(飽和信号レベルの初期値)。</param>
    /// <param name="hasRoi">ROIが選択されているか。</param>
    /// <param name="expectedReferenceSize">raw参照ファイルの期待バイト数(0なら検証しない)。</param>
    /// <param name="source">対象画像の同一性(差し替えを見分けるのに使う)。</param>
    public NoiseMeasureDialog(
        string sourceName, string? initialFolder, int maxCode, bool hasRoi,
        long expectedReferenceSize, object? source = null)
    {
        InitializeComponent();
        _initialResultText = ResultText.Text;
        UpdateSource(sourceName, initialFolder, maxCode, hasRoi, expectedReferenceSize, source);
    }

    /// <summary>「結果をコピー」でコピーする文字列(結果がなければ空)。</summary>
    internal string CopyText => _lastResultText;

    /// <summary>
    /// 対象画像が変わったときに表示と既定値を更新する。
    /// これを呼ばないと旧ファイル名・旧ビット深度の飽和コードのまま測定され、
    /// DRが最大で数stop誤る。
    /// </summary>
    /// <remarks>
    /// 飽和コードは σ と同じく raw code(対象のビット深度)の単位。ビット深度が変わったら(ビニング・フィルタの
    /// 16bit の結果、HDR合成、ビット深度の違うファイル)、既定値(前の最大code)のままなら新しい最大codeへ、
    /// 入れた値は同じ信号水準のコード(2^Δbit 倍)へ換算する。上限を超える値は上限へ戻す。
    /// 対象画像が替わったら(<paramref name="source"/> が前回と別のもの)、前の画像の測定結果を消して
    /// 「未測定」へ戻す。結果の本文には対象の名前・フレーム・ROI が出ないので、残すと新しい名前の下に前の
    /// 画像の値が並び、「結果をコピー」で新しい画像の値として記録されてしまう。
    /// </remarks>
    /// <param name="sourceName">対象画像(A)の表示名。</param>
    /// <param name="initialFolder">参照ファイル選択の初期フォルダ。</param>
    /// <param name="maxCode">ビット深度の最大code。</param>
    /// <param name="hasRoi">ROIが選択されているか。</param>
    /// <param name="expectedReferenceSize">raw参照ファイルの期待バイト数(0なら検証しない)。</param>
    /// <param name="source">対象画像の同一性(差し替えを見分けるのに使う)。</param>
    public void UpdateSource(
        string sourceName, string? initialFolder, int maxCode, bool hasRoi,
        long expectedReferenceSize, object? source = null)
    {
        SourceText.Text = $"対象 A: {sourceName}";
        Tag = initialFolder;
        _expectedReferenceSize = expectedReferenceSize;
        _sourceName = sourceName;
        if (!ReferenceEquals(source, _source))
        {
            _source = source;
            _lastResultText = "";
            ResultText.Text = _initialResultText;
        }

        // 前の画像の飽和コードが別のビット深度の単位のまま、または上限を超えて残らないようにする。
        // 以前は上限を超えるときしか直さず、12bit の既定値 4095 が 16bit の画像に残って DR が約24dB 小さく出た
        bool parsed = NumericInput.TryParsePositive(SaturationBox.Text, out double current);
        int previousMax = _maxCode;
        _maxCode = maxCode;
        if (parsed && previousMax > 0 && previousMax != maxCode)
        {
            current = current == previousMax
                ? maxCode
                : Math.Round(current * (maxCode + 1.0) / (previousMax + 1.0), 3);
            SaturationBox.Text = Math.Min(current, maxCode).ToString("0.###", CultureInfo.InvariantCulture);
        }
        else if (!parsed || current > maxCode)
        {
            SaturationBox.Text = maxCode.ToString(CultureInfo.InvariantCulture);
        }

        SetRoiAvailability(hasRoi);
    }

    /// <summary>ROIの有無に応じて「ROI内のみ」チェックの状態を更新する。</summary>
    /// <param name="hasRoi">ROIが選択されているか。</param>
    public void SetRoiAvailability(bool hasRoi)
    {
        RoiCheck.IsEnabled = hasRoi;
        if (!hasRoi)
        {
            // チェックが残ったままだと「ROI内のみ」表示で全画面測定になる
            RoiCheck.IsChecked = false;
        }
    }

    /// <summary>「測定実行」が押されたときに発火する。</summary>
    public event Action<NoiseMeasureRequest>? MeasureRequested;

    /// <summary>測定結果を表示する。</summary>
    /// <param name="measurement">測定結果。</param>
    /// <param name="bitDepth">ビット深度(表示用)。</param>
    /// <param name="pairPath">2枚目に使ったファイル名(単一測定ならnull)。</param>
    public void ShowResult(
        NoiseMeasurement measurement, int bitDepth, string? pairPath, bool perChannel = false)
    {
        // コピーした値の出典が分かるよう、測った対象を本文にも書く
        var sb = new StringBuilder();
        sb.Append("対象 A: ").Append(_sourceName)
            .AppendLine(RoiCheck.IsChecked == true ? " (ROI内)" : "");
        sb.Append("評価画素数: ").Append(measurement.SampleCount.ToString("N0"))
            .Append("    平均: ").AppendLine(measurement.Mean.ToString("F2"));
        sb.Append("測定方法: ").AppendLine(pairPath is null
            ? "単一フレーム(時間ノイズとFPNは分離できません)"
            : $"2枚差分 (B = {pairPath})");
        if (perChannel)
        {
            // チャネル混合だと感度差がσ_FPNに乗るため、Coreがチャネル別に集計している
            sb.AppendLine("空間統計: Bayerチャネル別に算出して合成(チャネル間の感度差を除外)");
        }

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
        if (!NumericInput.TryParsePositive(SaturationBox.Text, out double saturation))
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

        // raw参照は対象Aのフォーマットで強制解釈されるため、
        // サイズが違うと行ストライドがずれて無相関の差分になり、σ_FPN=0 / DR過小報告になる。
        if (reference.Length > 0 && _expectedReferenceSize > 0 && IsRawPath(reference))
        {
            long actual = SafeLength(reference);
            if (actual >= 0 && actual != _expectedReferenceSize)
            {
                string message =
                    $"2枚目のファイルサイズが対象Aと一致しません。{Environment.NewLine}" +
                    $"期待: {_expectedReferenceSize:N0} バイト / 実際: {actual:N0} バイト" +
                    $"{Environment.NewLine}{Environment.NewLine}" +
                    "対象Aのフォーマットで強制的に読み込むため、測定値が正しくない可能性があります。" +
                    $"{Environment.NewLine}続行しますか?";
                if (MessageBox.Show(this, message, "ノイズ測定",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                {
                    return;
                }
            }
        }

        RunButton.IsEnabled = false;
        MeasureRequested?.Invoke(new NoiseMeasureRequest(
            reference.Length > 0 ? reference : null,
            saturation,
            RoiCheck.IsChecked == true));
    }

    private static bool IsRawPath(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".raw", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".bin", StringComparison.OrdinalIgnoreCase);
    }

    private static long SafeLength(string path)
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

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.TrySetText(_lastResultText);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
