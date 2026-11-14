using System.Windows;
using System.Windows.Data;

namespace RawAnalyzer.App.Views;

/// <summary>解析グラフを表示する独立ウィンドウの共通部分。</summary>
public class GraphWindow : Window
{
    internal GraphWindow(GraphView view, double height, double minHeight)
    {
        Width = 820;
        Height = height;
        MinWidth = 620;
        MinHeight = minHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        Content = view;
        SetBinding(TitleProperty, new Binding(nameof(GraphView.GraphTitle)) { Source = view });
        Closed += (_, _) => view.PlotView.EndInteraction();
    }
}
