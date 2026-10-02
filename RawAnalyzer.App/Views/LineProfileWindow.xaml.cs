using System.Text;
using System.Windows;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.App.Views;

/// <summary>
/// 指定画素を通る水平/垂直ラインのraw値折れ線と、その統計を表示するウィンドウ。
/// </summary>
/// <remarks>
/// グラフ(統計・縦軸・拡大・移動・コピー/CSV)は射影の窓と共有する <see cref="ProfilePlotView"/>。
/// ROI の平均射影は、ツールバーの「水平射影」「垂直射影」の窓(<see cref="ProjectionWindow"/>)で見る。
/// </remarks>
public partial class LineProfileWindow : Window
{
    private double[] _rowProfile = Array.Empty<double>();
    private double[] _columnProfile = Array.Empty<double>();

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
        Plot.CsvFileName = () => IsHorizontal ? $"profile_y{_pointY}.csv" : $"profile_x{_pointX}.csv";
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

    /// <summary>水平/垂直の切替時に発火する(true=水平)。</summary>
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
    /// <param name="pointX">基準点X(元画像の座標。クリックした点、送り・差し替えの後は同じ点)。</param>
    /// <param name="pointY">基準点Y(元画像の座標)。</param>
    /// <param name="maxCode">ビット深度の最大raw code。</param>
    public void SetProfiles(double[] rowProfile, double[] columnProfile, int pointX, int pointY, int maxCode)
    {
        ApplyData(rowProfile, columnProfile, pointX, pointY, maxCode, outsideImage: null);
    }

    /// <summary>
    /// 基準点が表示中の画像の範囲外で、断面を出せないことを示す(前の断面・統計は消す)。
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
        ApplyData(Array.Empty<double>(), Array.Empty<double>(), pointX, pointY, maxCode, (imageWidth, imageHeight));
    }

    private void ApplyData(
        double[] rowProfile,
        double[] columnProfile,
        int pointX,
        int pointY,
        int maxCode,
        (int Width, int Height)? outsideImage)
    {
        _outsideImage = outsideImage;
        _rowProfile = rowProfile;
        _columnProfile = columnProfile;
        _pointX = pointX;
        _pointY = pointY;
        _maxCode = Math.Max(1, maxCode);
        Update();
    }

    private double[] CurrentData => IsHorizontal ? _rowProfile : _columnProfile;

    private void OnDirectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Plot.ResetHorizontalRange();
        Update();
        DirectionChanged?.Invoke(IsHorizontal);
    }

    /// <summary>選んでいる方向のデータをグラフへ渡し、見出しを出し直す。</summary>
    private void Update()
    {
        if (!_ready)
        {
            return;
        }

        bool horizontal = IsHorizontal;
        Plot.SetAxisLabels(
            $"{(horizontal ? "水平" : "垂直")}プロファイル — {(horizontal ? "x" : "y")}座標 [px・画像座標]",
            "元画像上の画素座標。ホイールで横軸だけ拡大・縮小できます。");
        if (_outsideImage is { } outside)
        {
            Plot.ShowMessage(
                $"基準点 (x={_pointX}, y={_pointY}) は表示中の画像 ({outside.Width}×{outside.Height}) の範囲外です。" +
                "範囲内の画像へ送るか、画像上をクリックし直してください。",
                _maxCode);
        }
        else
        {
            // 断面は整数の raw code。基準点の位置に縦線を出す
            Plot.SetData(new ProfilePlotData(
                CurrentData, _maxCode, MarkerIndex: horizontal ? _pointX : _pointY));
        }

        string origin = _outsideImage is not null
            ? $"範囲外 (x={_pointX}, y={_pointY})"
            : horizontal ? $"行 y={_pointY} (x={_pointX}基準)" : $"列 x={_pointX} (y={_pointY}基準)";
        InfoText.Text = $"{(horizontal ? "水平" : "垂直")}  {origin}";
        Title = _outsideImage is not null
            ? $"ラインプロファイル — 範囲外 (x={_pointX}, y={_pointY})"
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

        var sb = new StringBuilder();
        sb.Append(IsHorizontal ? "x" : "y").Append(separator).Append("value").AppendLine();
        for (int i = 0; i < data.Length; i++)
        {
            sb.Append(i).Append(separator).Append(ProfilePlotView.FormatValue(data[i])).AppendLine();
        }

        return sb.ToString();
    }
}
