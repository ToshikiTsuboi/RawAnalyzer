using System.Text;
using System.Windows;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Views;

/// <summary>
/// 指定画素を通る水平/垂直ラインのraw値折れ線と、その統計を表示するウィンドウ。
/// ROI選択時はROI内を直交方向に平均した射影プロファイルも表示できる。
/// </summary>
/// <remarks>
/// グラフ(統計・縦軸・拡大・移動・コピー/CSV)は射影の窓と共有する <see cref="ProfilePlotView"/>。
/// </remarks>
public partial class LineProfileWindow : Window
{
    private double[] _rowProfile = Array.Empty<double>();
    private double[] _columnProfile = Array.Empty<double>();
    private double[] _horizontalProjection = Array.Empty<double>();
    private double[] _verticalProjection = Array.Empty<double>();
    private RegionOfInterest? _roi;

    // チャネル分割表示で描いたROIの射影なら、その画素の元画像の格子(射影の座標は元画像ではなく分割表示(タイル)の
    // 座標。表には元画像の座標も並べる)。それ以外はnull
    private ChannelRegion? _projectionSourceRegion;

    private int _pointX;
    private int _pointY;

    // 基準点が表示中の画像の範囲外のとき、その画像の寸法(断面を出しているときは null)
    private (int Width, int Height)? _outsideImage;
    private int _maxCode = 65535;
    private bool _ready;

    /// <summary>ウィンドウを生成する。</summary>
    public LineProfileWindow()
    {
        InitializeComponent();
        Plot.TableBuilder = BuildTableCore;
        Plot.CsvFileName = () => UseProjection
            ? "projection.csv"
            : IsHorizontal ? $"profile_y{_pointY}.csv" : $"profile_x{_pointX}.csv";
        _ready = true;
        Update();
        Closed += (_, _) => Plot.EndInteraction();
    }

    internal ProfileAxisRange AxisRange => Plot.AxisRange;

    internal ProfileAxisRange HorizontalRange => Plot.HorizontalRange;

    internal void ZoomAt(Point position, int wheelDelta, bool zoomHorizontal = true, bool zoomVertical = true) =>
        Plot.ZoomAt(position, wheelDelta, zoomHorizontal, zoomVertical);

    internal void PanFrom(ProfileAxisRange horizontal, ProfileAxisRange vertical, Vector movement) =>
        Plot.PanFrom(horizontal, vertical, movement);

    internal void ResetView() => Plot.ResetView();

    internal void PrepareYScaleEditor() => Plot.PrepareYScaleEditor();

    internal string? BuildTable(char separator) => Plot.BuildTable(separator);

