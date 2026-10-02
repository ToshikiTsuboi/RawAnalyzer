using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Path = System.Windows.Shapes.Path;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;
using static RawAnalyzer.Tests.ProfileWindowParts;

namespace RawAnalyzer.Tests;

/// <summary>
/// 水平射影・垂直射影の窓。平均の線と各位置の最大・最小の線、横軸の向きの見出し、ROI・チャネル分割表示の座標、
/// 計算中・断る理由の表示、コピー/CSV。グラフの操作(縦軸の設定・拡大・移動)はラインプロファイル窓と共有する。
/// </summary>
[Collection("WPF UI")]
public class ProjectionWindowTests
{
    [Fact]
    public Task Result_DrawsMeanWithMaxAndMinAndNamesTheDirection() => WpfTestHost.Run(() =>
    {
        var window = NewWindow(ProjectionDirection.Horizontal);
        try
        {
            ShowRoiResult(window);

            // 平均(主の線)に加えて、EMVA 1288 にならい各列の最大・最小の線を描く(既定で表示)
            var canvas = Find<Canvas>(window, "PlotCanvas");
            var paths = canvas.Children.OfType<Path>().ToList();
            Assert.Equal(3, paths.Count);
            Assert.True(Find<CheckBox>(window, "ExtremesCheck").IsChecked);

            // 横軸の見出しは向き(列ごとの平均を x に並べる)を示す。目盛りは ROI の元画像の座標
            Assert.Equal("水平射影 — x 座標(列ごとの平均) [px・画像座標]", Find<TextBlock>(window, "XAxisTitle").Text);
            Assert.Equal(new[] { "100", "101", "102" },
                Find<Canvas>(window, "XAxisCanvas").Children.OfType<TextBlock>().Select(t => t.Text));
            Assert.Equal("対象: ROI (100, 200, 3×1000) · 各列 1000 画素の平均", Find<TextBlock>(window, "TargetText").Text);
            Assert.Equal("水平射影 — 対象: ROI (100, 200, 3×1000)", window.Title);

            // 「？」で向きと流儀(EMVA 1288 / 平均する方向で呼ぶ流儀)を説明する
            Assert.Equal(ProjectionText.WindowHelp(ProjectionDirection.Horizontal),
                Find<Border>(window, "HelpMark").ToolTip);
            CaptureIfRequested(window, "projection-horizontal");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Vertical_NamesRowsAndUsesYCoordinates() => WpfTestHost.Run(() =>
    {
        var window = NewWindow(ProjectionDirection.Vertical);
        try
        {
            window.ShowResult("対象: ROI (100, 200, 1000×3)", Profile(40, 50, 60),
                new ProjectionAxis(200, null), 255);

            Assert.Equal("垂直射影 — y 座標(行ごとの平均) [px・画像座標]", Find<TextBlock>(window, "XAxisTitle").Text);
            Assert.Equal(new[] { "200", "201", "202" },
                Find<Canvas>(window, "XAxisCanvas").Children.OfType<TextBlock>().Select(t => t.Text));
            Assert.StartsWith("y,mean,min,max" + Environment.NewLine + "200,40,39,41", window.BuildTable(','));
            Assert.Contains("各行 1000 画素の平均", Find<TextBlock>(window, "TargetText").Text);
            Assert.Equal(ProjectionText.WindowHelp(ProjectionDirection.Vertical), Find<Border>(window, "HelpMark").ToolTip);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Table_HasMeanMinMaxAtRoiCoordinates() => WpfTestHost.Run(() =>
    {
        var window = NewWindow(ProjectionDirection.Horizontal);
        try
        {
            ShowRoiResult(window);

            string nl = Environment.NewLine;
            Assert.Equal(
                $"x,mean,min,max{nl}100,1000.4,998,1003{nl}101,1000.55,999,1002{nl}102,1000.6,997,1004{nl}",
                window.BuildTable(','));

            // 統計(平均の線の)のコピー。最小・最大は平均の線の値
            string statistics = window.BuildStatisticsTable()!;
            Assert.Contains($"N\t3{nl}", statistics);
            Assert.Contains($"min\t1000.4{nl}", statistics);
            Assert.Contains($"max\t1000.6{nl}", statistics);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TableAndStatisticsCopy_KeepSubCodeDigitsOfFiveDigitCodes() => WpfTestHost.Run(() =>
    {
        // 射影は 1 code 未満の列ムラを見る値なので、14bit・16bit の明るい画像(10000 code 以上)でも表(コピー・CSV/TSV)と
        // 統計のコピーで小数を落とさない(有効数字 6 桁では 40000.43 が 40000.4 になり、窓の統計より粗かった)
        var window = NewWindow(ProjectionDirection.Horizontal);
        try
        {
            window.ShowResult("対象: ROI (10, 20, 3×1000)",
                new ProjectionProfile
                {
                    Mean = new[] { 40000.43, 40000.57, 40001.25 },
                    Min = new double[] { 39990, 39991, 39992 },
                    Max = new double[] { 40010, 40011, 40012 },
                    SamplesPerPosition = 1000,
                },
                new ProjectionAxis(10, null), 65535);

            string nl = Environment.NewLine;
            Assert.Equal(
                $"x,mean,min,max{nl}10,40000.43,39990,40010{nl}11,40000.57,39991,40011{nl}12,40001.25,39992,40012{nl}",
                window.BuildTable(','));
            string statistics = window.BuildStatisticsTable()!;
            Assert.Contains($"mean	40000.75{nl}", statistics);
            Assert.Contains($"min	40000.43{nl}", statistics);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task SplitView_LabelsAxisAndTableAsDisplayCoordinatesWithSourceColumn() => WpfTestHost.Run(() =>
    {
        // チャネル分割表示の射影は、ROI を描いた分割表示(タイル)の座標で横軸・CSV/TSV を出す。元画像の列と読み違えないよう
        // 見出し・ツールチップ・CSV の見出しで表示座標であることを示し、元画像の座標(1チャネルの格子なので2画素おき)の
        // 列も並べる。4000×3000 の分割表示で、右上の象限の (2100, 100) から 3×3 = 元画像の x 201,203,205 / y 200,202,204
        var horizontal = NewWindow(ProjectionDirection.Horizontal);
        var vertical = NewWindow(ProjectionDirection.Vertical);
        try
        {
            horizontal.ShowResult("対象: ROI (2100, 100, 3×3・チャネル分割表示の座標) · チャネル Gr",
                Profile(10, 20, 30), new ProjectionAxis(2100, 201), 4095);
            vertical.ShowResult("対象: ROI (2100, 100, 3×3・チャネル分割表示の座標) · チャネル Gr",
                Profile(40, 50, 60), new ProjectionAxis(100, 200), 4095);

            var title = Find<TextBlock>(horizontal, "XAxisTitle");
            var axis = Find<Canvas>(horizontal, "XAxisCanvas");
            Assert.Contains("チャネル分割表示の座標", title.Text);
            Assert.DoesNotContain("画像座標]", title.Text);
            Assert.Contains("分割表示", (string)axis.ToolTip);
            Assert.DoesNotContain("元画像上の画素座標", (string)axis.ToolTip);
            Assert.Equal(new[] { "2100", "2101", "2102" }, axis.Children.OfType<TextBlock>().Select(t => t.Text));

            string nl = Environment.NewLine;
            Assert.Equal(
                $"x_display,x_source,mean,min,max{nl}2100,201,10,9,11{nl}2101,203,20,19,21{nl}2102,205,30,29,31{nl}",
                horizontal.BuildTable(','));
            Assert.Equal(
                $"y_display\ty_source\tmean\tmin\tmax{nl}100\t200\t40\t39\t41{nl}101\t202\t50\t49\t51{nl}" +
                $"102\t204\t60\t59\t61{nl}",
                vertical.BuildTable('\t'));

            // 分割表示でない結果に替えると画像座標へ戻る
            horizontal.ShowResult("対象: 画像全体 (3×10)", Profile(1, 2, 3), new ProjectionAxis(0, null), 4095);
            Assert.EndsWith("[px・画像座標]", title.Text);
            Assert.Contains("元画像上の画素座標", (string)axis.ToolTip);
            Assert.StartsWith("x,mean,min,max" + nl + "0,1,0,2", horizontal.BuildTable(','));
        }
        finally
        {
            horizontal.Close();
            vertical.Close();
        }
    });

    [Fact]
    public Task Statistics_ShowSubCodeMinMaxAndPeakToPeak() => WpfTestHost.Run(() =>
    {
        // 射影は平均(実数)で 1 code 未満の列ムラを見るための値なので、統計の最小・最大・P-P を整数に丸めず小数2桁で出す
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
        var window = NewWindow(ProjectionDirection.Horizontal);
        try
        {
            ShowRoiResult(window);

            string stats = Find<TextBlock>(window, "StatsText").Text;
            Assert.StartsWith("N=3   平均 1000.52", stats);
            Assert.Contains("最小 1000.40", stats);
            Assert.Contains("最大 1000.60", stats);
            Assert.EndsWith("P-P 0.20", stats);
        }
        finally
        {
            window.Close();
            CultureInfo.CurrentCulture = previousCulture;
        }
    });

    [Fact]
    public Task HidingMaxMin_FitsAutoScaleToSubCodeMeanAndKeepsWheelLimit() => WpfTestHost.Run(() =>
    {
        // 最大・最小の線を出しているときの自動の縦軸はその線も入れる。消すと平均の線だけに合わせ、1 code 未満のムラが
        // 見える。ホイールでの拡大は1 raw code幅までなので、自動範囲が既に1未満ならそれ以上拡大しないが縮小はできる
        var window = NewWindow(ProjectionDirection.Horizontal);
        try
        {
            window.ShowResult("対象: 画像全体 (3×3)",
                new ProjectionProfile
                {
                    Mean = new[] { 1000.001, 1000.002, 1000.003 },
                    Min = new double[] { 990, 991, 992 },
                    Max = new double[] { 1010, 1011, 1012 },
                    SamplesPerPosition = 3,
                },
                new ProjectionAxis(0, null), 4095);
            var mode = Find<ComboBox>(window, "YScaleCombo");
            mode.SelectedIndex = 1;
            Assert.InRange(window.AxisRange.Minimum, 988, 990);
            Assert.InRange(window.AxisRange.Maximum, 1012, 1014);
            CaptureIfRequested(window, "projection-auto-extremes");

            Find<CheckBox>(window, "ExtremesCheck").IsChecked = false;
            var canvas = Find<Canvas>(window, "PlotCanvas");
            Assert.Single(canvas.Children.OfType<Path>());
            ProfileAxisRange original = window.AxisRange;
            double span = original.Maximum - original.Minimum;
            Assert.InRange(span, 0.001, 0.01);
            CaptureIfRequested(window, "projection-auto-mean");
            var center = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
            for (int i = 0; i < 20; i++) window.ZoomAt(center, 1200, false, true);
            Assert.Equal(original, window.AxisRange);
            Assert.Equal(1, mode.SelectedIndex);
            window.ZoomAt(center, -120, false, true);
            Assert.Equal(span * 1.2, window.AxisRange.Maximum - window.AxisRange.Minimum, 8);

            // 線を消しても表(コピー・CSV)には最大・最小を出す
            Assert.StartsWith("x,mean,min,max" + Environment.NewLine + "0,1000.001,990,1010", window.BuildTable(','));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Busy_KeepsZoomButNeverExportsPreviousValues() => WpfTestHost.Run(() =>
    {
        // 計算中(ROI・送りの変更の後)は前の結果を薄く残して横軸の拡大を保つが、前の対象の値をコピー・CSV に出さない
        var window = NewWindow(ProjectionDirection.Horizontal);
        try
        {
            window.ShowResult("対象: 画像全体 (2000×10)", Ramp(2000), new ProjectionAxis(0, null), 4095);
            var canvas = Find<Canvas>(window, "PlotCanvas");
            window.ZoomAt(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 240, true, false);
            ProfileAxisRange zoomed = window.HorizontalRange;

            window.ShowBusy("対象: 画像全体 (2000×10) · フレーム 2/3");

            Assert.True(window.IsBusy);
            Assert.Null(window.BuildTable(','));
            Assert.Null(window.BuildStatisticsTable());
            var overlay = Find<TextBlock>(window, "OverlayText");
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.StartsWith("計算中", overlay.Text);
            Assert.StartsWith("計算中", Find<TextBlock>(window, "StatsText").Text);
            Assert.Equal(0.35, canvas.Opacity, 3);
            Assert.Equal("対象: 画像全体 (2000×10) · フレーム 2/3", Find<TextBlock>(window, "TargetText").Text);

            window.ReportProgress(0.427);
            Assert.StartsWith("計算中… 42%", overlay.Text);
            CaptureIfRequested(window, "projection-busy");

            // 同じ長さの結果なら横軸の拡大を保つ
            window.ShowResult("対象: 画像全体 (2000×10) · フレーム 2/3", Ramp(2000), new ProjectionAxis(0, null), 4095);
            Assert.False(window.IsBusy);
            Assert.Equal(zoomed, window.HorizontalRange);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.Equal(1, canvas.Opacity);
            Assert.NotNull(window.BuildTable(','));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Busy_AppearsOnlyAfterDelaySoQuickResultsDoNotFlicker() => WpfTestHost.Run(() =>
    {
        var window = NewWindow(ProjectionDirection.Horizontal);
        try
        {
            window.BusyDelay = TimeSpan.FromSeconds(10);
            ShowRoiResult(window);
            window.ShowBusy("対象: 画像全体 (3×3)");

            // コピーはすぐ止めるが、見た目(薄くする・知らせ)は遅らせる
            Assert.Null(window.BuildTable(','));
            Assert.Equal(Visibility.Collapsed, Find<TextBlock>(window, "OverlayText").Visibility);
            Assert.Equal(1, Find<Canvas>(window, "PlotCanvas").Opacity);
            Assert.StartsWith("N=3", Find<TextBlock>(window, "StatsText").Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Message_ClearsPreviousResultAndShowsReason() => WpfTestHost.Run(() =>
    {
        // 断る理由(象限をまたぐ ROI・HDR 分割ビューの画像全体の垂直射影など)や再生中の知らせでは、前の結果を消す
        // (別の対象の値と誤読させない)。縦軸の手動設定は保つ
        var window = NewWindow(ProjectionDirection.Vertical);
        try
        {
            var mode = Find<ComboBox>(window, "YScaleCombo");
            mode.SelectedIndex = 2;
            Find<TextBox>(window, "YMinimumBox").Text = "100";
            Find<TextBox>(window, "YMaximumBox").Text = "300";
            Find<Button>(window, "ApplyYScaleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ShowRoiResult(window);

            window.ShowMessage("対象: ROI なし", ProjectionTargets.ChannelSplitWithoutRoi, 1023);

            Assert.Null(window.BuildTable(','));
            Assert.Empty(Find<Canvas>(window, "PlotCanvas").Children.OfType<Path>());
            Assert.Equal(ProjectionTargets.ChannelSplitWithoutRoi, Find<TextBlock>(window, "StatsText").Text);
            Assert.Equal("対象: ROI なし", Find<TextBlock>(window, "TargetText").Text);
            Assert.False(window.IsBusy);
            CaptureIfRequested(window, "projection-message");

            ShowRoiResult(window);
            Assert.Equal(new ProfileAxisRange(100, 300), window.AxisRange);
            Assert.NotNull(window.BuildTable(','));
        }
        finally
        {
            window.Close();
        }
    });

    private static void ShowRoiResult(ProjectionWindow window)
    {
        window.ShowResult(
            "対象: ROI (100, 200, 3×1000)",
            new ProjectionProfile
            {
                Mean = new[] { 1000.40, 1000.55, 1000.60 },
                Min = new double[] { 998, 999, 997 },
                Max = new double[] { 1003, 1002, 1004 },
                SamplesPerPosition = 1000,
            },
            new ProjectionAxis(100, null), 4095);
    }

    /// <summary>平均 v、最小 v−1、最大 v+1 の射影(各位置 1000 画素)。</summary>
    private static ProjectionProfile Profile(params double[] means) => new()
    {
        Mean = means,
        Min = means.Select(v => v - 1).ToArray(),
        Max = means.Select(v => v + 1).ToArray(),
        SamplesPerPosition = 1000,
    };

    private static ProjectionProfile Ramp(int length) =>
        Profile(Enumerable.Range(0, length).Select(i => 1000.0 + (i % 7)).ToArray());

    private static ProjectionWindow NewWindow(ProjectionDirection direction)
    {
        var window = new ProjectionWindow(direction) { BusyDelay = TimeSpan.Zero };
        Layout(window);
        return window;
    }
}
