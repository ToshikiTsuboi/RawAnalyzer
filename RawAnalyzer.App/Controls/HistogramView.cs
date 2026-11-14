using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Themes;

namespace RawAnalyzer.App.Controls;

public sealed class HistogramView : FrameworkElement
{
    private const double AxisHeight = 17;
    private const double EdgeInset = 4;
    private static readonly Brush AxisBrush = ThemeColors.Brush(ThemeColors.TextDim);
    private static readonly Brush ClipBrush = ThemeColors.Brush(ThemeColors.AccentSoft);
    private static readonly Brush BlackHandle = ThemeColors.Brush(ThemeColors.Warning);
    private static readonly Brush WhiteHandle = ThemeColors.Brush(ThemeColors.Accent);
    private HistogramPlot? _plot;
    private bool? _dragBlack;

    public static readonly DependencyProperty BlackLevelProperty = RegisterLevel(nameof(BlackLevel), 0);
    public static readonly DependencyProperty WhiteLevelProperty = RegisterLevel(nameof(WhiteLevel), 65535);
    public static readonly DependencyProperty MaxCodeProperty = RegisterLevel(nameof(MaxCode), 65535);

    private static DependencyProperty RegisterLevel(string name, double initial) =>
        DependencyProperty.Register(name, typeof(double), typeof(HistogramView),
            new FrameworkPropertyMetadata(initial, FrameworkPropertyMetadataOptions.AffectsRender
                | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double BlackLevel
    {
        get => (double)GetValue(BlackLevelProperty);
        set => SetValue(BlackLevelProperty, value);
    }

    public double WhiteLevel
    {
        get => (double)GetValue(WhiteLevelProperty);
        set => SetValue(WhiteLevelProperty, value);
    }

    public double MaxCode
    {
        get => (double)GetValue(MaxCodeProperty);
        set => SetValue(MaxCodeProperty, value);
    }

    private double PlotWidth => Math.Max(0, ActualWidth - 2 * EdgeInset);
    internal int ColumnCount => Math.Max(1, (int)Math.Round(PlotWidth * VisualTreeHelper.GetDpi(this).DpiScaleX));
    internal event EventHandler? PlotSizeChanged;

    internal void SetPlot(HistogramPlot? plot)
    {
        _plot = plot;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.WidthChanged)
        {
            PlotSizeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
        PlotSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double height = Math.Max(0, ActualHeight - AxisHeight);
        double width = PlotWidth;
        if (_plot is not { } plot || width <= 0 || height <= 0)
        {
            return;
        }

        dc.PushClip(new RectangleGeometry(new Rect(EdgeInset, 0, width, height)));
        for (int s = 0; s < plot.Series.Length; s++)
        {
            double[] curve = plot.Series[s];
            Brush brush = ThemeColors.Brush(plot.Colors[s]);
            double columnWidth = width / curve.Length;
            for (int i = 0; i < curve.Length; i++)
            {
                double bar = curve[i] * Math.Max(0, height - 2);
                double y = height - bar;
                if (plot.ByChannel)
                {
                    dc.DrawRectangle(brush, null, new Rect(EdgeInset + i * columnWidth, y - 2, columnWidth, 2));
                }
                else if (bar > 0)
                {
                    dc.DrawRectangle(brush, null, new Rect(EdgeInset + i * columnWidth, y, columnWidth, bar));
                }
            }
        }

        double blackX = HistogramPlot.XForCode(BlackLevel, MaxCode, width);
        double whiteX = HistogramPlot.XForCode(WhiteLevel, MaxCode, width);
        dc.DrawRectangle(ClipBrush, null, new Rect(EdgeInset, 0, blackX, height));
        dc.DrawRectangle(ClipBrush, null, new Rect(EdgeInset + whiteX, 0, width - whiteX, height));
        dc.Pop();

        dc.DrawRectangle(AxisBrush, null, new Rect(EdgeInset, height, width, 1));
        DrawHandle(dc, EdgeInset + blackX, height, BlackHandle);
        DrawHandle(dc, EdgeInset + whiteX, height, WhiteHandle);
        var left = AxisText("0");
        var right = AxisText(MaxCode.ToString("0", CultureInfo.InvariantCulture));
        dc.DrawText(left, new Point(EdgeInset, height + 2));
        dc.DrawText(right, new Point(EdgeInset + width - right.Width, height + 2));
    }

    private FormattedText AxisText(string text) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface("Consolas"), ThemeTypography.FontSizeCaption, AxisBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static void DrawHandle(DrawingContext dc, double x, double height, Brush brush)
    {
        dc.DrawRectangle(brush, null, new Rect(x - 0.75, 0, 1.5, height));
        dc.DrawRoundedRectangle(brush, null, new Rect(x - 4, 0, 8, 7), 1, 1);
    }

    internal bool BeginLevelDrag(double x)
    {
        if (_plot is null || !IsEnabled)
        {
            return false;
        }

        double black = Math.Abs(x - EdgeInset - HistogramPlot.XForCode(BlackLevel, MaxCode, PlotWidth));
        double white = Math.Abs(x - EdgeInset - HistogramPlot.XForCode(WhiteLevel, MaxCode, PlotWidth));
        if (Math.Min(black, white) > 9)
        {
            return false;
        }

        _dragBlack = black <= white;
        return true;
    }

    internal void MoveLevelDrag(double x)
    {
        double value = HistogramPlot.CodeForX(x - EdgeInset, MaxCode, PlotWidth);
        if (_dragBlack == true)
        {
            SetCurrentValue(BlackLevelProperty, Math.Min(value, Math.Max(0, WhiteLevel - 1)));
        }
        else if (_dragBlack == false)
        {
            SetCurrentValue(WhiteLevelProperty, Math.Max(value, Math.Min(MaxCode, BlackLevel + 1)));
        }
    }

    internal void EndLevelDrag() => _dragBlack = null;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point point = e.GetPosition(this);
        if (point.Y <= ActualHeight - AxisHeight && BeginLevelDrag(point.X))
        {
            if (!CaptureMouse())
            {
                EndLevelDrag();
            }

            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragBlack.HasValue && e.LeftButton == MouseButtonState.Pressed)
        {
            MoveLevelDrag(e.GetPosition(this).X);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragBlack.HasValue)
        {
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndLevelDrag();
    }
}
