using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Views;

/// <summary>グラフに重ねて描く線(射影の各位置の最大・最小など)。統計・表には使わない。</summary>
/// <param name="Values">値(主の線と同じ長さ・同じ座標)。</param>
/// <param name="Stroke">線の色。</param>
internal sealed record ProfileSeries(double[] Values, Color Stroke);

/// <summary>グラフに出すデータ。</summary>
/// <param name="Values">主の線(統計・表・自動の縦軸の元)。</param>
/// <param name="MaxCode">ビット深度の最大 raw code(縦軸の全範囲)。</param>
/// <param name="CoordinateOffset">位置 0 の横軸の座標(ROI の左端・上端など)。</param>
/// <param name="IntegerValues">
/// 値が整数の raw code か(統計の最小・最大・P-P を整数で出す)。射影(平均)は false で、小数2桁で出す。
/// </param>
/// <param name="MarkerIndex">縦線で示す位置(断面の基準点。なければ null)。</param>
/// <param name="ExtraSeries">重ねて描く線(なければ null)。</param>
internal sealed record ProfilePlotData(
    double[] Values,
    int MaxCode,
    long CoordinateOffset = 0,
    bool IntegerValues = true,
    int? MarkerIndex = null,
    IReadOnlyList<ProfileSeries>? ExtraSeries = null);

/// <summary>
/// ラインプロファイル窓と射影の窓で共有するグラフ。統計、縦軸の全範囲・自動・手動、ホイールでの拡大・縮小、
/// ドラッグでの移動、ダブルクリックで全体表示、データ・統計のコピーと CSV 保存を持つ。
/// </summary>
/// <remarks>
/// 拡大・移動は表示だけを変える。統計・コピー・CSV は常に主の線の全体が対象。表の列(座標の見出しなど)は
/// 窓ごとに違うので、窓が <see cref="TableBuilder"/> で作る。
/// </remarks>
public partial class ProfilePlotView : UserControl
{
    private static readonly Color PrimaryStroke = Color.FromRgb(0x5B, 0x9D, 0xD9);

    private ProfilePlotData _data = new(Array.Empty<double>(), 65535);

    // 統計のキャッシュ(算出元の配列参照が変わったときだけ再計算する)
    private double[]? _statsSource;
    private ProfileStatistics _stats;

    // 重ねる線の値の範囲のキャッシュ(自動の縦軸に使う)
    private IReadOnlyList<ProfileSeries>? _extentSource;
    private (double Min, double Max)? _extraExtent;

    private ProfileAxisRange _axisRange = ProfileAxisRange.Full(65535);
    private ProfileAxisRange? _manualRange;
    private bool _extraSeriesVisible = true;
    private string? _statsMessage;
    private string? _busyMessage;
    private bool _ready;
    private bool _updatingScaleControls;

    /// <summary>グラフを生成する。</summary>
    public ProfilePlotView()
    {
        InitializeComponent();
        _ready = true;
        Redraw();
    }

    /// <summary>表(データのコピー・CSV)を作る。区切り文字を受け取り、データがなければ null を返す。</summary>
    internal Func<char, string?>? TableBuilder { get; set; }

    /// <summary>CSV 保存の既定のファイル名。</summary>
    internal Func<string>? CsvFileName { get; set; }

    /// <summary>CSV 保存に失敗したときの知らせの見出し。</summary>
    internal string CsvSaveCaption { get; set; } = "プロファイル保存";

    /// <summary>主の線の値。</summary>
    internal double[] Values => _data.Values;

    /// <summary>位置 0 の横軸の座標。</summary>
    internal long CoordinateOffset => _data.CoordinateOffset;

    /// <summary>計算中(前のデータを薄く残し、コピー・CSV はしない)か。</summary>
    internal bool IsBusy => _busyMessage is not null;

    /// <summary>表示中の縦軸の範囲。</summary>
    internal ProfileAxisRange AxisRange => _axisRange;

    /// <summary>重ねる線を描くか(自動の縦軸も重ねる線を含めて決める)。</summary>
    internal bool ExtraSeriesVisible
    {
        get => _extraSeriesVisible;
        set
        {
            if (_extraSeriesVisible == value) return;
            _extraSeriesVisible = value;
            Redraw();
        }
    }

