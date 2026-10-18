using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// グラフ(ラインプロファイル・射影で共有)の軸の書式: 縦軸の目盛り、目盛りの間隔の指定、手動の横軸、目盛線。
/// </summary>
[Collection("WPF UI")]
public class ProfileAxisFormatTests
{
    [Fact]
    public Task VerticalAxis_ShowsTickLabelsAtMultiplesOfTheStepAndGridLines() => WithPlot(plot =>
    {
        // 以前の縦軸は最小・中央・最大の 3 つの値だけで、目盛線もなかった
        ProfileAxisTicks y = plot.VerticalTicks;
        Assert.Contains(y.MajorStep, new double[] { 500, 1000 });
        double[] labels = Labels(plot, "YAxisCanvas");
        Assert.NotEmpty(labels);
        Assert.All(labels, value => Assert.Equal(0, value % y.MajorStep));

        // 上下の端は範囲の値(全範囲なら 0 と 4095)を出し、それと重なる目盛りのラベルは出さない
        Assert.Equal("4095", Find<TextBlock>(plot, "MaxLabel").Text);
        Assert.Equal("0", Find<TextBlock>(plot, "MinLabel").Text);
        Assert.DoesNotContain(0.0, labels);

        // 主目盛りの目盛線は既定で描き、補助目盛りの目盛線は描かない
        Assert.Equal(y.Major.Count + plot.HorizontalTicks.Major.Count, GridLines(plot, ProfilePlotView.MajorGridTag));
        Assert.Equal(0, GridLines(plot, ProfilePlotView.MinorGridTag));
        Assert.NotEmpty(y.Minor);
        ProfileWindowParts.CaptureElementIfRequested(plot, "axis-format-default", 700, 420);
    });

    [Fact]
    public Task ManualSteps_SetTheTickIntervalsOfBothAxes() => WithPlot(plot =>
    {
        // 指定へ切り替えた時点では、いま描いている間隔を引き継ぐ
        double autoStep = plot.VerticalTicks.MajorStep;
        SetAuto(plot, "YMajorAutoCheck", false);
        Assert.Equal(autoStep.ToString("G10", CultureInfo.CurrentCulture), Find<TextBox>(plot, "YMajorStepBox").Text);
        Assert.Equal(autoStep, plot.VerticalTicks.MajorStep);

        Find<TextBox>(plot, "YMajorStepBox").Text = "250";
        Assert.Equal(autoStep, plot.VerticalTicks.MajorStep); // 入力だけでは未反映
        Apply(plot);
        Assert.Equal(250, plot.VerticalTicks.MajorStep);
        Assert.All(Labels(plot, "YAxisCanvas"), value => Assert.Equal(0, value % 250));

        SetAuto(plot, "XMajorAutoCheck", false);
        Find<TextBox>(plot, "XMajorStepBox").Text = "50";
        Apply(plot);
        ProfileAxisTicks x = plot.HorizontalTicks;
        Assert.Equal(50, x.MajorStep);
        Assert.All(x.Major, value => Assert.Equal(0, value % 50));
        // ラベルは重ならないよう間引いても、間隔の整数倍の座標に付ける
        double[] xLabels = Labels(plot, "XAxisCanvas");
        Assert.NotEmpty(xLabels);
        Assert.True(xLabels.Length <= x.Major.Count);
        Assert.All(xLabels, value => Assert.Equal(0, value % 50));
        ProfileWindowParts.CaptureElementIfRequested(plot, "axis-format-manual-steps", 700, 420);

        // 自動へ戻すと自動の間隔で描く(指定の値は覚えておく)
        SetAuto(plot, "XMajorAutoCheck", true);
        Assert.Equal(500, plot.HorizontalTicks.MajorStep);
        Assert.False(Find<TextBox>(plot, "XMajorStepBox").IsEnabled);
        SetAuto(plot, "XMajorAutoCheck", false);
        Assert.Equal(50, plot.HorizontalTicks.MajorStep);
    });

