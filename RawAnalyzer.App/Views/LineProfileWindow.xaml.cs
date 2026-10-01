using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Views;

/// <summary>
/// 指定画素を通る水平/垂直ラインのraw値折れ線と、その統計を表示するウィンドウ。
/// ROI選択時はROI内を直交方向に平均した射影プロファイルも表示できる。
/// </summary>
public partial class LineProfileWindow : Window
{
    private double[] _rowProfile = Array.Empty<double>();
    private double[] _columnProfile = Array.Empty<double>();
    private double[] _horizontalProjection = Array.Empty<double>();
    private double[] _verticalProjection = Array.Empty<double>();
    private RegionOfInterest? _roi;

    // チャネル分割表示で描いたROIの射影か(射影の座標は元画像ではなく分割表示(タイル)の座標)
    private bool _projectionInSplitView;

    // 統計のキャッシュ(算出元の配列参照が変わったときだけ再計算する)
    private double[]? _statsSource;
    private ProfileStatistics _stats;

    private int _pointX;
    private int _pointY;

    // 基準点が表示中の画像の範囲外のとき、その画像の寸法(断面を出しているときは null)
    private (int Width, int Height)? _outsideImage;
    private int _maxCode = 65535;
    private ProfileAxisRange _axisRange = ProfileAxisRange.Full(65535);
    private ProfileAxisRange? _manualRange;
    private bool _ready;
    private bool _updatingScaleControls;

    /// <summary>ウィンドウを生成する。</summary>
    public LineProfileWindow()
    {
        InitializeComponent();
        _ready = true;
        Redraw();
        Closed += (_, _) =>
        {
            EndPan();
            YScalePopup.IsOpen = false;
        };
    }

    internal ProfileAxisRange AxisRange => _axisRange;

    private bool ManualScale => YScaleCombo.SelectedIndex == 2;

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
        return valid;
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

    /// <summary>水平/垂直・射影の切替時に発火する(true=水平)。</summary>
    public event Action<bool>? DirectionChanged;

    /// <summary>現在水平プロファイル表示か。</summary>
    public bool IsHorizontal => HorizontalRadio?.IsChecked != false;

    /// <summary>現在のプロファイル基準点。</summary>
    public (int X, int Y) CurrentPoint => (_pointX, _pointY);

    /// <summary>基準点が表示中の画像の範囲外で、断面を出していないか。</summary>
    public bool IsOutsideImage => _outsideImage is not null;

    /// <summary>
    /// プロファイルデータを設定して再描画する。
    /// </summary>
    /// <param name="rowProfile">クリック行の水平プロファイル(raw code)。</param>
    /// <param name="columnProfile">クリック列の垂直プロファイル(raw code)。</param>
    /// <param name="horizontalProjection">ROI内の水平射影(ROIなしなら空)。</param>
    /// <param name="verticalProjection">ROI内の垂直射影(ROIなしなら空)。</param>
    /// <param name="roi">対象ROI(なければnull)。</param>
    /// <param name="pointX">基準点X(元画像の座標。クリックした点、送り・差し替えの後は同じ点)。</param>
    /// <param name="pointY">基準点Y(元画像の座標)。</param>
    /// <param name="maxCode">ビット深度の最大raw code。</param>
    /// <param name="projectionInSplitView">
    /// チャネル分割表示で描いたROIの射影か。射影の座標は元画像ではなく、ROIを描いた分割表示(タイル)の座標になる。
    /// </param>
    public void SetProfiles(
        double[] rowProfile,
        double[] columnProfile,
        double[] horizontalProjection,
        double[] verticalProjection,
        RegionOfInterest? roi,
        int pointX,
        int pointY,
        int maxCode,
        bool projectionInSplitView = false)
    {
        ApplyData(rowProfile, columnProfile, horizontalProjection, verticalProjection, roi,
            pointX, pointY, maxCode, outsideImage: null, projectionInSplitView);
    }