    internal string? BuildStatisticsTable() => Plot.BuildStatisticsTable();

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
    /// <param name="projectionSourceRegion">
    /// チャネル分割表示で描いたROIの射影なら、その画素の元画像の格子(1チャネルの2画素おきの格子)。射影の座標は
    /// 元画像ではなく、ROIを描いた分割表示(タイル)の座標になる(表には元画像の座標も並べる)。通常の表示ではnull。
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
        ChannelRegion? projectionSourceRegion = null)
    {
        ApplyData(rowProfile, columnProfile, horizontalProjection, verticalProjection, roi,
            pointX, pointY, maxCode, outsideImage: null, projectionSourceRegion);
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
            null, pointX, pointY, maxCode, (imageWidth, imageHeight), projectionSourceRegion: null);
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
        ChannelRegion? projectionSourceRegion)
    {
        // 射影の選択を外すと方向の切替として通知され、MainWindow が基準点へマーカーを置き直す。
        // 通知より前に範囲内・外と基準点を新しい値にしておく(範囲外の点にマーカーを出さない)
        _outsideImage = outsideImage;
        _rowProfile = rowProfile;
        _columnProfile = columnProfile;
        _horizontalProjection = horizontalProjection;
        _verticalProjection = verticalProjection;
        _roi = roi;
        _projectionSourceRegion = projectionSourceRegion;
        _pointX = pointX;
        _pointY = pointY;
        _maxCode = Math.Max(1, maxCode);

        bool hasProjection = horizontalProjection.Length > 0 && verticalProjection.Length > 0;
        ProjectionCheck.IsEnabled = hasProjection;
        if (!hasProjection)
        {
            ProjectionCheck.IsChecked = false;
        }

        Update();
    }

    private bool UseProjection => ProjectionCheck?.IsChecked == true
        && _horizontalProjection.Length > 0;

    /// <summary>表示中の射影の座標がチャネル分割表示(タイル)の座標か(断面は分割表示でも元画像の座標)。</summary>
    private bool UseSplitViewCoordinates => UseProjection && _projectionSourceRegion is not null;

    private double[] CurrentData => (IsHorizontal, UseProjection) switch
    {
        (true, true) => _horizontalProjection,
        (true, false) => _rowProfile,
        (false, true) => _verticalProjection,
        _ => _columnProfile,
    };

    private int CoordinateOffset => UseProjection && _roi is { } roi ? IsHorizontal ? roi.X : roi.Y : 0;

    private void OnDirectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Plot.ResetHorizontalRange();
        Update();
        DirectionChanged?.Invoke(IsHorizontal);
    }

    /// <summary>選んでいる方向・射影のデータをグラフへ渡し、見出しを出し直す。</summary>
    private void Update()
    {
        if (!_ready)
        {
            return;
        }

        bool horizontal = IsHorizontal;
        bool projection = UseProjection;

        // チャネル分割表示の射影は ROI を描いた分割表示(タイル)の座標で出す。元画像の列・行と読み違えないよう示す
        bool splitView = UseSplitViewCoordinates;
        Plot.SetAxisLabels(
            $"{(horizontal ? "水平" : "垂直")}{(projection ? " ROI平均射影" : "プロファイル")}" +
            $" — {(horizontal ? "x" : "y")}座標 [px・{(splitView ? "チャネル分割表示の座標" : "画像座標")}]",
            splitView
                ? "チャネル分割表示(R/Gr/Gb/B の2×2並置)上で ROI を描いた座標。元画像の列・行ではありません。" +
                  "ホイールで横軸だけ拡大・縮小できます。"
                : "元画像上の画素座標。ホイールで横軸だけ拡大・縮小できます。");
        if (_outsideImage is { } outside)
        {
            Plot.ShowMessage(
                $"基準点 (x={_pointX}, y={_pointY}) は表示中の画像 ({outside.Width}×{outside.Height}) の範囲外です。" +
                "範囲内の画像へ送るか、画像上をクリックし直してください。",
                _maxCode);
        }
        else
        {
            // 断面は整数の raw code、射影は直交方向の平均(実数)。クリック位置マーカーは単一ライン表示時のみ
            Plot.SetData(new ProfilePlotData(
                CurrentData, _maxCode, CoordinateOffset, IntegerValues: !projection,
                MarkerIndex: projection ? null : horizontal ? _pointX : _pointY));
        }

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
    }

    private string? BuildTableCore(char separator)
    {
        double[] data = Plot.Values;
        if (data.Length == 0)
        {
            return null;
        }

        // チャネル分割表示の射影は、表示(タイル)の座標に並べて元画像の座標(1チャネルの格子なので2画素おき)も出す
        string axis = IsHorizontal ? "x" : "y";
        ChannelRegion? source = UseSplitViewCoordinates ? _projectionSourceRegion : null;
        long sourceOrigin = source is { } region ? IsHorizontal ? region.X : region.Y : 0;
        var sb = new StringBuilder();
        sb.Append(axis).Append(source is null ? "" : "_display").Append(separator);
        if (source is not null)
        {
            sb.Append(axis).Append("_source").Append(separator);
        }

        sb.Append("value").AppendLine();
        for (int i = 0; i < data.Length; i++)
        {
            sb.Append((long)i + Plot.CoordinateOffset).Append(separator);
            if (source is not null)
            {
                sb.Append(sourceOrigin + (2L * i)).Append(separator);
            }

            sb.Append(ProfilePlotView.FormatValue(data[i])).AppendLine();
        }

        return sb.ToString();
    }
}