    private bool ManualScale => YScaleCombo.SelectedIndex == 2;

    /// <summary>
    /// データを設定して再描画する(計算中・メッセージの表示を終える)。データの長さか位置 0 の座標が変わったら
    /// 横軸の拡大を全体へ戻す(縦軸の設定は保つ)。
    /// </summary>
    /// <param name="data">データ。</param>
    internal void SetData(ProfilePlotData data)
    {
        int previousCount = _data.Values.Length;
        long previousOffset = _data.CoordinateOffset;
        EndPan();
        _data = data with { MaxCode = Math.Max(1, data.MaxCode) };
        _statsMessage = null;
        _busyMessage = null;
        if (previousCount != data.Values.Length || previousOffset != data.CoordinateOffset) _horizontalRange = null;
        Redraw();
    }

    /// <summary>
    /// データを消して、統計の欄(と、あればグラフの上)にメッセージを出す(範囲外・断る理由など)。
    /// 前のデータを残すと別の対象の値と誤読されるので消す。縦軸の設定は保つ。
    /// </summary>
    /// <param name="statsMessage">統計の欄に出すメッセージ。</param>
    /// <param name="maxCode">表示中の画像のビット深度の最大 raw code。</param>
    /// <param name="plotMessage">グラフの上に出すメッセージ(出さなければ null)。</param>
    internal void ShowMessage(string statsMessage, int maxCode, string? plotMessage = null)
    {
        SetData(new ProfilePlotData(Array.Empty<double>(), maxCode));
        _statsMessage = statsMessage;
        _overlayMessage = plotMessage;
        Redraw();
    }

    /// <summary>
    /// 計算中を示す。前のデータは薄く残し(横軸の拡大を保つため)、統計の欄とグラフの上に知らせを出す。
    /// 計算中はデータ・統計のコピーと CSV 保存をしない(前の対象の値を出さない)。
    /// </summary>
    /// <param name="message">知らせ。</param>
    internal void ShowBusy(string message)
    {
        EndPan();
        _busyMessage = message;
        Redraw();
    }

    // グラフの上に出すメッセージ(ShowMessage のもの)。データを設定したら消す
    private string? _overlayMessage;

    /// <summary>横軸の見出しとツールチップを設定する。</summary>
    /// <param name="title">見出し。</param>
    /// <param name="toolTip">ツールチップ。</param>
    internal void SetAxisLabels(string title, string toolTip)
    {
        XAxisTitle.Text = title;
        XAxisCanvas.ToolTip = toolTip;
    }

    /// <summary>横軸の拡大を全体へ戻す(向きの切り替えなど、同じ長さでも別の座標になったとき)。</summary>
    internal void ResetHorizontalRange()
    {
        EndPan();
        _horizontalRange = null;
        Redraw();
    }

    /// <summary>ドラッグを終え、縦軸の設定を閉じる(窓を閉じるとき)。</summary>
    internal void EndInteraction()
    {
        EndPan();
        YScalePopup.IsOpen = false;
    }

    /// <summary>
    /// 主の線の統計。中央値の算出でソート用配列を確保するため、データが変わったときだけ計算してキャッシュする
    /// (リサイズのたびに N=46341 で 371KB の LOH 割り当てが発生していた)。
    /// </summary>
    internal ProfileStatistics Statistics
    {
        get
        {
            double[] data = _data.Values;
            if (!ReferenceEquals(data, _statsSource))
            {
                _statsSource = data;
                _stats = ImageAnalysis.ComputeProfileStatistics(data);
            }

            return _stats;
        }
    }