    [Fact]
    public Task ManualStep_TooFineForTheZoom_UsesAutoUntilZoomedIn() => WithPlot(plot =>
    {
        SetAuto(plot, "XMajorAutoCheck", false);
        Find<TextBox>(plot, "XMajorStepBox").Text = "1";
        Apply(plot);

        // 2000 画素を 600px 余りに 1 画素おきの目盛りは詰まりすぎるので、自動の間隔で描いて知らせる
        Assert.True(plot.HorizontalTicks.ManualMajorTooFine);
        Assert.Equal(500, plot.HorizontalTicks.MajorStep);
        Assert.Contains("細かすぎる", (string)Find<Canvas>(plot, "XAxisCanvas").ToolTip);
        var note = Find<TextBlock>(plot, "AxisFormatNote");
        Assert.Equal(Visibility.Visible, note.Visibility);
        Assert.StartsWith("横軸:", note.Text);

        var canvas = Find<Canvas>(plot, "PlotCanvas");
        for (int i = 0; i < 20; i++) plot.ZoomAt(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 120, true, false);
        Assert.False(plot.HorizontalTicks.ManualMajorTooFine);
        Assert.Equal(1, plot.HorizontalTicks.MajorStep);
        Assert.Equal(Visibility.Collapsed, note.Visibility);
    });

    [Fact]
    public Task InvalidSteps_MarkTheFieldAndCannotBeApplied() => WithPlot(plot =>
    {
        var apply = Find<Button>(plot, "ApplyAxisFormatButton");
        var major = Find<TextBox>(plot, "XMajorStepBox");
        var minor = Find<TextBox>(plot, "XMinorStepBox");
        Assert.True(apply.IsEnabled);
        FieldFeedback.AssertValid(major);

        SetAuto(plot, "XMajorAutoCheck", false);
        major.Text = "1.5";
        Assert.Contains("1 以上の整数", FieldFeedback.AssertInvalid(major));
        Assert.False(apply.IsEnabled);
        Apply(plot);
        Assert.Equal(500, plot.HorizontalTicks.MajorStep);

        major.Text = "100";
        FieldFeedback.AssertValid(major);
        Assert.True(apply.IsEnabled);

        // 補助目盛りは、主目盛りも指定なら(まだ適用していない値とも)それより小さい値に限る
        SetAuto(plot, "XMinorAutoCheck", false);
        minor.Text = "200";
        Assert.Contains("目盛間隔より小さい値", FieldFeedback.AssertInvalid(minor));
        Assert.False(apply.IsEnabled);
        minor.Text = "20";
        FieldFeedback.AssertValid(minor);
        Apply(plot);
        Assert.Equal(100, plot.HorizontalTicks.MajorStep);
        Assert.Equal(20, plot.HorizontalTicks.MinorStep);
        Assert.All(plot.HorizontalTicks.Minor, value => Assert.Equal(0, value % 20));

        // 0 は補助目盛りなし
        minor.Text = "0";
        Apply(plot);
        Assert.Empty(plot.HorizontalTicks.Minor);

        // 自動へ戻した欄は使わないので知らせない
        major.Text = "abc";
        FieldFeedback.AssertInvalid(major);
        SetAuto(plot, "XMajorAutoCheck", true);
        FieldFeedback.AssertValid(major);
    });

    [Fact]
    public Task ManualHorizontalRange_KeepsCoordinatesAcrossDataChanges() => WithPlot(plot =>
    {
        var mode = Find<ComboBox>(plot, "XScaleCombo");
        var min = Find<TextBox>(plot, "XMinimumBox");
        var max = Find<TextBox>(plot, "XMaximumBox");
        Assert.False(min.IsEnabled);

        // 手動へ切り替えた時点の表示範囲で固定し、値を入れて適用する
        mode.SelectedIndex = 1;
        Assert.True(min.IsEnabled);
        Assert.Equal(new ProfileAxisRange(0, 1999), plot.HorizontalRange);
        min.Text = "100";
        max.Text = "300";
        Apply(plot);
        Assert.Equal(new ProfileAxisRange(100, 300), plot.HorizontalRange);

        // 自動の横軸(ホイールでの拡大を含む)はデータの長さが変わると全体へ戻るが、手動の範囲は座標で保つ
        double[] values = Enumerable.Range(0, 1000).Select(i => 1000.0 + i % 7).ToArray();
        plot.SetData(new ProfilePlotData(values, 4095));
        Assert.Equal(new ProfileAxisRange(100, 300), plot.HorizontalRange);
        plot.SetData(new ProfilePlotData(values, 4095, CoordinateOffset: 50));
        Assert.Equal(new ProfileAxisRange(50, 250), plot.HorizontalRange); // データの位置では 50〜250
        Assert.All(Labels(plot, "XAxisCanvas"), value => Assert.InRange(value, 100, 300));

        // 手動のまま拡大すると、その範囲が新しい手動の範囲になる
        var canvas = Find<Canvas>(plot, "PlotCanvas");
        plot.ZoomAt(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 120, true, false);
        Assert.Equal(1, mode.SelectedIndex);
        ProfileAxisRange zoomed = plot.HorizontalRange;
        Assert.True(zoomed.Maximum - zoomed.Minimum < 200);
        Assert.Equal(zoomed.Minimum + 50, double.Parse(min.Text, CultureInfo.CurrentCulture), 9);

        // 全体表示に戻すと自動(データ全体)。手動へ戻すと最後の手動の範囲を使う
        plot.ResetView();
        Assert.Equal(0, mode.SelectedIndex);
        Assert.Equal(new ProfileAxisRange(0, 999), plot.HorizontalRange);
        mode.SelectedIndex = 1;
        Assert.Equal(zoomed, plot.HorizontalRange);

        // 向きの切り替え(同じ長さでも別の座標)では手動の範囲を使わない
        plot.ResetHorizontalRange();
        Assert.Equal(0, mode.SelectedIndex);
        Assert.Equal(new ProfileAxisRange(0, 999), plot.HorizontalRange);
    });