    /// <summary>
    /// 基準点が表示中の画像の範囲外で、断面を出せないことを示す(前の断面・射影・統計は消す)。
    /// </summary>
    /// <remarks>
    /// フレーム・ページ・ファイルの送りや表示画像の差し替えの後は同じ基準点で計算し直すが、寸法の違う画像では
    /// 基準点が範囲外になり得る。前の画像の断面を残すと送った先の画像の値と誤読されるので、データを空にして
    /// 範囲外であることを示す。基準点・方向・縦軸の設定は保ち、範囲内の画像へ戻れば同じ点・同じ方向で出し直せる。
    /// </remarks>
    /// <param name="pointX">基準点X(元画像の座標)。</param>
    /// <param name="pointY">基準点Y(元画像の座標)。</param>
    /// <param name="imageWidth">表示中の画像の幅。</param>
    /// <param name="imageHeight">表示中の画像の高さ。</param>
    /// <param name="maxCode">表示中の画像のビット深度の最大raw code。</param>
    public void ShowOutsideImage(int pointX, int pointY, int imageWidth, int imageHeight, int maxCode)
    {
        ApplyData(Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(),
            null, pointX, pointY, maxCode, (imageWidth, imageHeight), projectionInSplitView: false);
    }

    private void ApplyData(
        double[] rowProfile,
        double[] columnProfile,
        double[] horizontalProjection,
        double[] verticalProjection,
        RegionOfInterest? roi,
        int pointX,
        int pointY,
        int maxCode,
        (int Width, int Height)? outsideImage,
        bool projectionInSplitView)
    {
        int previousCount = CurrentData.Length;
        int previousOffset = CoordinateOffset;
        EndPan();

        // 射影の選択を外すと方向の切替として通知され、MainWindow が基準点へマーカーを置き直す。
        // 通知より前に範囲内・外と基準点を新しい値にしておく(範囲外の点にマーカーを出さない)
        _outsideImage = outsideImage;
        _rowProfile = rowProfile;
        _columnProfile = columnProfile;
        _horizontalProjection = horizontalProjection;
        _verticalProjection = verticalProjection;
        _roi = roi;
        _projectionInSplitView = projectionInSplitView;
        _pointX = pointX;
        _pointY = pointY;
        _maxCode = Math.Max(1, maxCode);

        bool hasProjection = horizontalProjection.Length > 0 && verticalProjection.Length > 0;
        ProjectionCheck.IsEnabled = hasProjection;
        if (!hasProjection)
        {
            ProjectionCheck.IsChecked = false;
        }

        if (previousCount != CurrentData.Length || previousOffset != CoordinateOffset) _horizontalRange = null;
        Redraw();
    }

    private bool UseProjection => ProjectionCheck?.IsChecked == true
        && _horizontalProjection.Length > 0;

    /// <summary>表示中の射影の座標がチャネル分割表示(タイル)の座標か(断面は分割表示でも元画像の座標)。</summary>
    private bool UseSplitViewCoordinates => UseProjection && _projectionInSplitView;

    private double[] CurrentData => (IsHorizontal, UseProjection) switch
    {
        (true, true) => _horizontalProjection,
        (true, false) => _rowProfile,
        (false, true) => _verticalProjection,
        _ => _columnProfile,
    };

    /// <summary>
    /// 現在データの統計。中央値の算出でソート用配列を確保するため、
    /// データが変わったときだけ計算してキャッシュする
    /// (リサイズのたびに N=46341 で 371KB の LOH 割り当てが発生していた)。
    /// </summary>
    private ProfileStatistics CurrentStatistics
    {
        get
        {
            double[] data = CurrentData;
            if (!ReferenceEquals(data, _statsSource))
            {
                _statsSource = data;
                _stats = ImageAnalysis.ComputeProfileStatistics(data);
            }

            return _stats;
        }
    }

    private void OnDirectionChanged(object sender, RoutedEventArgs e)
    {
        EndPan();
        _horizontalRange = null;
        Redraw();
        DirectionChanged?.Invoke(IsHorizontal);
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Redraw();
    }