    private void OnYScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingScaleControls) return;
        // 初回の手動切替は現在の表示範囲を固定。その後は最後に適用した値を復元する。
        if (ManualScale) _manualRange ??= _axisRange;
        YMinimumBox.IsEnabled = ManualScale;
        YMaximumBox.IsEnabled = ManualScale;
        Redraw();
        UpdateScaleInputs();
    }

    private void UpdateScaleInputs()
    {
        _updatingScaleControls = true;
        // 自動表示は読みやすく、手動入力へ移すときは丸めず値を引き継ぐ。
        string format = ManualScale ? "G17" : "G8";
        YMinimumBox.Text = _axisRange.Minimum.ToString(format, CultureInfo.CurrentCulture);
        YMaximumBox.Text = _axisRange.Maximum.ToString(format, CultureInfo.CurrentCulture);
        YMinimumBox.IsEnabled = ManualScale;
        YMaximumBox.IsEnabled = ManualScale;
        _updatingScaleControls = false;
        ValidateScaleInputs();
    }

    private bool ValidateScaleInputs()
    {
        bool valid = ProfileAxisRange.TryParse(YMinimumBox.Text, YMaximumBox.Text,
            CultureInfo.CurrentCulture, out _);
        ApplyYScaleButton.IsEnabled = ManualScale && valid;
        YScaleErrorText.Visibility = ManualScale && !valid ? Visibility.Visible : Visibility.Collapsed;
        MarkInvalidScaleInputs();
        return valid;
    }

    /// <summary>
    /// 手動の縦軸の欄のうち悪いほうを、ファイル一覧の絞り込み欄と同じく赤枠とツールチップの理由で示す。
    /// </summary>
    /// <remarks>
    /// 以前は欄の下の説明だけで、どちらの欄が悪いのかは欄の見た目で分からなかった。読めない欄はその欄に、
    /// 「最小 ＜ 最大」になっていないときは最大の欄に出す。手動でないときは欄を使わない(無効)ので知らせない。
    /// </remarks>
    private void MarkInvalidScaleInputs()
    {
        const string NotNumber = "有限の数値で指定してください。現在の表示範囲は変更していません。";
        bool minValid = ProfileAxisRange.TryParseLimit(YMinimumBox.Text, CultureInfo.CurrentCulture, out double min);
        bool maxValid = ProfileAxisRange.TryParseLimit(YMaximumBox.Text, CultureInfo.CurrentCulture, out double max);
        string? maxError = !maxValid ? NotNumber
            : !minValid ? null
            : !(min < max) ? "最小値より大きい値を指定してください。現在の表示範囲は変更していません。"
            : !double.IsFinite(max - min) ? "最小と最大の差が大きすぎます。現在の表示範囲は変更していません。"
            : null;
        InputFeedback.SetError(YMinimumBox, ManualScale && !minValid ? NotNumber : null);
        InputFeedback.SetError(YMaximumBox, ManualScale ? maxError : null);
    }

    private void OnYLimitsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready && !_updatingScaleControls) ValidateScaleInputs();
    }

    private void OnYLimitsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyManualScale();
        e.Handled = true;
    }

    private void OnApplyYScaleClick(object sender, RoutedEventArgs e) => ApplyManualScale();

    private void ApplyManualScale()
    {
        if (!ManualScale || !ValidateScaleInputs()
            || !ProfileAxisRange.TryParse(YMinimumBox.Text, YMaximumBox.Text,
                CultureInfo.CurrentCulture, out ProfileAxisRange range)) return;
        _manualRange = range;
        Redraw();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Redraw();
    }

    /// <summary>自動の縦軸の範囲。重ねる線を描くときはその値も入るようにする。</summary>
    private ProfileAxisRange AutoRange(ProfileStatistics stats)
    {
        if (stats.Count == 0) return ProfileAxisRange.Full(_data.MaxCode);
        double low = stats.Min;
        double high = stats.Max;
        if (_extraSeriesVisible && ExtraExtent() is { } extent)
        {
            low = Math.Min(low, extent.Min);
            high = Math.Max(high, extent.Max);
        }

        return ProfileAxisRange.Auto(low, high, _data.MaxCode);
    }

    private (double Min, double Max)? ExtraExtent()
    {
        IReadOnlyList<ProfileSeries>? series = _data.ExtraSeries;
        if (!ReferenceEquals(series, _extentSource))
        {
            _extentSource = series;
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            foreach (double value in series?.SelectMany(s => s.Values) ?? Enumerable.Empty<double>())
            {
                if (!double.IsFinite(value)) continue;
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            _extraExtent = min <= max ? (min, max) : null;
        }

        return _extraExtent;
    }

    /// <summary>再描画する(データ・縦軸・窓の大きさが変わったとき)。</summary>
    internal void Redraw()
    {
        if (!_ready || PlotCanvas is null)
        {
            return;
        }

        PlotCanvas.Children.Clear();
        double[] data = _data.Values;
        double width = PlotCanvas.ActualWidth;
        double height = PlotCanvas.ActualHeight;
        int maxCode = _data.MaxCode;

        ProfileStatistics stats = Statistics;
        _axisRange = YScaleCombo.SelectedIndex switch
        {
            1 => AutoRange(stats),
            2 => _manualRange ?? ProfileAxisRange.Full(maxCode),
            _ => ProfileAxisRange.Full(maxCode),
        };
        MaxLabel.Text = _axisRange.Maximum.ToString("G8", CultureInfo.CurrentCulture);
        MinLabel.Text = _axisRange.Minimum.ToString("G8", CultureInfo.CurrentCulture);
        if (MaxLabel.Text == MinLabel.Text)
        {
            MaxLabel.Text = _axisRange.Maximum.ToString("G17", CultureInfo.CurrentCulture);
            MinLabel.Text = _axisRange.Minimum.ToString("G17", CultureInfo.CurrentCulture);
        }

        const string axisHint = "\n右クリックで縦軸スケールを設定";
        MaxLabel.ToolTip = _axisRange.Maximum.ToString("G17", CultureInfo.CurrentCulture) + axisHint;
        MinLabel.ToolTip = _axisRange.Minimum.ToString("G17", CultureInfo.CurrentCulture) + axisHint;
        double middle = _axisRange.Minimum + (_axisRange.Maximum - _axisRange.Minimum) / 2;
        MiddleLabel.Text = middle.ToString("G8", CultureInfo.CurrentCulture);
        MiddleLabel.ToolTip = middle.ToString("G17", CultureInfo.CurrentCulture) + axisHint;
        if (_ready && !ManualScale) UpdateScaleInputs();

        // 断面は整数の raw code なので最小・最大・P-P は整数で出す。射影は直交方向の平均(実数)で 1 code 未満の
        // 列ムラを見るための値なので、整数に丸めず平均・σ と同じ小数2桁で出す
        string extremeFormat = _data.IntegerValues ? "F0" : "F2";
        StatsText.Text = _busyMessage ?? _statsMessage ?? (stats.Count == 0
            ? "—"
            : $"N={stats.Count}   平均 {stats.Mean:F2}   最小 {stats.Min.ToString(extremeFormat)}   " +
              $"最大 {stats.Max.ToString(extremeFormat)}   中央値 {stats.Median:F1}   σ {stats.Sigma:F2}   " +
              $"P-P {(stats.Max - stats.Min).ToString(extremeFormat)}");

        // 計算中は前のデータを薄く残し、上に知らせを出す。断る理由などもグラフの上に出す
        string? overlay = _busyMessage ?? _overlayMessage;
        OverlayText.Text = overlay ?? "";
        OverlayText.Visibility = overlay is null ? Visibility.Collapsed : Visibility.Visible;
        double opacity = IsBusy ? 0.35 : 1;
        PlotCanvas.Opacity = opacity;
        XAxisCanvas.Opacity = opacity;
        bool exportable = !IsBusy && data.Length > 0;
        CopyDataMenu.IsEnabled = exportable;
        CopyStatsMenu.IsEnabled = exportable;
        SaveCsvMenu.IsEnabled = exportable;

        DrawHorizontalAxis(width, height);
        if (data.Length == 0 || width < 4 || height < 4)
        {
            return;
        }

        // 平均・±σのガイド線
        AddGuideLine(stats.Mean, height, width, Color.FromArgb(0x70, 0x7E, 0xCB, 0x72));
        AddGuideLine(stats.Mean + stats.Sigma, height, width,
            Color.FromArgb(0x40, 0x9A, 0x9A, 0x95));
        AddGuideLine(stats.Mean - stats.Sigma, height, width,
            Color.FromArgb(0x40, 0x9A, 0x9A, 0x95));

        // 重ねる線は主の線の下に描く
        if (_extraSeriesVisible && _data.ExtraSeries is { } extras)
        {
            foreach (ProfileSeries series in extras)
            {
                AddSeries(series.Values, series.Stroke, width, height);
            }
        }

        AddSeries(data, PrimaryStroke, width, height);

        if (data.Length == 1 && _axisRange.Contains(data[0]))
        {
            var dot = new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(PrimaryStroke) };
            Canvas.SetLeft(dot, ProfilePlotNavigation.ToCanvasX(0, HorizontalRange, width) - 2);
            Canvas.SetTop(dot, _axisRange.ToCanvasY(data[0], height) - 2);
            PlotCanvas.Children.Add(dot);
        }

        // 基準点のマーカー(断面のみ)
        if (_data.MarkerIndex is { } index && index >= 0 && index < data.Length && HorizontalRange.Contains(index))
        {
            double markerX = ProfilePlotNavigation.ToCanvasX(index, HorizontalRange, width);
            PlotCanvas.Children.Add(new Line
            {
                X1 = markerX,
                X2 = markerX,
                Y1 = 0,
                Y2 = height,
                Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0xD9, 0x9B, 0x5B)),
                StrokeThickness = 1,
            });
        }
    }

    private void AddSeries(double[] values, Color stroke, double width, double height)
    {
        if (values.Length == 0) return;

        // 拡大中は表示区間と隣接点だけを読む。間引きでも鋭いピークは残す。
        IReadOnlyList<Point> points = ProfilePlotNavigation.SampleVisible(values, HorizontalRange, width);
        PlotCanvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = _axisRange.BuildGeometry(points, height),
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = 1,
        });
    }

    private void AddGuideLine(double value, double canvasHeight, double width, Color color)
    {
        if (!_axisRange.Contains(value))
        {
            return;
        }

        PlotCanvas.Children.Add(new Line
        {
            X1 = 0,
            X2 = width,
            Y1 = _axisRange.ToCanvasY(value, canvasHeight),
            Y2 = _axisRange.ToCanvasY(value, canvasHeight),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 },
        });
    }

    /// <summary>表・統計のコピーに出す値(射影の小数を落とさない有効数字9桁)。</summary>
    /// <param name="value">値。</param>
    /// <returns>文字列。</returns>
    internal static string FormatValue(double value) => value.ToString("G9", CultureInfo.InvariantCulture);

    /// <summary>データの表(コピー・CSV)。計算中・データがなければ null。</summary>
    /// <param name="separator">区切り文字。</param>
    /// <returns>表。</returns>
    internal string? BuildTable(char separator) =>
        IsBusy || _data.Values.Length == 0 ? null : TableBuilder?.Invoke(separator);

    /// <summary>統計のコピー(Excel 貼り付け用の TSV)。計算中・データがなければ null。</summary>
    /// <returns>表。</returns>
    internal string? BuildStatisticsTable()
    {
        // データがない(基準点が範囲外など)ときは、0 を並べた統計を実測値のようにコピーしない
        // (データのコピー・CSV保存と同じ)
        if (IsBusy || _data.Values.Length == 0)
        {
            return null;
        }

        ProfileStatistics stats = Statistics;
        var sb = new StringBuilder();
        sb.AppendLine("metric\tvalue");
        sb.Append("N\t").Append(stats.Count).AppendLine();
        sb.Append("mean\t").Append(FormatValue(stats.Mean)).AppendLine();
        sb.Append("min\t").Append(FormatValue(stats.Min)).AppendLine();
        sb.Append("max\t").Append(FormatValue(stats.Max)).AppendLine();
        sb.Append("median\t").Append(FormatValue(stats.Median)).AppendLine();
        sb.Append("sigma\t").Append(FormatValue(stats.Sigma)).AppendLine();
        return sb.ToString();
    }

    private void OnCopyDataClick(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.TrySetText(BuildTable('\t'));
    }

    private void OnCopyStatsClick(object sender, RoutedEventArgs e)
    {
        if (BuildStatisticsTable() is { } table)
        {
            ClipboardHelper.TrySetText(table);
        }
    }

    private void OnSaveCsvClick(object sender, RoutedEventArgs e)
    {
        string? table = BuildTable(',');
        if (table is null)
        {
            return;
        }

        if (Window.GetWindow(this) is not { } owner)
        {
            return;
        }

        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = CsvFileName?.Invoke() ?? "profile.csv" };
        if (dialog.ShowDialog(owner) == true)
        {
            ClipboardHelper.WriteTextOrWarn(owner, dialog.FileName, table, CsvSaveCaption);
        }
    }
}
