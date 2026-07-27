using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using RawViewer.App.Services;
using RawViewer.Core;

namespace RawViewer.App.Views;

/// <summary>
/// 欠陥画素検出のパラメータ指定・結果一覧ウィンドウ。
/// 行のダブルクリックで該当画素へジャンプするイベントを発火する。
/// </summary>
public partial class DefectPixelWindow : Window
{
    private DefectDetectionResult? _result;

    /// <summary>ウィンドウを生成する。</summary>
    public DefectPixelWindow()
    {
        InitializeComponent();
    }

    /// <summary>「検出実行」が押されたときに発火する(σ係数, 白点, 黒点)。</summary>
    public event Action<double, bool, bool>? RunRequested;

    /// <summary>結果行がダブルクリックされたときに発火する。</summary>
    public event Action<DefectPixel>? DefectActivated;

    /// <summary>「この欠陥を補正」が押されたときに発火する(検出結果, 補正方法)。</summary>
    public event Action<DefectDetectionResult, DefectCorrectionMethod>? CorrectionRequested;

    /// <summary>リスト表示用の行アイテム。</summary>
    public sealed record DefectRow(int Index, int X, int Y, int Code, string TypeLabel)
    {
        /// <summary>元の欠陥画素。</summary>
        public required DefectPixel Source { get; init; }
    }

    /// <summary>検出結果を表示する。</summary>
    /// <param name="result">検出結果。</param>
    /// <param name="maxCode">ビット深度の最大raw code。</param>
    public void ShowResult(DefectDetectionResult result, int maxCode)
    {
        _result = result;
        SummaryText.Text =
            $"mean={result.Statistics.Mean:F1}  σ={result.Statistics.Sigma:F2}  " +
            $"閾値: 白点>{result.HotThreshold:F1} / 黒点<{result.DeadThreshold:F1} (max={maxCode})\n" +
            $"検出: 白点 {result.HotCount} / 黒点 {result.DeadCount}" +
            (result.Truncated ? "  ⚠ 上限で打ち切り" : "");

        var rows = new List<DefectRow>(result.Defects.Count);
        for (int i = 0; i < result.Defects.Count; i++)
        {
            DefectPixel d = result.Defects[i];
            rows.Add(new DefectRow(
                i + 1, d.X, d.Y, d.Code,
                d.Type == DefectType.Hot ? "白点" : "黒点")
            {
                Source = d,
            });
        }

        DefectList.ItemsSource = rows;
        RunButton.IsEnabled = true;
        CorrectButton.IsEnabled = result.Defects.Count > 0;
    }

    private void OnCorrectClick(object sender, RoutedEventArgs e)
    {
        if (_result is null || _result.Defects.Count == 0)
        {
            return;
        }

        DefectCorrectionMethod method = CorrectionMethodCombo.SelectedIndex == 1
            ? DefectCorrectionMethod.Mean
            : DefectCorrectionMethod.Median;
        CorrectButton.IsEnabled = false;
        CorrectionRequested?.Invoke(_result, method);
    }

    /// <summary>実行失敗時にボタンを戻す。</summary>
    public void ResetRunButton()
    {
        RunButton.IsEnabled = true;
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(SigmaBox.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double sigma) || sigma <= 0)
        {
            MessageBox.Show(this, "σ係数は正の数値で指定してください。", "欠陥画素検出",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool hot = HotCheck.IsChecked == true;
        bool dead = DeadCheck.IsChecked == true;
        if (!hot && !dead)
        {
            MessageBox.Show(this, "白点・黒点の少なくとも一方を選択してください。", "欠陥画素検出",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RunButton.IsEnabled = false;
        RunRequested?.Invoke(sigma, hot, dead);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DefectList.SelectedItem is DefectRow row)
        {
            DefectActivated?.Invoke(row.Source);
        }
    }

    private string? BuildTable(char separator)
    {
        if (_result is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("x").Append(separator).Append("y").Append(separator)
            .Append("raw_code").Append(separator).Append("type").AppendLine();
        foreach (DefectPixel d in _result.Defects)
        {
            sb.Append(d.X).Append(separator).Append(d.Y).Append(separator)
                .Append(d.Code).Append(separator)
                .Append(d.Type == DefectType.Hot ? "hot" : "dead").AppendLine();
        }

        return sb.ToString();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.TrySetText(BuildTable('\t'));
    }

    private void OnSaveCsvClick(object sender, RoutedEventArgs e)
    {
        string? table = BuildTable(',');
        if (table is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = "defect_pixels.csv",
        };
        if (dialog.ShowDialog(this) == true)
        {
            ClipboardHelper.WriteTextOrWarn(this, dialog.FileName, table, "欠陥画素リスト保存");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