    [Fact]
    public Task GridLines_CanBeTurnedOnAndOffForMajorAndMinorTicks() => WithPlot(plot =>
    {
        SetCheck(plot, "MajorGridCheck", false);
        Assert.Equal((false, false), plot.GridLinesVisible);
        Assert.Equal(0, GridLines(plot, ProfilePlotView.MajorGridTag));

        SetCheck(plot, "MinorGridCheck", true);
        int minor = plot.VerticalTicks.Minor.Count + plot.HorizontalTicks.Minor.Count;
        Assert.True(minor > 0);
        Assert.Equal(minor, GridLines(plot, ProfilePlotView.MinorGridTag));

        // 目盛線はグラフの中に収め、データの線(Path)は 1 本のまま
        var canvas = Find<Canvas>(plot, "PlotCanvas");
        Assert.Single(canvas.Children.OfType<System.Windows.Shapes.Path>());
        Assert.All(canvas.Children.OfType<Line>(), line =>
        {
            Assert.InRange(line.Y1, 0, canvas.ActualHeight);
            Assert.InRange(line.Y2, 0, canvas.ActualHeight);
        });
        SetCheck(plot, "MajorGridCheck", true);
        ProfileWindowParts.CaptureElementIfRequested(plot, "axis-format-minor-grid", 700, 420);
    });

    private static Task WithPlot(Action<ProfilePlotView> test) => WpfTestHost.Run(() =>
    {
        // 間隔の欄の表示("0.5" など)を比べるため文化を固定し、共有の STA スレッドへ持ち越さないよう戻す
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
        try
        {
            var plot = new ProfilePlotView { BusyDelay = TimeSpan.Zero };
            double[] row = Enumerable.Range(0, 2000).Select(i => 1000.0 + 5 * Math.Sin(i / 30.0)).ToArray();
            plot.SetData(new ProfilePlotData(row, 4095));
            plot.Measure(new Size(700, 420));
            plot.Arrange(new Rect(0, 0, 700, 420));
            plot.UpdateLayout();
            test(plot);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    });

    private static T Find<T>(ProfilePlotView plot, string name)
        where T : class =>
        plot.FindName(name) as T ?? throw new InvalidOperationException($"{name} ({typeof(T).Name}) が見つかりません");

    private static double[] Labels(ProfilePlotView plot, string axis) =>
        Find<Canvas>(plot, axis).Children.OfType<TextBlock>().Select(label => (double)label.Tag).ToArray();

    private static int GridLines(ProfilePlotView plot, string tag) =>
        Find<Canvas>(plot, "PlotCanvas").Children.OfType<Line>().Count(line => Equals(line.Tag, tag));

    private static void SetAuto(ProfilePlotView plot, string name, bool auto) => SetCheck(plot, name, auto);

    private static void SetCheck(ProfilePlotView plot, string name, bool value)
    {
        var check = Find<CheckBox>(plot, name);
        check.IsChecked = value;
        check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static void Apply(ProfilePlotView plot) =>
        Find<Button>(plot, "ApplyAxisFormatButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
}
