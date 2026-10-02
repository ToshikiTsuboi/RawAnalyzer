using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class LineProfileWindowTests
{
    [Fact]
    public Task ScaleModes_UpdateAxesAndKeepRawStatistics() => WpfTestHost.Run(() =>
    {
        // 手動入力欄の表示文字列("998.5")を比較するため、このテストだけ文化を固定する。
        // 共有STAスレッドの CurrentCulture を他の UI テストへ持ち越さないよう finally で戻す。
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
        var window = NewWindow();
        try
        {
            var mode = ProfileWindowParts.Find<ComboBox>(window, "YScaleCombo");
            var min = ProfileWindowParts.Find<TextBox>(window, "YMinimumBox");
            var max = ProfileWindowParts.Find<TextBox>(window, "YMaximumBox");
            var apply = ProfileWindowParts.Find<Button>(window, "ApplyYScaleButton");
            var error = ProfileWindowParts.Find<TextBlock>(window, "YScaleErrorText");
            Assert.Equal(new ProfileAxisRange(0, 4095), window.AxisRange);
            Assert.False(min.IsEnabled);
            string statistics = (ProfileWindowParts.Find<TextBlock>(window, "StatsText")).Text;
            ProfileWindowParts.CaptureIfRequested(window, "profile-full");

            // 縦軸の右クリックメニューはコンボボックスと同じモード切替
            (ProfileWindowParts.Find<MenuItem>(window, "YAutoRangeMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(1, mode.SelectedIndex);
            Assert.InRange(window.AxisRange.Minimum, 994, 995);
            Assert.InRange(window.AxisRange.Maximum, 1005, 1006);
            ProfileWindowParts.CaptureIfRequested(window, "profile-auto");

            mode.SelectedIndex = 2;
            Assert.True(min.IsEnabled);
            ProfileAxisRange frozen = window.AxisRange; // 手動へ切り替えた時点の表示範囲が固定される

            // 不正な入力は適用できず、クリックしても最後の範囲を置き換えない
            min.Text = "abc";
            Assert.False(apply.IsEnabled);
            Assert.Equal(Visibility.Visible, error.Visibility);
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(frozen, window.AxisRange);

            min.Text = "998.5";
            max.Text = "1001.5";
            Assert.True(apply.IsEnabled);
            Assert.Equal(Visibility.Collapsed, error.Visibility);
            Assert.Equal(frozen, window.AxisRange); // 入力だけでは未反映
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new ProfileAxisRange(998.5, 1001.5), window.AxisRange);
            Assert.Equal(statistics, (ProfileWindowParts.Find<TextBlock>(window, "StatsText")).Text);
            ProfileWindowParts.CaptureIfRequested(window, "profile-manual");

            var canvas = ProfileWindowParts.Find<Canvas>(window, "PlotCanvas");
            var plot = Assert.Single(canvas.Children.OfType<System.Windows.Shapes.Path>());
            Assert.InRange(plot.Data.Bounds.Top, 0, canvas.ActualHeight);
            Assert.InRange(plot.Data.Bounds.Bottom, 0, canvas.ActualHeight);
            Assert.All(canvas.Children.OfType<Line>(), line =>
            {
                Assert.InRange(line.Y1, 0, canvas.ActualHeight);
                Assert.InRange(line.Y2, 0, canvas.ActualHeight);
            });

            mode.SelectedIndex = 0;
            Assert.Equal(new ProfileAxisRange(0, 4095), window.AxisRange);
            mode.SelectedIndex = 2;
            Assert.Equal(new ProfileAxisRange(998.5, 1001.5), window.AxisRange);
            Assert.Equal("998.5", min.Text);

            // スケール設定を開くだけでは表示範囲を変えず、表示中の値を丸めずに手動欄へ引き継ぐ
            mode.SelectedIndex = 1;
            ProfileAxisRange auto = window.AxisRange;
            window.PrepareYScaleEditor(); // OSのポップアップを表示せず内容だけ検証
            Assert.Equal(auto, window.AxisRange);
            Assert.Equal(2, mode.SelectedIndex);
            Assert.Equal(auto.Minimum.ToString("G17", CultureInfo.CurrentCulture), min.Text);
            ProfileWindowParts.CaptureElementIfRequested((FrameworkElement)(ProfileWindowParts.Find<Popup>(window, "YScalePopup")).Child,
                "profile-axis-editor", 330, 280);
        }
        finally
        {
            window.Close();
            CultureInfo.CurrentCulture = previousCulture;
        }
    });

    [Fact]
    public Task ManualScale_InvalidLimit_MarksTheFieldWithReason() => WpfTestHost.Run(() =>
    {
        // 以前は欄の下に「有限の数値で「最小 ＜ 最大」を…」と出すだけで、どちらの欄が悪いのかは欄の見た目で
        // 分からなかった。ファイル一覧の絞り込み欄と同じく、悪い欄を赤枠にしてツールチップに理由を出す
        var window = NewWindow();
        try
        {
            var mode = ProfileWindowParts.Find<ComboBox>(window, "YScaleCombo");
            var min = ProfileWindowParts.Find<TextBox>(window, "YMinimumBox");
            var max = ProfileWindowParts.Find<TextBox>(window, "YMaximumBox");
            mode.SelectedIndex = 2;
            object maxHelp = max.ToolTip;
            FieldFeedback.AssertValid(min);
            FieldFeedback.AssertValid(max);

            min.Text = "abc";
            Assert.Contains("有限の数値", FieldFeedback.AssertInvalid(min));
            FieldFeedback.AssertValid(max);

            min.Text = "2000";
            max.Text = "1000";
            FieldFeedback.AssertValid(min);
            Assert.Contains("最小値より大きい値", FieldFeedback.AssertInvalid(max));

            max.Text = "3000";
            FieldFeedback.AssertValid(max);
            Assert.Equal(maxHelp, max.ToolTip);

            min.Text = "NaN";
            FieldFeedback.AssertInvalid(min);
            mode.SelectedIndex = 0; // 手動でなければ欄は使わない(無効になる)ので知らせない
            FieldFeedback.AssertValid(min);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ManualScale_PersistsAcrossDirectionAndDataUpdates() => WpfTestHost.Run(() =>
    {
        var window = NewWindow();
        try
        {
            var mode = ProfileWindowParts.Find<ComboBox>(window, "YScaleCombo");
            mode.SelectedIndex = 2;
            (ProfileWindowParts.Find<TextBox>(window, "YMinimumBox")).Text = "-10.25";
            (ProfileWindowParts.Find<TextBox>(window, "YMaximumBox")).Text = "1010.5";
            (ProfileWindowParts.Find<Button>(window, "ApplyYScaleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var expected = new ProfileAxisRange(-10.25, 1010.5);
            (ProfileWindowParts.Find<RadioButton>(window, "VerticalRadio")).IsChecked = true;
            Assert.Equal(expected, window.AxisRange);
            window.SetProfiles(new double[] { 1, 2 }, new double[] { 10, 20 }, 0, 0, 255);
            Assert.Equal(expected, window.AxisRange);
            mode.SelectedIndex = 0;
            Assert.Equal(new ProfileAxisRange(0, 255), window.AxisRange);

            // 自動スケールは空データでは全範囲、1点では±0.5に落ち、再描画で例外にならない
            mode.SelectedIndex = 1;
            window.SetProfiles(Array.Empty<double>(), Array.Empty<double>(), 0, 0, 1023);
            Assert.Equal(new ProfileAxisRange(0, 1023), window.AxisRange);
            Assert.Equal("1023", (ProfileWindowParts.Find<TextBlock>(window, "MaxLabel")).Text);
            window.SetProfiles(new double[] { 25 }, new double[] { 25 }, 0, 0, 1023);
            Assert.Equal(new ProfileAxisRange(24.5, 25.5), window.AxisRange);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task WheelZoom_PreservesCursorCoordinateAndRawData() => WpfTestHost.Run(() =>
    {
        var window = NewWindow();
        try
        {
            (ProfileWindowParts.Find<ComboBox>(window, "YScaleCombo")).SelectedIndex = 1;
            string? data = window.BuildTable(',');
            string stats = (ProfileWindowParts.Find<TextBlock>(window, "StatsText")).Text;
            var canvas = ProfileWindowParts.Find<Canvas>(window, "PlotCanvas");
            var cursor = new Point(canvas.ActualWidth * 0.3, 1 + (canvas.ActualHeight - 2) * 0.7);
            ProfileAxisRange x = window.HorizontalRange;
            ProfileAxisRange y = window.AxisRange;
            for (int i = 0; i < 5; i++) window.ZoomAt(cursor, 120);
            Assert.True(window.HorizontalRange.Maximum - window.HorizontalRange.Minimum < (x.Maximum - x.Minimum) / 2);
            Assert.Equal(x.Minimum + (x.Maximum - x.Minimum) * 0.3,
                window.HorizontalRange.Minimum + (window.HorizontalRange.Maximum - window.HorizontalRange.Minimum) * 0.3, 8);
            Assert.Equal(y.Minimum + (y.Maximum - y.Minimum) * 0.3,
                window.AxisRange.Minimum + (window.AxisRange.Maximum - window.AxisRange.Minimum) * 0.3, 8);
            Assert.Equal(data, window.BuildTable(','));
            Assert.Equal(stats, (ProfileWindowParts.Find<TextBlock>(window, "StatsText")).Text);
            ProfileWindowParts.CaptureIfRequested(window, "profile-zoomed");
            window.ResetView();
            Assert.Equal(new ProfileAxisRange(0, 1999), window.HorizontalRange);
            Assert.Equal(new ProfileAxisRange(0, 4095), window.AxisRange);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task IndependentAxisZoomAndDrag_UpdateOnlyRequestedRanges() => WpfTestHost.Run(() =>
    {
        var window = NewWindow();
        try
        {
            var canvas = ProfileWindowParts.Find<Canvas>(window, "PlotCanvas");
            var center = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
            ProfileAxisRange originalY = window.AxisRange;
            window.ZoomAt(center, 120, zoomHorizontal: true, zoomVertical: false);
            Assert.Equal(originalY, window.AxisRange);
            Assert.Equal(0, (ProfileWindowParts.Find<ComboBox>(window, "YScaleCombo")).SelectedIndex);
            ProfileAxisRange originalX = window.HorizontalRange;
            window.ZoomAt(center, 120, zoomHorizontal: false, zoomVertical: true);
            Assert.Equal(originalX, window.HorizontalRange);
            ProfileAxisRange y = window.AxisRange;
            window.PanFrom(originalX, y, new Vector(canvas.ActualWidth * 0.05, canvas.ActualHeight * 0.1));
            Assert.True(window.HorizontalRange.Minimum < originalX.Minimum);
            Assert.True(window.AxisRange.Minimum > y.Minimum);
            Assert.Equal(originalX.Maximum - originalX.Minimum,
                window.HorizontalRange.Maximum - window.HorizontalRange.Minimum, 8);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(255, 0)]
    [InlineData(4095, 0.37)]
    public Task WheelZoom_StopsAtOneRawCodeAndCanZoomBackOut(int maxCode, double anchor) => WpfTestHost.Run(() =>
    {
        var window = NewWindow();
        try
        {
            window.SetProfiles(new double[] { 0, maxCode }, new double[] { 0, maxCode }, 0, 0, maxCode);
            var canvas = ProfileWindowParts.Find<Canvas>(window, "PlotCanvas");
            var position = new Point(canvas.ActualWidth / 2, 1 + (1 - anchor) * (canvas.ActualHeight - 2));
            for (int i = 0; i < 30; i++) window.ZoomAt(position, 1200, false, true);
            ProfileAxisRange zoomed = window.AxisRange;
            Assert.Equal(1, zoomed.Maximum - zoomed.Minimum, 8);
            Assert.Equal(maxCode * anchor, zoomed.Minimum + anchor, 8);
            window.ZoomAt(position, 120, false, true);
            Assert.Equal(zoomed, window.AxisRange);
            window.ZoomAt(position, -120, false, true);
            Assert.Equal(1.2, window.AxisRange.Maximum - window.AxisRange.Minimum, 8);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AxisLabels_IdentifyDirectionInImageCoordinates() => WpfTestHost.Run(() =>
    {
        // 断面(行・列プロファイル)はチャネル分割表示でも元画像の列・行なので、横軸は画像座標。ROI の射影は
        // 射影の窓へ移した(ProjectionWindowTests)
        var window = NewWindow();
        try
        {
            var title = ProfileWindowParts.Find<TextBlock>(window, "XAxisTitle");
            var axis = ProfileWindowParts.Find<Canvas>(window, "XAxisCanvas");
            Assert.Equal("水平プロファイル — x座標 [px・画像座標]", title.Text);
            Assert.Contains("元画像上の画素座標", (string)axis.ToolTip);
            ProfileWindowParts.Find<RadioButton>(window, "VerticalRadio").IsChecked = true;
            Assert.Equal("垂直プロファイル — y座標 [px・画像座標]", title.Text);
            Assert.StartsWith("y,value" + Environment.NewLine + "0,200", window.BuildTable(','));
            Assert.Equal(new[] { "0", "1", "2", "3" }, axis.Children.OfType<TextBlock>().Select(t => t.Text));

            // ROI の射影を選ぶ欄はもうない(射影は射影の窓で見る)
            Assert.Null(window.FindName("ProjectionCheck"));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Statistics_ShowIntegerMinMaxAndPeakToPeak() => WpfTestHost.Run(() =>
    {
        // 断面は整数の raw code なので、統計の最小・最大・P-P は整数で出す(射影の窓は小数2桁)
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
        var window = NewWindow();
        try
        {
            window.SetProfiles(new double[] { 1000, 1001, 1003 }, new double[] { 1, 2, 3 }, 0, 0, 4095);
            string stats = ProfileWindowParts.Find<TextBlock>(window, "StatsText").Text;
            Assert.Contains("最小 1000 ", stats);
            Assert.Contains("最大 1003 ", stats);
            Assert.EndsWith("P-P 3", stats);

            // 表(コピー・CSV)は 5 桁の raw code でもそのまま整数
            string nl = Environment.NewLine;
            window.SetProfiles(new double[] { 40000, 40001, 40003 }, new double[] { 1, 2, 3 }, 0, 0, 65535);
            Assert.Equal($"x,value{nl}0,40000{nl}1,40001{nl}2,40003{nl}", window.BuildTable(','));
        }
        finally
        {
            window.Close();
            CultureInfo.CurrentCulture = previousCulture;
        }
    });

    [Fact]
    public Task DataOrDirectionChanges_ResetOnlyObsoleteHorizontalZoom() => WpfTestHost.Run(() =>
    {
        var window = NewWindow();
        try
        {
            var canvas = ProfileWindowParts.Find<Canvas>(window, "PlotCanvas");
            window.ZoomAt(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 240);
            ProfileAxisRange zoomedY = window.AxisRange;
            (ProfileWindowParts.Find<RadioButton>(window, "VerticalRadio")).IsChecked = true;
            Assert.Equal(new ProfileAxisRange(0, 3), window.HorizontalRange);
            Assert.Equal(zoomedY, window.AxisRange);
            window.ZoomAt(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 120, true, false);
            ProfileAxisRange x = window.HorizontalRange;
            window.SetProfiles(new double[] { 1, 2 }, new double[] { 10, 20, 30, 40 }, 1, 1, 255);
            Assert.Equal(x, window.HorizontalRange); // 同じ向き・長さの別ラインでは維持
            window.SetProfiles(new double[] { 1, 2 }, new double[] { 10, 20 }, 1, 1, 255);
            Assert.Equal(new ProfileAxisRange(0, 1), window.HorizontalRange);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task OutsideImage_ClearsPreviousProfileAndKeepsPointAndDirection() => WpfTestHost.Run(() =>
    {
        // 送り・差し替えの後は同じ基準点で計算し直すが、寸法の違う画像では基準点が範囲外になり得る。
        // 前の画像の断面・統計を残すと送った先の画像の値と誤読されるので、範囲外であることを示して
        // データを空にする(コピー・CSVにも出さない)。基準点・方向・縦軸の設定は保ち、範囲内の画像へ
        // 戻れば同じ点・同じ方向で出し直せるようにする
        var window = NewWindow();
        try
        {
            (ProfileWindowParts.Find<RadioButton>(window, "VerticalRadio")).IsChecked = true;
            var mode = ProfileWindowParts.Find<ComboBox>(window, "YScaleCombo");
            mode.SelectedIndex = 2;
            (ProfileWindowParts.Find<TextBox>(window, "YMinimumBox")).Text = "100";
            (ProfileWindowParts.Find<TextBox>(window, "YMaximumBox")).Text = "300";
            (ProfileWindowParts.Find<Button>(window, "ApplyYScaleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            window.ShowOutsideImage(1000, 2, 640, 480, 1023);

            Assert.Null(window.BuildTable(','));
            var canvas = ProfileWindowParts.Find<Canvas>(window, "PlotCanvas");
            Assert.Empty(canvas.Children.OfType<System.Windows.Shapes.Path>());
            string stats = (ProfileWindowParts.Find<TextBlock>(window, "StatsText")).Text;
            Assert.Contains("範囲外", stats);
            Assert.Contains("640×480", stats);
            Assert.Contains("範囲外", window.Title);
            Assert.True(window.IsOutsideImage);
            Assert.Equal((1000, 2), window.CurrentPoint);
            Assert.False(window.IsHorizontal);
            ProfileWindowParts.CaptureIfRequested(window, "profile-outside");

            window.SetProfiles(new double[] { 1, 2 }, new double[] { 7, 8, 9 }, 1000, 2, 1023);
            Assert.False(window.IsOutsideImage);
            Assert.False(window.IsHorizontal);
            Assert.StartsWith("y,value" + Environment.NewLine + "0,7", window.BuildTable(','));
            Assert.DoesNotContain("範囲外", (ProfileWindowParts.Find<TextBlock>(window, "StatsText")).Text);
            Assert.Equal(new ProfileAxisRange(100, 300), window.AxisRange);
        }
        finally
        {
            window.Close();
        }
    });

    private static LineProfileWindow NewWindow()
    {
        var window = new LineProfileWindow();
        double[] row = Enumerable.Range(0, 2000).Select(i => 1000.0 + 5 * Math.Sin(i / 30.0)).ToArray();
        double[] column = new double[] { 200, 220, 240, 220 };
        window.SetProfiles(row, column, 1000, 2, 4095);
        ProfileWindowParts.Layout(window);
        return window;
    }
}
