using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            var mode = (ComboBox)window.FindName("YScaleCombo");
            var min = (TextBox)window.FindName("YMinimumBox");
            var max = (TextBox)window.FindName("YMaximumBox");
            var apply = (Button)window.FindName("ApplyYScaleButton");
            var error = (TextBlock)window.FindName("YScaleErrorText");
            Assert.Equal(new ProfileAxisRange(0, 4095), window.AxisRange);
            Assert.False(min.IsEnabled);
            string statistics = ((TextBlock)window.FindName("StatsText")).Text;
            CaptureIfRequested(window, "profile-full");

            // 縦軸の右クリックメニューはコンボボックスと同じモード切替
            ((MenuItem)window.FindName("YAutoRangeMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(1, mode.SelectedIndex);
            Assert.InRange(window.AxisRange.Minimum, 994, 995);
            Assert.InRange(window.AxisRange.Maximum, 1005, 1006);
            CaptureIfRequested(window, "profile-auto");

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
            Assert.Equal(statistics, ((TextBlock)window.FindName("StatsText")).Text);
            CaptureIfRequested(window, "profile-manual");

            var canvas = (Canvas)window.FindName("PlotCanvas");
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
            CaptureElementIfRequested((FrameworkElement)((Popup)window.FindName("YScalePopup")).Child,
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
            var mode = (ComboBox)window.FindName("YScaleCombo");
            var min = (TextBox)window.FindName("YMinimumBox");
            var max = (TextBox)window.FindName("YMaximumBox");
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
    public Task ManualScale_PersistsAcrossDirectionProjectionAndDataUpdates() => WpfTestHost.Run(() =>
    {
        var window = NewWindow();
        try
        {
            var mode = (ComboBox)window.FindName("YScaleCombo");
            mode.SelectedIndex = 2;
            ((TextBox)window.FindName("YMinimumBox")).Text = "-10.25";
            ((TextBox)window.FindName("YMaximumBox")).Text = "1010.5";
            ((Button)window.FindName("ApplyYScaleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var expected = new ProfileAxisRange(-10.25, 1010.5);
            ((RadioButton)window.FindName("VerticalRadio")).IsChecked = true;
            Assert.Equal(expected, window.AxisRange);
            ((CheckBox)window.FindName("ProjectionCheck")).IsChecked = true;
            Assert.Equal(expected, window.AxisRange);
            window.SetProfiles(new double[] { 1, 2 }, new double[] { 10, 20 }, Array.Empty<double>(),
                Array.Empty<double>(), null, 0, 0, 255);
            Assert.Equal(expected, window.AxisRange);
            mode.SelectedIndex = 0;
            Assert.Equal(new ProfileAxisRange(0, 255), window.AxisRange);

            // 自動スケールは空データでは全範囲、1点では±0.5に落ち、再描画で例外にならない
            mode.SelectedIndex = 1;
            window.SetProfiles(Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(),
                Array.Empty<double>(), null, 0, 0, 1023);
            Assert.Equal(new ProfileAxisRange(0, 1023), window.AxisRange);
            Assert.Equal("1023", ((TextBlock)window.FindName("MaxLabel")).Text);
            window.SetProfiles(new double[] { 25 }, new double[] { 25 }, Array.Empty<double>(),
                Array.Empty<double>(), null, 0, 0, 1023);
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
            ((ComboBox)window.FindName("YScaleCombo")).SelectedIndex = 1;
            string? data = window.BuildTable(',');
            string stats = ((TextBlock)window.FindName("StatsText")).Text;
            var canvas = (Canvas)window.FindName("PlotCanvas");
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
            Assert.Equal(stats, ((TextBlock)window.FindName("StatsText")).Text);
            CaptureIfRequested(window, "profile-zoomed");
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
            var canvas = (Canvas)window.FindName("PlotCanvas");
            var center = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
            ProfileAxisRange originalY = window.AxisRange;
            window.ZoomAt(center, 120, zoomHorizontal: true, zoomVertical: false);
            Assert.Equal(originalY, window.AxisRange);
            Assert.Equal(0, ((ComboBox)window.FindName("YScaleCombo")).SelectedIndex);
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
            window.SetProfiles(new double[] { 0, maxCode }, new double[] { 0, maxCode },
                Array.Empty<double>(), Array.Empty<double>(), null, 0, 0, maxCode);
            var canvas = (Canvas)window.FindName("PlotCanvas");
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
    public Task WheelZoom_PreservesSubCodeAutoRangeButAllowsZoomOut() => WpfTestHost.Run(() =>
    {
        var window = NewWindow();
        try
        {
            ((CheckBox)window.FindName("ProjectionCheck")).IsChecked = true;
            var mode = (ComboBox)window.FindName("YScaleCombo");
            mode.SelectedIndex = 1;
            ProfileAxisRange original = window.AxisRange;
            double span = original.Maximum - original.Minimum;
            Assert.InRange(span, 0.001, 0.01);
            var canvas = (Canvas)window.FindName("PlotCanvas");
            var center = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
            for (int i = 0; i < 20; i++) window.ZoomAt(center, 1200, false, true);
            Assert.Equal(original, window.AxisRange);
            Assert.Equal(1, mode.SelectedIndex);
            window.ZoomAt(center, -120, false, true);
            Assert.Equal(span * 1.2, window.AxisRange.Maximum - window.AxisRange.Minimum, 8);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task HorizontalLabels_IdentifyDirectionAndUseRoiImageCoordinates() => WpfTestHost.Run(() =>
    {
        var window = NewWindow();
        try
        {
            var title = (TextBlock)window.FindName("XAxisTitle");
            Assert.Contains("水平プロファイル", title.Text);
            Assert.Contains("x座標", title.Text);
            ((RadioButton)window.FindName("VerticalRadio")).IsChecked = true;
            Assert.Contains("垂直プロファイル", title.Text);
            Assert.Contains("y座標", title.Text);
            window.SetProfiles(new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 },
                new double[] { 10, 20, 30 }, new double[] { 40, 50, 60 },
                new RegionOfInterest(100, 200, 3, 3), 0, 0, 255);
            ((CheckBox)window.FindName("ProjectionCheck")).IsChecked = true;
            var axis = (Canvas)window.FindName("XAxisCanvas");
            Assert.Contains("垂直 ROI平均射影", title.Text);
            Assert.Equal(new[] { "200", "201", "202" }, axis.Children.OfType<TextBlock>().Select(t => t.Text));
            Assert.StartsWith("y,value" + Environment.NewLine + "200,40", window.BuildTable(','));
            CaptureIfRequested(window, "profile-vertical-projection");
            ((RadioButton)window.FindName("HorizontalRadio")).IsChecked = true;
            Assert.Contains("水平 ROI平均射影", title.Text);
            Assert.Equal(new[] { "100", "101", "102" }, axis.Children.OfType<TextBlock>().Select(t => t.Text));
            Assert.StartsWith("x,value" + Environment.NewLine + "100,10", window.BuildTable(','));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task SplitViewProjection_LabelsAxisAndTableAsDisplayCoordinates() => WpfTestHost.Run(() =>
    {
        // 全体レビュー 2026-10-01 B88。チャネル分割表示の ROI 平均射影は、ROI を描いた分割表示(タイル)の座標で
        // 横軸・CSV/TSV を出す(MainWindow が ChannelRoiTarget の DisplayRoi を渡す)。それなのに軸の見出し・
        // ツールチップは「画像座標」「元画像上の画素座標」、CSV の見出しは断面と同じ "x" で、元画像の列と
        // 読み違えていた。分割表示の射影では表示座標であることを見出し・ツールチップ・CSV の見出しに示す
        var window = NewWindow();
        try
        {
            var title = (TextBlock)window.FindName("XAxisTitle");
            var axis = (Canvas)window.FindName("XAxisCanvas");
            var projection = (CheckBox)window.FindName("ProjectionCheck");
            window.SetProfiles(new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 },
                new double[] { 10, 20, 30 }, new double[] { 40, 50, 60 },
                new RegionOfInterest(2100, 100, 3, 3), 0, 0, 4095,
                projectionSourceRegion: new ChannelRegion(201, 200, 3, 3));
            projection.IsChecked = true;

            Assert.Contains("分割表示の座標", title.Text);
            Assert.DoesNotContain("画像座標", title.Text);
            Assert.Contains("分割表示", (string)axis.ToolTip);
            Assert.DoesNotContain("元画像上の画素座標", (string)axis.ToolTip);
            Assert.StartsWith("x_display,", window.BuildTable(','));
            ((RadioButton)window.FindName("VerticalRadio")).IsChecked = true;
            Assert.StartsWith("y_display,", window.BuildTable(','));

            // 断面(行・列プロファイル)は分割表示でも元画像の列・行なので、従来どおり画像座標
            projection.IsChecked = false;
            Assert.Contains("画像座標", title.Text);
            Assert.Contains("元画像上の画素座標", (string)axis.ToolTip);
            Assert.StartsWith("y,value" + Environment.NewLine + "0,4", window.BuildTable(','));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task SplitViewProjection_TableAlsoHasSourceCoordinates() => WpfTestHost.Run(() =>
    {
        // 残課題 2026-10-02 A5。チャネル分割表示の射影の CSV/TSV は表示(タイル)の座標 x_display / y_display だけで、
        // 元画像のどの列・行の平均かを利用者が換算する必要があった。元画像の座標列(1チャネルの格子なので2画素おき)を
        // 並べる。4000×3000 の分割表示で、右上の象限の (2100, 100) から 3×3 = 元画像の x 201,203,205 / y 200,202,204
        var window = NewWindow();
        try
        {
            window.SetProfiles(new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 },
                new double[] { 10, 20, 30 }, new double[] { 40, 50, 60 },
                new RegionOfInterest(2100, 100, 3, 3), 0, 0, 4095,
                projectionSourceRegion: new ChannelRegion(201, 200, 3, 3));
            ((CheckBox)window.FindName("ProjectionCheck")).IsChecked = true;

            string nl = Environment.NewLine;
            Assert.Equal(
                $"x_display,x_source,value{nl}2100,201,10{nl}2101,203,20{nl}2102,205,30{nl}",
                window.BuildTable(','));
            ((RadioButton)window.FindName("VerticalRadio")).IsChecked = true;
            Assert.Equal(
                $"y_display\ty_source\tvalue{nl}100\t200\t40{nl}101\t202\t50{nl}102\t204\t60{nl}",
                window.BuildTable('\t'));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ProjectionStatistics_ShowSubCodeMinMaxAndPeakToPeak() => WpfTestHost.Run(() =>
    {
        // 全体レビュー 2026-10-01 B89。射影は ROI の直交方向の平均(実数)で、1 code 未満の列ムラを見るための
        // 値なのに、最小・最大・P-P を F0 に丸めて「最小 1000 最大 1001 P-P 0」のように矛盾した値を出していた。
        // 射影では平均・σ と同じ小数2桁で出す。整数の raw code の断面は従来どおり整数
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
        var window = NewWindow();
        try
        {
            var stats = (TextBlock)window.FindName("StatsText");
            double[] projection = { 1000.40, 1000.55, 1000.60 };
            window.SetProfiles(new double[] { 1000, 1001, 1003 }, new double[] { 1, 2, 3 },
                projection, projection, new RegionOfInterest(0, 0, 3, 1000), 0, 0, 4095);
            ((CheckBox)window.FindName("ProjectionCheck")).IsChecked = true;

            Assert.Contains("最小 1000.40", stats.Text);
            Assert.Contains("最大 1000.60", stats.Text);
            Assert.Contains("P-P 0.20", stats.Text);

            ((CheckBox)window.FindName("ProjectionCheck")).IsChecked = false;
            Assert.Contains("最小 1000 ", stats.Text);
            Assert.Contains("最大 1003 ", stats.Text);
            Assert.EndsWith("P-P 3", stats.Text);
        }
        finally
        {
            window.Close();
            CultureInfo.CurrentCulture = previousCulture;
        }
    });

    [Fact]
    public Task ProjectionTableAndStatisticsCopy_KeepSubCodeDigitsOfFiveDigitCodes() => WpfTestHost.Run(() =>
    {
        // 射影は 1 code 未満の列ムラを見る値なのに、表(コピー・CSV/TSV)と統計のコピーは有効数字 6 桁で、
        // 10000 code 以上(14bit・16bit の明るい画像)では小数 1 桁に丸まり、窓の統計(小数 2 桁)より粗かった
        // (40000.43 が 40000.4)。小数を落とさない桁数で出す。整数の断面は従来どおり整数
        var window = NewWindow();
        try
        {
            double[] projection = { 40000.43, 40000.57, 40001.25 };
            window.SetProfiles(new double[] { 40000, 40001, 40003 }, new double[] { 1, 2, 3 },
                projection, projection, new RegionOfInterest(10, 20, 3, 1000), 0, 0, 65535);
            ((CheckBox)window.FindName("ProjectionCheck")).IsChecked = true;

            string nl = Environment.NewLine;
            Assert.Equal($"x,value{nl}10,40000.43{nl}11,40000.57{nl}12,40001.25{nl}", window.BuildTable(','));
            string statistics = window.BuildStatisticsTable()!;
            Assert.Contains($"mean\t40000.75{nl}", statistics);
            Assert.Contains($"min\t40000.43{nl}", statistics);

            ((CheckBox)window.FindName("ProjectionCheck")).IsChecked = false;
            Assert.Equal($"x,value{nl}0,40000{nl}1,40001{nl}2,40003{nl}", window.BuildTable(','));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task DataOrDirectionChanges_ResetOnlyObsoleteHorizontalZoom() => WpfTestHost.Run(() =>
    {
        var window = NewWindow();
        try
        {
            var canvas = (Canvas)window.FindName("PlotCanvas");
            window.ZoomAt(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 240);
            ProfileAxisRange zoomedY = window.AxisRange;
            ((RadioButton)window.FindName("VerticalRadio")).IsChecked = true;
            Assert.Equal(new ProfileAxisRange(0, 3), window.HorizontalRange);
            Assert.Equal(zoomedY, window.AxisRange);
            window.ZoomAt(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 120, true, false);
            ProfileAxisRange x = window.HorizontalRange;
            window.SetProfiles(new double[] { 1, 2 }, new double[] { 10, 20, 30, 40 },
                Array.Empty<double>(), Array.Empty<double>(), null, 1, 1, 255);
            Assert.Equal(x, window.HorizontalRange); // 同じ向き・長さの別ラインでは維持
            window.SetProfiles(new double[] { 1, 2 }, new double[] { 10, 20 },
                Array.Empty<double>(), Array.Empty<double>(), null, 1, 1, 255);
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
        // 前の画像の断面・射影・統計を残すと送った先の画像の値と誤読されるので、範囲外であることを示して
        // データを空にする(コピー・CSVにも出さない)。基準点・方向・縦軸の設定は保ち、範囲内の画像へ
        // 戻れば同じ点・同じ方向で出し直せるようにする
        var window = NewWindow();
        try
        {
            ((RadioButton)window.FindName("VerticalRadio")).IsChecked = true;
            var projection = (CheckBox)window.FindName("ProjectionCheck");
            projection.IsChecked = true;
            var mode = (ComboBox)window.FindName("YScaleCombo");
            mode.SelectedIndex = 2;
            ((TextBox)window.FindName("YMinimumBox")).Text = "100";
            ((TextBox)window.FindName("YMaximumBox")).Text = "300";
            ((Button)window.FindName("ApplyYScaleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            window.ShowOutsideImage(1000, 2, 640, 480, 1023);

            Assert.Null(window.BuildTable(','));
            var canvas = (Canvas)window.FindName("PlotCanvas");
            Assert.Empty(canvas.Children.OfType<System.Windows.Shapes.Path>());
            string stats = ((TextBlock)window.FindName("StatsText")).Text;
            Assert.Contains("範囲外", stats);
            Assert.Contains("640×480", stats);
            Assert.Contains("範囲外", window.Title);
            Assert.False(projection.IsEnabled);
            Assert.False(projection.IsChecked);
            Assert.True(window.IsOutsideImage);
            Assert.Equal((1000, 2), window.CurrentPoint);
            Assert.False(window.IsHorizontal);
            CaptureIfRequested(window, "profile-outside");

            window.SetProfiles(new double[] { 1, 2 }, new double[] { 7, 8, 9 },
                Array.Empty<double>(), Array.Empty<double>(), null, 1000, 2, 1023);
            Assert.False(window.IsOutsideImage);
            Assert.False(window.IsHorizontal);
            Assert.StartsWith("y,value" + Environment.NewLine + "0,7", window.BuildTable(','));
            Assert.DoesNotContain("範囲外", ((TextBlock)window.FindName("StatsText")).Text);
            Assert.Equal(new ProfileAxisRange(100, 300), window.AxisRange);
        }
        finally
        {
            window.Close();
        }
    });

    private static void CaptureElementIfRequested(FrameworkElement element, string name, int width, int height)
    {
        string? directory = Environment.GetEnvironmentVariable("RAWANALYZER_UI_SNAPSHOTS");
        if (string.IsNullOrEmpty(directory)) return;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(System.IO.Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }

    private static LineProfileWindow NewWindow()
    {
        var window = new LineProfileWindow();
        double[] row = Enumerable.Range(0, 2000).Select(i => 1000.0 + 5 * Math.Sin(i / 30.0)).ToArray();
        double[] column = new double[] { 200, 220, 240, 220 };
        double[] projection = new double[] { 1000.001, 1000.002, 1000.003 };
        window.SetProfiles(row, column, projection, projection,
            new RegionOfInterest(0, 0, 3, 3), 1000, 2, 4095);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(800, 440));
        content.Arrange(new Rect(0, 0, 800, 440));
        content.UpdateLayout();
        return window;
    }

    private static void CaptureIfRequested(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("RAWANALYZER_UI_SNAPSHOTS");
        if (string.IsNullOrEmpty(directory)) return;
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(800, 440, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext dc = background.RenderOpen())
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, 800, 440));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(System.IO.Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }
}
