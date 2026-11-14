using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.App.Views;

public partial class ProfilePlotView
{
    private int? _hoverIndex;
    internal event Action<int?>? HoverIndexChanged;

    internal void ShowHover(double x)
    {
        double width = PlotCanvas.ActualWidth;
        if (IsBusy || ProfilePlotNavigation.ToDataIndex(x, HorizontalRange, width, _data.Values.Length) is not int index)
        {
            ClearHover();
            return;
        }

        if (_hoverIndex == index)
        {
            return;
        }

        _hoverIndex = index;
        double lineX = Math.Clamp(ProfilePlotNavigation.ToCanvasX(index, HorizontalRange, width), 0, Math.Max(0, width - 0.5));
        HoverLine.X1 = HoverLine.X2 = lineX;
        HoverLine.Y2 = PlotCanvas.ActualHeight;
        string Format(double value) => value.ToString(_data.IntegerValues ? "F0" : "F2", CultureInfo.CurrentCulture);
        string valueLabel = _data.IntegerValues ? "値" : "平均";
        string reading = $"{_data.CoordinateOffset + index} px  {valueLabel} {Format(_data.Values[index])}";
        if (_extraSeriesVisible && _data.ExtraSeries is { } extras)
        {
            foreach (ProfileSeries series in extras)
            {
                if (index < series.Values.Length)
                {
                    reading += $"  {series.Label} {Format(series.Values[index])}";
                }
            }
        }

        HoverReadout.MaxWidth = width;
        HoverText.Text = reading;
        HoverLayer.Visibility = Visibility.Visible;
        HoverReadout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double labelWidth = HoverReadout.DesiredSize.Width;
        double left = lineX + 6 + labelWidth <= width ? lineX + 6 : Math.Max(0, lineX - 6 - labelWidth);
        Canvas.SetLeft(HoverReadout, left);
        Canvas.SetTop(HoverReadout, 5);
        HoverIndexChanged?.Invoke(index);
    }

    internal void ClearHover()
    {
        if (_hoverIndex is null)
        {
            return;
        }

        _hoverIndex = null;
        HoverLayer.Visibility = Visibility.Collapsed;
        HoverIndexChanged?.Invoke(null);
    }

    private void OnPlotMouseLeave(object sender, MouseEventArgs e) => ClearHover();
}
