using System.Windows;
using System.Windows.Input;
using RawAnalyzer.App.Views;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    private bool _draggingGraphDock;

    private void InitializeGraphDock()
    {
        AnalysisGraphDock.Restore(_session.GraphDockHeight, _session.FloatingGraphs);
        AnalysisGraphDock.LayoutRequested += UpdateGraphDock;
        GraphDockSplitter.DragStarted += (_, _) => _draggingGraphDock = true;
        GraphDockSplitter.DragCompleted += (_, e) =>
        {
            _draggingGraphDock = false;
            if (!e.Canceled)
                CaptureGraphDockHeight();
            UpdateGraphDock();
        };
        GraphDockSplitter.AddHandler(Keyboard.KeyDownEvent,
            new KeyEventHandler((_, e) =>
            {
                if (e.Handled && e.Key is Key.Up or Key.Down)
                    CaptureGraphDockHeight();
            }), handledEventsToo: true);
    }

    private void CaptureGraphDockHeight()
    {
        if (GraphDockSplitter.Visibility == Visibility.Visible && GraphDockRow.Height.IsAbsolute)
            AnalysisGraphDock.RequestedHeight = GraphDockRow.Height.Value;
    }

    private void OnGraphLayoutSizeChanged(object sender, SizeChangedEventArgs e) => UpdateGraphDock();

    private void UpdateGraphDock()
    {
        if (AnalysisGraphDock is null || _closed || _draggingGraphDock)
            return;

        var layout = AnalysisGraphDock.ArrangeDock(
            GraphLayout.ActualWidth - AnalysisGraphDock.Margin.Left, GraphLayout.ActualHeight, _compareMode);
        ViewportRow.MinHeight = layout.Height > 0 ? GraphDock.MinimumImageHeight : 0;
        if (GraphDockRow.MinHeight > layout.Maximum)
            GraphDockRow.MinHeight = 0;
        GraphDockRow.MaxHeight = layout.Maximum;
        GraphDockRow.MinHeight = layout.Minimum;
        GraphDockRow.Height = new GridLength(layout.Height);
        GraphSplitterRow.Height = new GridLength(layout.Expanded ? GraphDock.SplitterHeight : 0);
        GraphDockSplitter.Visibility = layout.Expanded ? Visibility.Visible : Visibility.Collapsed;
    }
}
