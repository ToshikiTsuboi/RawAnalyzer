using System.Text;
using System.Windows;
using System.Windows.Media;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Views;

/// <summary>
/// 水平射影(各列を縦に平均して x 座標に並べる)または垂直射影(各行を横に平均して y 座標に並べる)の窓。
/// 平均の線に加えて、EMVA 1288 にならい各位置の最大・最小の線を出す(チェックで消せる)。
/// </summary>
/// <remarks>
/// グラフ(統計・縦軸の設定・拡大・移動・コピー/CSV)はラインプロファイル窓と共有する <see cref="ProfilePlotView"/>。
/// 対象の決め方・計算・計算し直しの契機は MainWindow が受け持ち、この窓は結果・計算中・断る理由を出すだけ。
/// </remarks>
public partial class ProjectionWindow : Window
{
    /// <summary>最大の線の色。</summary>
    internal static readonly Color MaxStroke = Color.FromRgb(0xE0, 0x88, 0x4E);

    /// <summary>最小の線の色。</summary>
    internal static readonly Color MinStroke = Color.FromRgb(0x9F, 0x86, 0xE0);

    private ProjectionProfile _profile = ProjectionProfile.Empty();
    private ProjectionAxis _axis;
    private int _busyPercent = -1;

    // 出している結果・知らせの対象の説明(計算をやめて前の結果へ戻すときに見出しも戻す)
    private string _shownHeader = "";

    // チャネルの選択欄を共有の選択に合わせている間(利用者の選択として知らせない)。窓を作る間も知らせない
    private bool _settingChannel = true;

    // 選択欄の並び(未選択・R・Gr・Gb・B)
    private static readonly BayerChannel?[] ChannelItems =
        { null, BayerChannel.R, BayerChannel.Gr, BayerChannel.Gb, BayerChannel.B };

    /// <summary>向きを指定して窓を生成する。</summary>
    /// <param name="direction">向き。</param>
    internal ProjectionWindow(ProjectionDirection direction)
    {
        Direction = direction;
        InitializeComponent();
        string name = ProjectionText.Name(direction);
        Title = name;
        HelpMark.ToolTip = ProjectionText.WindowHelp(direction);
        ExtremesCheck.ToolTip =
            $"各{Position}の画素の最大値・最小値の線を出します(EMVA 1288 と同じ)。" +
            "消すと、縦軸の自動は平均の線だけに合わせます(1 code 未満のムラが見やすくなります)。";
        Plot.TableBuilder = BuildTableCore;
        Plot.CsvFileName = () => $"{(direction == ProjectionDirection.Horizontal ? "horizontal" : "vertical")}_projection.csv";
        Plot.CsvSaveCaption = $"{name}の保存";
        Plot.SetAxisLabels(ProjectionText.AxisTitle(direction, false), ProjectionText.AxisToolTip(false));
        ChannelCombo.ToolTip = ProjectionText.ChannelChoiceToolTip;
        RoiPriorityText.ToolTip = ProjectionText.RoiTakesPriorityToolTip;
        _settingChannel = false;
        Closed += (_, _) => Plot.EndInteraction();
    }

    /// <summary>向き。</summary>
    internal ProjectionDirection Direction { get; }

    /// <summary>利用者がチャネルの選択欄で選んだ(未選択に戻したら null)。プログラムからの設定では起きない。</summary>
    internal event Action<BayerChannel?>? ChannelSelectionChanged;

