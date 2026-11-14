using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace RawAnalyzer.App.Views;

/// <summary>解析グラフを表示する独立ウィンドウの共通部分。</summary>
public class GraphWindow : Window
{
    internal GraphWindow(GraphView view, double height, double minHeight)
    {
        Graph = view;
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
        Closed += (_, _) =>
        {
            if (Content is not null)
                view.NotifyClosed();
        };
    }

    internal GraphView Graph { get; }

    internal void AddDockButton(Button button)
    {
        Content = null;
        DockPanel.SetDock(button, Dock.Top);
        var panel = new DockPanel();
        panel.Children.Add(button);
        panel.Children.Add(Graph);
        Content = panel;
    }

    internal void Detach()
    {
        if (Content is DockPanel panel)
            panel.Children.Remove(Graph);
        Content = null;
    }
}