    private void Redraw()
    {
        if (!_ready || PlotCanvas is null)
        {
            return;
        }

        PlotCanvas.Children.Clear();
        bool horizontal = IsHorizontal;
        bool projection = UseProjection;
        double[] data = CurrentData;
        double width = PlotCanvas.ActualWidth;
        double height = PlotCanvas.ActualHeight;

        ProfileStatistics stats = CurrentStatistics;
        _axisRange = YScaleCombo.SelectedIndex switch
        {
            1 => ProfileAxisRange.Auto(stats, _maxCode),
            2 => _manualRange ?? ProfileAxisRange.Full(_maxCode),
            _ => ProfileAxisRange.Full(_maxCode),
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
        string extremeFormat = projection ? "F2" : "F0";
        StatsText.Text = _outsideImage is { } outside
            ? $"基準点 (x={_pointX}, y={_pointY}) は表示中の画像 ({outside.Width}×{outside.Height}) の範囲外です。" +
              "範囲内の画像へ送るか、画像上をクリックし直してください。"
            : stats.Count == 0
            ? "—"
            : $"N={stats.Count}   平均 {stats.Mean:F2}   最小 {stats.Min.ToString(extremeFormat)}   " +
              $"最大 {stats.Max.ToString(extremeFormat)}   中央値 {stats.Median:F1}   σ {stats.Sigma:F2}   " +
              $"P-P {(stats.Max - stats.Min).ToString(extremeFormat)}";

        string origin = _outsideImage is not null
            ? $"範囲外 (x={_pointX}, y={_pointY})"
            : projection && _roi is { } r
            ? $"ROI({r.X},{r.Y} {r.Width}×{r.Height}) 平均射影"
            : horizontal ? $"行 y={_pointY} (x={_pointX}基準)" : $"列 x={_pointX} (y={_pointY}基準)";
        InfoText.Text = $"{(horizontal ? "水平" : "垂直")}  {origin}";
        Title = _outsideImage is not null
            ? $"ラインプロファイル — 範囲外 (x={_pointX}, y={_pointY})"
            : projection
            ? $"ラインプロファイル — {(horizontal ? "水平" : "垂直")}射影 (ROI平均)"
            : horizontal
                ? $"ラインプロファイル — 行 y={_pointY}"
                : $"ラインプロファイル — 列 x={_pointX}";

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

        // 拡大中は表示区間と隣接点だけを読む。間引きでも鋭いピークは残す。
        IReadOnlyList<Point> points = ProfilePlotNavigation.SampleVisible(data, HorizontalRange, width);

        PlotCanvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = _axisRange.BuildGeometry(points, height),
            Stroke = new SolidColorBrush(Color.FromRgb(0x5B, 0x9D, 0xD9)),
            StrokeThickness = 1,
        });

        if (data.Length == 1 && _axisRange.Contains(data[0]))
        {
            var dot = new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(Color.FromRgb(0x5B, 0x9D, 0xD9)) };
            Canvas.SetLeft(dot, ProfilePlotNavigation.ToCanvasX(0, HorizontalRange, width) - 2);
            Canvas.SetTop(dot, _axisRange.ToCanvasY(data[0], height) - 2);
            PlotCanvas.Children.Add(dot);
        }

        // クリック位置マーカー(単一ライン表示時のみ)
        if (!projection)
        {
            int index = horizontal ? _pointX : _pointY;
            if (index >= 0 && index < data.Length && HorizontalRange.Contains(index))
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

    internal string? BuildTable(char separator)
    {
        double[] data = CurrentData;
        if (data.Length == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append(IsHorizontal ? "x" : "y").Append(UseSplitViewCoordinates ? "_display" : "")
            .Append(separator).Append("value").AppendLine();
        for (int i = 0; i < data.Length; i++)
        {
            sb.Append((long)i + CoordinateOffset).Append(separator)
                .Append(data[i].ToString("G6", CultureInfo.InvariantCulture)).AppendLine();
        }

        return sb.ToString();
    }

    private void OnCopyDataClick(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.TrySetText(BuildTable('\t'));
    }

    private void OnCopyStatsClick(object sender, RoutedEventArgs e)
    {
        // データがない(基準点が範囲外など)ときは、0 を並べた統計を実測値のようにコピーしない
        // (データのコピー・CSV保存と同じ)
        if (CurrentData.Length == 0)
        {
            return;
        }

        ProfileStatistics stats = CurrentStatistics;
        var sb = new StringBuilder();
        sb.AppendLine("metric\tvalue");
        sb.Append("N\t").Append(stats.Count).AppendLine();
        sb.Append("mean\t").Append(stats.Mean.ToString("G6", CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("min\t").Append(stats.Min.ToString("G6", CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("max\t").Append(stats.Max.ToString("G6", CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("median\t").Append(stats.Median.ToString("G6", CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("sigma\t").Append(stats.Sigma.ToString("G6", CultureInfo.InvariantCulture)).AppendLine();
        ClipboardHelper.TrySetText(sb.ToString());
    }

    private void OnSaveCsvClick(object sender, RoutedEventArgs e)
    {
        string? table = BuildTable(',');
        if (table is null)
        {
            return;
        }

        string name = UseProjection
            ? "projection.csv"
            : IsHorizontal ? $"profile_y{_pointY}.csv" : $"profile_x{_pointX}.csv";
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = name };
        if (dialog.ShowDialog(this) == true)
        {
            ClipboardHelper.WriteTextOrWarn(this, dialog.FileName, table, "プロファイル保存");
        }
    }
}
