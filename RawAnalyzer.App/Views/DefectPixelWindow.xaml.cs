using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Views;

/// <summary>
/// 欠陥画素検出のパラメータ指定・結果一覧ウィンドウ。
/// 行のダブルクリックで該当画素へジャンプするイベントを発火する。
/// </summary>
public partial class DefectPixelWindow : Window
{
    private const string InvalidSigmaMessage = "σ係数は正の数値で指定してください。";

    private DefectDetectionResult? _result;

    // 検出結果(またはそれを破棄した案内)を表示したことがあるか。まだなら破棄する一覧はなく、
    // 最初の案内のままにする
    private bool _resultShown;

    // 表示中の一覧を補正に使えるか(HDR表示中の検出結果は使えない)
    private bool _correctable;

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
    /// <remarks>
    /// 補正に使えない結果(HDR表示中の検出など)でも、一覧・移動・コピー・CSV保存は使える。
    /// 「この欠陥を補正」だけは有効にせず(押しても断られるだけになる)、理由を一覧の上に示す。
    /// </remarks>
    /// <param name="result">検出結果。</param>
    /// <param name="maxCode">ビット深度の最大raw code。</param>
    /// <param name="correctionUnavailableReason">
    /// この結果で補正できない理由と次にすること。補正できる結果ならnull。
    /// </param>
    public void ShowResult(
        DefectDetectionResult result, int maxCode, string? correctionUnavailableReason = null)
    {
        SetResult(result);
        _resultShown = true;
        _correctable = correctionUnavailableReason is null;
        var sb = new StringBuilder();
        if (result.ChannelThresholds.Count > 0)
        {
            // Bayerはチャネル間の感度差が大きいため、チャネル別の閾値で判定している。
            // HDR分割ビューは段(露光)ごとにも分けて判定している(左の段から長秒→短秒)
            bool bySegment = result.SegmentCount > 1;
            bool byChannel = result.ChannelThresholds.Any(t => t.Channel != BayerChannel.None);
            sb.Append(bySegment ? (byChannel ? "閾値 (段×チャネル別" : "閾値 (段別") : "閾値 (チャネル別")
                .Append(bySegment ? "・段は左から長秒→短秒" : "")
                .Append(", max=").Append(maxCode).AppendLine("):");
            foreach (DefectChannelThreshold t in result.ChannelThresholds)
            {
                sb.Append("  ");
                if (bySegment)
                {
                    sb.Append("段").Append(t.Segment + 1).Append(t.Channel != BayerChannel.None ? " " : "");
                }

                if (t.Channel != BayerChannel.None)
                {
                    sb.Append(t.Channel);
                }

                sb.Append(": mean=")
                    .Append(t.Mean.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(" σ=").Append(t.Sigma.ToString("F2", CultureInfo.InvariantCulture))
                    .Append("  白点>")
                    .Append(t.HotThreshold.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(" / 黒点<")
                    .Append(t.DeadThreshold.ToString("F1", CultureInfo.InvariantCulture))
                    .AppendLine();
            }
        }
        else
        {
            sb.Append($"mean={result.Statistics.Mean:F1}  σ={result.Statistics.Sigma:F2}  ")
                .AppendLine(
                    $"閾値: 白点>{result.HotThreshold:F1} / " +
                    $"黒点<{result.DeadThreshold:F1} (max={maxCode})");
        }

        sb.Append($"検出: 白点 {result.HotCount} / 黒点 {result.DeadCount}")
            .Append(result.Truncated ? "  ⚠ 上限で打ち切り" : "");
        if (correctionUnavailableReason is not null)
        {
            sb.AppendLine().Append(correctionUnavailableReason);
        }

        SummaryText.Text = sb.ToString();

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
        CorrectButton.IsEnabled = _correctable && result.Defects.Count > 0;
    }

    private void OnCorrectClick(object sender, RoutedEventArgs e)
    {
        if (_result is null || _result.Defects.Count == 0 || !_correctable)
        {
            return;
        }

        DefectCorrectionMethod method = CorrectionMethodCombo.SelectedIndex == 1
            ? DefectCorrectionMethod.Mean
            : DefectCorrectionMethod.Median;
        CorrectButton.IsEnabled = false;
        CorrectionRequested?.Invoke(_result, method);
    }

    /// <summary>
    /// 表示する画像が替わったため、検出結果を破棄して「未実行」の状態へ戻す(ウィンドウは開いたまま)。
    /// </summary>
    /// <remarks>
    /// 一覧は検出した画像・フレームの座標と画素値のもの。画像を開く・ページ/連番/フレームを送る・
    /// 処理結果へ差し替える・HDR表示に出入りすると表示中の画像のものではなくなる(一覧からの移動は
    /// 別の画素を指し、補正は断られる)ので、一覧を消して補正を押せなくし、「検出実行」で表示中の
    /// 画像を検出し直すよう案内する。右パネルでBayerパターンを変えたときも、判定と補正の前提が
    /// 変わるので同じく破棄する(<see cref="BayerChangedNotice"/>)。ウィンドウを閉じないので、
    /// 再生中に開いたウィンドウも次の送りで消えない。
    /// </remarks>
    /// <param name="notice">
    /// 一覧の上に出す案内(破棄した理由と次にすること)。省略時は表示中の画像が替わったことを示し、
    /// まだ何も検出していなければ最初の案内のままにする。
    /// </param>
    public void DiscardResult(string? notice = null)
    {
        if (notice is null && !_resultShown)
        {
            return;
        }

        _resultShown = true;
        ClearResult(notice ?? "表示中の画像が替わったため、検出結果を破棄しました。\n" +
            "「検出実行」で表示中の画像を検出し直してください。");
    }

    /// <summary>
    /// 欠陥補正で表示中の画像を補正後のものへ差し替えたときに、<see cref="DiscardResult"/> へ渡す案内。
    /// </summary>
    /// <remarks>
    /// 補正前の一覧は補正前の画像のもので、残すと「この欠陥を補正」を押せても表示中の画像のもの
    /// ではないと断られるだけになる。補正後の画像に残った欠陥を確かめるのは検出のやり直し。
    /// </remarks>
    /// <param name="count">補正した画素数。</param>
    /// <param name="methodLabel">補正方法の表示名(「メディアン」「平均」)。</param>
    /// <returns>案内の文。</returns>
    public static string CorrectionAppliedNotice(int count, string methodLabel) =>
        $"欠陥画素 {count} 個を{methodLabel}補間で補正しました(補正前の一覧は破棄しました)。\n" +
        "補正後の画像を確かめるには「検出実行」で検出し直してください。";

    /// <summary>
    /// 右パネルで表示中の画像のBayerパターンを変えたときに、<see cref="DiscardResult"/> へ渡す案内。
    /// </summary>
    /// <remarks>
    /// 検出はパターンのチャネル別の統計で閾値を決め(なしなら全画素の統計)、補正はパターンで選んだ近傍から
    /// 補う。前のパターンで検出した一覧は、変えた後のパターンでは欠陥ではない画素を含み得るので補正に使わせない。
    /// 画像は替わっていないので、画像の差し替えの案内(既定)とは分ける。
    /// </remarks>
    public const string BayerChangedNotice =
        "Bayerパターンを変更したため、検出結果を破棄しました(欠陥の判定と補正はBayerパターンで変わります)。\n" +
        "「検出実行」で検出し直してください。";

    /// <summary>一覧を消し、補正・コピー・CSV保存を押せない「未実行」の状態にして案内を出す。</summary>
    /// <param name="notice">一覧の上に出す案内。</param>
    private void ClearResult(string notice)
    {
        SetResult(null);
        DefectList.ItemsSource = null;
        CorrectButton.IsEnabled = false;
        SummaryText.Text = notice;
    }

    /// <summary>
    /// 表示する検出結果を差し替え、それを書き出す「コピー」「CSVで保存…」を押せるかを合わせる。
    /// </summary>
    /// <remarks>
    /// 結果の表示(<see cref="ShowResult"/>)と破棄(<see cref="ClearResult"/>)はここを通すので、ボタンと
    /// 書き出す表(<see cref="BuildTable"/>)の有無がずれない。以前は一覧がなくても(未実行・破棄した後)
    /// 押せて、押しても何も起きなかった。0 件も検出結果として書き出せる(見出しだけの表で、「検出して 0 件」
    /// を未実行と区別して欠陥がある場合と同じ形式で残せる)。
    /// </remarks>
    /// <param name="result">表示する検出結果。破棄したときはnull。</param>
    private void SetResult(DefectDetectionResult? result)
    {
        _result = result;
        CopyButton.IsEnabled = result is not null;
        SaveCsvButton.IsEnabled = result is not null;
    }

    /// <summary>実行失敗・キャンセル時にボタンを操作可能な状態へ戻す。</summary>
    public void ResetRunButton()
    {
        RunButton.IsEnabled = true;

        // 補正のキャンセル・エラー時も、補正に使える一覧が残っているなら再度押せるようにする
        CorrectButton.IsEnabled = _correctable && _result is { Defects.Count: > 0 };
    }

    private void OnSigmaTextChanged(object sender, TextChangedEventArgs e)
    {
        // 以前は「検出実行」を押すまで何も示さなかった。ファイル一覧の絞り込み欄と同じく、打った時点で
        // 赤枠とツールチップの理由で示す(実行時の確認は従来どおり)
        InputFeedback.Check(SigmaBox, NumericInput.TryParsePositive(SigmaBox.Text, out _), InvalidSigmaMessage);
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (!NumericInput.TryParsePositive(SigmaBox.Text, out double sigma))
        {
            MessageBox.Show(this, InvalidSigmaMessage, "欠陥画素検出",
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
        if (IsRowDoubleClick(e.OriginalSource as DependencyObject, e.ChangedButton)
            && DefectList.SelectedItem is DefectRow row)
        {
            DefectActivated?.Invoke(row.Source);
        }
    }

    /// <summary>一覧のダブルクリックが、行(ListViewItem)の上での左ボタンのダブルクリックか。</summary>
    /// <remarks>
    /// Control.MouseDoubleClick は処理済みのマウス押下にも発火し、右ボタンでも発火する。ListView 全体で受けると、
    /// スクロールバーの矢印・つまみ、列見出し(境界のダブルクリックで列幅を合わせる)のダブルクリックや右ボタンの
    /// ダブルクリックでも、選択中の行の欠陥へ移動して拡大してしまう。押した要素から ListView までの間に行がある
    /// ときだけ移動する。
    /// </remarks>
    /// <param name="originalSource">押した要素(MouseButtonEventArgs.OriginalSource)。</param>
    /// <param name="changedButton">押したボタン。</param>
    /// <returns>行の上での左ボタンのダブルクリックならtrue。</returns>
    internal static bool IsRowDoubleClick(DependencyObject? originalSource, MouseButton changedButton)
    {
        if (changedButton != MouseButton.Left)
        {
            return false;
        }

        // 行の中の文字(Run)はビジュアルではないので、論理ツリーの親もたどる
        for (DependencyObject? element = originalSource; element is not null and not ListView;)
        {
            if (element is ListViewItem)
            {
                return true;
            }

            DependencyObject? parent = element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : null;
            element = parent ?? LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    /// <summary>コピー(Excel用)・CSV保存に使う一覧の表を作る。</summary>
    /// <param name="separator">列の区切り文字。</param>
    /// <returns>見出し行付きの表。検出結果がなければnull。</returns>
    internal string? BuildTable(char separator)
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