    /// <summary>チャネルの選択欄の見せ方と選んでいるチャネルを設定する(水平・垂直の窓で共有する選択を映す)。</summary>
    /// <param name="choice">見せ方。</param>
    /// <param name="channel">選んでいるチャネル(未選択なら null)。</param>
    internal void SetChannelChoice(ProjectionChannelChoice choice, BayerChannel? channel)
    {
        _settingChannel = true;
        try
        {
            ChannelPanel.Visibility = choice == ProjectionChannelChoice.None ? Visibility.Collapsed : Visibility.Visible;
            ChannelCombo.IsEnabled = choice == ProjectionChannelChoice.Choosable;
            RoiPriorityText.Visibility = choice == ProjectionChannelChoice.RoiTakesPriority
                ? Visibility.Visible
                : Visibility.Collapsed;
            ChannelCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ChannelItems, channel));
        }
        finally
        {
            _settingChannel = false;
        }
    }

    private void OnChannelComboChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_settingChannel || ChannelCombo is null || ChannelCombo.SelectedIndex < 0) return;
        ChannelSelectionChanged?.Invoke(ChannelItems[ChannelCombo.SelectedIndex]);
    }

    /// <summary>計算中か(前の結果を薄く残し、コピー・CSV はしない)。</summary>
    internal bool IsBusy => Plot.IsBusy;

    /// <summary>計算中の表示を遅らせる時間(すぐ終わる計算でちらつかせない。テストでは0)。</summary>
    internal TimeSpan BusyDelay
    {
        get => Plot.BusyDelay;
        set => Plot.BusyDelay = value;
    }

    internal ProfileAxisRange AxisRange => Plot.AxisRange;

    internal ProfileAxisRange HorizontalRange => Plot.HorizontalRange;

    internal void ZoomAt(Point position, int wheelDelta, bool zoomHorizontal = true, bool zoomVertical = true) =>
        Plot.ZoomAt(position, wheelDelta, zoomHorizontal, zoomVertical);

    internal void ResetView() => Plot.ResetView();

    internal string? BuildTable(char separator) => Plot.BuildTable(separator);

    internal string? BuildStatisticsTable() => Plot.BuildStatisticsTable();

    private string Position => Direction == ProjectionDirection.Horizontal ? "列" : "行";

    /// <summary>
    /// 計算中を示す(前の結果は薄く残す。横軸の拡大を保つため)。上部の対象は計算している対象に替える。
    /// </summary>
    /// <param name="header">計算している対象の説明。</param>
    internal void ShowBusy(string header)
    {
        SetHeader(header, samples: null);
        _busyPercent = -1;
        Plot.ShowBusy("計算中…(対象の全画素を読んでいます)");
    }

    /// <summary>計算の進み具合を示す(1% ごとに表示を変える)。</summary>
    /// <param name="fraction">進み具合(0〜1)。</param>
    internal void ReportProgress(double fraction)
    {
        int percent = (int)Math.Clamp(fraction * 100, 0, 100);
        if (!IsBusy || percent == _busyPercent) return;
        _busyPercent = percent;
        Plot.UpdateBusyMessage($"計算中… {percent}%(対象の全画素を読んでいます)");
    }

    /// <summary>
    /// 計算中の表示をやめ、前の結果(と見出し)のまま出し直す(計算をやめ、出していた結果がいまの表示のものに戻ったとき)。
    /// </summary>
    internal void CancelBusy()
    {
        if (!IsBusy) return;
        SetHeader(_shownHeader, _profile.Length > 0 ? _profile.SamplesPerPosition : null);
        Plot.CancelBusy();
    }

    /// <summary>結果を出す。</summary>
    /// <param name="header">対象の説明(「対象: ROI (x, y, w×h)」など)。</param>
    /// <param name="profile">射影。</param>
    /// <param name="axis">横軸の座標。</param>
    /// <param name="maxCode">表示中の画像のビット深度の最大 raw code。</param>
    internal void ShowResult(string header, ProjectionProfile profile, ProjectionAxis axis, int maxCode)
    {
        _profile = profile;
        _axis = axis;
        _shownHeader = header;
        SetHeader(header, profile.SamplesPerPosition);
        Plot.SetAxisLabels(
            ProjectionText.AxisTitle(Direction, axis.IsSplitDisplay), ProjectionText.AxisToolTip(axis.IsSplitDisplay));

        // 射影は平均(実数)で 1 code 未満のムラを見る値なので、統計の最小・最大・P-P は小数で出す
        Plot.SetData(new ProfilePlotData(
            profile.Mean, maxCode, axis.Origin, IntegerValues: false,
            ExtraSeries: new[] { new ProfileSeries(profile.Max, MaxStroke), new ProfileSeries(profile.Min, MinStroke) }));
    }

    /// <summary>
    /// 射影を出せない理由・知らせを出す(前の結果は消す。別の対象の値と誤読させない)。
    /// </summary>
    /// <param name="header">対象の説明。</param>
    /// <param name="message">理由・知らせ。</param>
    /// <param name="maxCode">表示中の画像のビット深度の最大 raw code。</param>
    internal void ShowMessage(string header, string message, int maxCode)
    {
        _profile = ProjectionProfile.Empty();
        _axis = default;
        _shownHeader = header;
        SetHeader(header, samples: null);
        Plot.SetAxisLabels(ProjectionText.AxisTitle(Direction, false), ProjectionText.AxisToolTip(false));
        Plot.ShowMessage(message, maxCode);
    }

    private void SetHeader(string header, long? samples)
    {
        TargetText.Text = samples is { } count
            ? $"{header} · 各{Position} {count} 画素の平均"
            : header;
        TargetText.ToolTip = TargetText.Text;
        Title = $"{ProjectionText.Name(Direction)} — {header}";
    }

    private void OnExtremesChanged(object sender, RoutedEventArgs e)
    {
        if (Plot is null) return;
        bool visible = ExtremesCheck.IsChecked == true;
        Plot.ExtraSeriesVisible = visible;
        MaxLegend.Opacity = visible ? 1 : 0.3;
        MinLegend.Opacity = visible ? 1 : 0.3;
    }

    private string? BuildTableCore(char separator)
    {
        ProjectionProfile profile = _profile;
        if (profile.Length == 0)
        {
            return null;
        }

        // チャネル分割表示の射影は、表示(タイル)の座標に並べて元画像の座標(1チャネルの格子なので2画素おき)も出す
        string axis = ProjectionText.Axis(Direction);
        bool split = _axis.IsSplitDisplay;
        var sb = new StringBuilder();
        sb.Append(axis).Append(split ? "_display" : "").Append(separator);
        if (split)
        {
            sb.Append(axis).Append("_source").Append(separator);
        }

        sb.Append("mean").Append(separator).Append("min").Append(separator).Append("max").AppendLine();
        for (int i = 0; i < profile.Length; i++)
        {
            sb.Append(_axis.Origin + i).Append(separator);
            if (split)
            {
                sb.Append(_axis.SourceCoordinate(i)).Append(separator);
            }

            sb.Append(ProfilePlotView.FormatValue(profile.Mean[i])).Append(separator)
                .Append(ProfilePlotView.FormatValue(profile.Min[i])).Append(separator)
                .Append(ProfilePlotView.FormatValue(profile.Max[i])).AppendLine();
        }

        return sb.ToString();
    }
}
