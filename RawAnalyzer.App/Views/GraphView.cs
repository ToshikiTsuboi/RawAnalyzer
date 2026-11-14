using System.Windows;
using System.Windows.Controls;

namespace RawAnalyzer.App.Views;

/// <summary>解析グラフの内容と、ホストのタイトルに使う表示文字列。</summary>
public abstract class GraphView : UserControl
{
    public static readonly DependencyProperty GraphTitleProperty = DependencyProperty.Register(
        nameof(GraphTitle), typeof(string), typeof(GraphView), new PropertyMetadata(""));

    public string GraphTitle
    {
        get => (string)GetValue(GraphTitleProperty);
        protected set => SetValue(GraphTitleProperty, value);
    }

    internal abstract ProfilePlotView PlotView { get; }

    internal abstract FrameworkElement Header { get; }

    internal event EventHandler? GraphClosed;
    internal event Action? ChromeChanged;

    protected void NotifyChromeChanged() => ChromeChanged?.Invoke();

    internal virtual void SetDocked(bool docked) => PlotView.SetDocked(docked);

    internal double DockChromeHeight(double width)
    {
        const double margin = 20; // グラフを囲む DockPanel の上下と左右
        double contentWidth = Math.Max(0, width - margin);
        Header.Measure(new Size(contentWidth, double.PositiveInfinity));
        return margin + Header.DesiredSize.Height + PlotView.DockChromeHeight(contentWidth);
    }

    internal void NotifyClosed()
    {
        PlotView.EndInteraction();
        GraphClosed?.Invoke(this, EventArgs.Empty);
    }
}
