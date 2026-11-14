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
}
