using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.App.Views;

public partial class ProfilePlotView
{
    private ProfileAxisRange? _horizontalRange;
    private Point? _panOrigin;
    private ProfileAxisRange _panHorizontal;
    private ProfileAxisRange _panVertical;

    /// <summary>表示中の横軸の範囲(データの位置で)。</summary>
    internal ProfileAxisRange HorizontalRange => _horizontalRange ?? ProfilePlotNavigation.FullHorizontal(_data.Values.Length);

    private void OnYAxisContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        YFullRangeMenu.IsChecked = YScaleCombo.SelectedIndex == 0;
        YAutoRangeMenu.IsChecked = YScaleCombo.SelectedIndex == 1;
        YManualRangeMenu.IsChecked = ManualScale;
    }

    private void OnYFullRangeClick(object sender, RoutedEventArgs e) => YScaleCombo.SelectedIndex = 0;

    private void OnYAutoRangeClick(object sender, RoutedEventArgs e) => YScaleCombo.SelectedIndex = 1;

    /// <summary>縦軸の手動設定を、表示中の範囲を引き継いで始める(ポップアップは開かない)。</summary>
    internal void PrepareYScaleEditor()
    {
        EndPan();
        // 設定を開いただけで以前の手動範囲へ戻らないよう、表示中の値を引き継ぐ。
        _manualRange = _axisRange;
        YScaleCombo.SelectedIndex = 2;
        UpdateScaleInputs();
    }

    private void OnYManualRangeClick(object sender, RoutedEventArgs e)
    {
        PrepareYScaleEditor();
        YScalePopup.IsOpen = true;
        YMinimumBox.Focus();
        YMinimumBox.SelectAll();
    }

    private void OnCloseYScaleClick(object sender, RoutedEventArgs e) => YScalePopup.IsOpen = false;

    private void OnYScalePopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        YScalePopup.IsOpen = false;
        e.Handled = true;
    }

    private void OnPlotMouseWheel(object sender, MouseWheelEventArgs e)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        ZoomAt(e.GetPosition(PlotCanvas), e.Delta, zoomHorizontal: !control || shift, zoomVertical: !shift || control);
        e.Handled = true;
    }

    private void OnYAxisMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(PlotCanvas), e.Delta, zoomHorizontal: false, zoomVertical: true);
        e.Handled = true;
    }

    private void OnXAxisMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(PlotCanvas), e.Delta, zoomHorizontal: true, zoomVertical: false);
        e.Handled = true;
    }

    /// <summary>ホイールでの拡大・縮小(位置はグラフの座標)。</summary>
    /// <param name="position">中心にする位置。</param>
    /// <param name="wheelDelta">ホイールの回転量。</param>
    /// <param name="zoomHorizontal">横軸を拡大・縮小するか。</param>
    /// <param name="zoomVertical">縦軸を拡大・縮小するか。</param>
    internal void ZoomAt(Point position, int wheelDelta, bool zoomHorizontal = true, bool zoomVertical = true)
    {
        int count = _data.Values.Length;
        if (count == 0 || wheelDelta == 0 || PlotCanvas.ActualWidth < 4 || PlotCanvas.ActualHeight < 4) return;
        EndPan();
        double factor = Math.Pow(1.2, -Math.Clamp(wheelDelta / 120.0, -10, 10));
        ProfileAxisRange? horizontal = null, vertical = null;
        if (zoomHorizontal)
        {
            ProfileAxisRange full = ProfilePlotNavigation.FullHorizontal(count);
            horizontal = ProfilePlotNavigation.Zoom(HorizontalRange, position.X / PlotCanvas.ActualWidth,
                factor, 1, full.Maximum - full.Minimum, full);
        }

        if (zoomVertical)
        {
            double span = _axisRange.Maximum - _axisRange.Minimum;
            // ホイールでの拡大は1 raw code幅まで。手動指定・射影の自動範囲が
            // 既に1未満なら、それ以上拡大しないが縮小は通常の倍率で行える。
            if (factor >= 1 || span > 1)
            {
                vertical = ProfilePlotNavigation.Zoom(_axisRange,
                    1 - (position.Y - 1) / (PlotCanvas.ActualHeight - 2), factor,
                    Math.Min(span, 1), Math.Max(span, _data.MaxCode * 16.0));
            }
        }

        ApplyNavigation(horizontal, vertical);
    }

    private void ApplyNavigation(ProfileAxisRange? horizontal, ProfileAxisRange? vertical)
    {
        if (horizontal is { } x) _horizontalRange = x;
        if (vertical is { } y && y != _axisRange)
        {
            _manualRange = y;
            _updatingScaleControls = true;
            YScaleCombo.SelectedIndex = 2;
            _updatingScaleControls = false;
        }

        Redraw();
        if (vertical is not null) UpdateScaleInputs();
    }

    private void OnPlotMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetView();
        }
        else if (_data.Values.Length > 0)
        {
            _panOrigin = e.GetPosition(PlotCanvas);
            _panHorizontal = HorizontalRange;
            _panVertical = _axisRange;
            if (PlotCanvas.CaptureMouse()) PlotCanvas.Cursor = Cursors.Hand;
            else _panOrigin = null;
        }

        e.Handled = true;
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        if (_panOrigin is not { } start) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPan();
            return;
        }

        Point position = e.GetPosition(PlotCanvas);
        PanFrom(_panHorizontal, _panVertical, position - start);
        e.Handled = true;
    }

    /// <summary>ドラッグでの移動(始めたときの範囲から、動かした量だけ)。</summary>
    /// <param name="horizontal">始めたときの横軸の範囲。</param>
    /// <param name="vertical">始めたときの縦軸の範囲。</param>
    /// <param name="movement">動かした量(グラフの座標)。</param>
    internal void PanFrom(ProfileAxisRange horizontal, ProfileAxisRange vertical, Vector movement)
    {
        int count = _data.Values.Length;
        if (count == 0 || PlotCanvas.ActualWidth < 4 || PlotCanvas.ActualHeight < 4) return;
        ProfileAxisRange full = ProfilePlotNavigation.FullHorizontal(count);
        ApplyNavigation(
            ProfilePlotNavigation.Pan(horizontal, -movement.X / PlotCanvas.ActualWidth, full),
            ProfilePlotNavigation.Pan(vertical, movement.Y / (PlotCanvas.ActualHeight - 2)));
    }

    private void OnPlotMouseUp(object sender, MouseButtonEventArgs e)
    {
        EndPan();
        e.Handled = true;
    }

    private void OnPlotLostMouseCapture(object sender, MouseEventArgs e) => EndPan();

    private void EndPan()
    {
        _panOrigin = null;
        if (PlotCanvas is null) return;
        PlotCanvas.Cursor = Cursors.Arrow;
        if (PlotCanvas.IsMouseCaptured) PlotCanvas.ReleaseMouseCapture();
    }

    /// <summary>両軸を全体表示へ戻す(縦軸は全範囲)。</summary>
    internal void ResetView()
    {
        EndPan();
        _horizontalRange = null;
        YScaleCombo.SelectedIndex = 0;
        Redraw();
    }

    private void OnResetViewClick(object sender, RoutedEventArgs e) => ResetView();

    private void DrawHorizontalAxis(double width, double height)
    {
        XAxisCanvas.Children.Clear();
        int count = _data.Values.Length;
        if (count == 0 || width < 4) return;
        long offset = _data.CoordinateOffset;
        ProfileAxisRange horizontal = HorizontalRange;
        var coordinates = new ProfileAxisRange(horizontal.Minimum + offset, horizontal.Maximum + offset);
        foreach (double coordinate in ProfilePlotNavigation.Ticks(coordinates, width))
        {
            double x = ProfilePlotNavigation.ToCanvasX(coordinate - offset, horizontal, width);
            XAxisCanvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = 4,
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
            });
            var label = new TextBlock
            {
                Text = coordinate.ToString("0", CultureInfo.CurrentCulture),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = Brushes.DarkGray,
                Tag = coordinate,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Clamp(x - label.DesiredSize.Width / 2, 0, Math.Max(0, width - label.DesiredSize.Width)));
            Canvas.SetTop(label, 5);
            XAxisCanvas.Children.Add(label);
            if (height >= 4)
            {
                PlotCanvas.Children.Add(new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = 0,
                    Y2 = height,
                    Stroke = new SolidColorBrush(Color.FromArgb(0x18, 0x9A, 0x9A, 0x95)),
                    StrokeThickness = 1,
                });
            }
        }
    }
}
