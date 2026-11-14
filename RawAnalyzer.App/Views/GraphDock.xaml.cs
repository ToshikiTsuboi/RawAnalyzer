using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Views;

internal enum GraphKind { LineProfile, HorizontalProjection, VerticalProjection }

/// <summary>解析グラフの表示先とドック内の選択を管理する。</summary>
public partial class GraphDock : UserControl
{
    internal const double DefaultHeight = 325;
    internal const double MinimumPlotHeight = 80;
    internal const double MinimumPlotWidth = 200;
    internal const double MinimumImageHeight = 180;
    internal const double SplitterHeight = 5;
    private const double PlotSideChrome = 20 + 92 + 2;

    private readonly Dictionary<GraphKind, Entry> _open = new();
    private readonly HashSet<GraphKind> _floating = new();
    private bool _changingTabs;
    private bool _compare;
    private bool _shuttingDown;
    private bool _layoutScheduled;

    internal event Action? LayoutRequested;
    internal GraphKind? SelectedKind => GraphTabs.SelectedItem is TabItem { Tag: GraphKind kind } ? kind : null;
    internal double RequestedHeight { get; set; } = DefaultHeight;

    public GraphDock() => InitializeComponent();

    internal void Restore(double? height, IEnumerable<GraphKind>? floating)
    {
        if (height is > 0 && double.IsFinite(height.Value))
            RequestedHeight = height.Value;
        _floating.UnionWith((floating ?? []).Where(kind => Enum.IsDefined(kind)));
    }

    internal GraphKind[] FloatingPreferences() => _floating.Order().ToArray();

    internal void ShowGraph(GraphKind kind, GraphView graph)
    {
        TabItem tab = new() { Header = TabLabel(kind), Tag = kind };
        AutomationProperties.SetName(tab, kind switch
        {
            GraphKind.LineProfile => "ラインプロファイル",
            GraphKind.HorizontalProjection => "水平射影",
            _ => "垂直射影",
        });
        var entry = new Entry(graph, tab);
        _open.Add(kind, entry);
        graph.GraphClosed += (_, _) => RemoveClosed(kind, entry);
        graph.ChromeChanged += OnChromeChanged;
        if (_floating.Contains(kind))
            ShowFloating(kind, entry);
        else
            AddTab(entry);
    }

    internal void SelectGraph(GraphKind kind)
    {
        if (_open.TryGetValue(kind, out Entry? entry) && entry.Window is null)
            GraphTabs.SelectedItem = entry.Tab;
    }

    internal void CloseGraph(GraphKind kind)
    {
        if (!_open.TryGetValue(kind, out Entry? entry))
            return;
        if (entry.Window is { } window)
            window.Close();
        else
            entry.Graph.NotifyClosed();
    }

    internal void Shutdown()
    {
        _shuttingDown = true;
        foreach (GraphKind kind in _open.Keys.ToArray())
            CloseGraph(kind);
    }

    internal (double Height, double Minimum, double Maximum, bool Expanded) ArrangeDock(
        double width, double availableHeight, bool compare)
    {
        _compare = compare;
        if (compare || GraphTabs.Items.Count == 0)
        {
            SetContent(null);
            Visibility = Visibility.Collapsed;
            return default;
        }

        Visibility = Visibility.Visible;
        double band = TabBand.Height;
        Entry? selected = SelectedKind is GraphKind kind ? _open.GetValueOrDefault(kind) : null;
        double maximum = Math.Max(0, availableHeight - MinimumImageHeight - SplitterHeight);
        if (selected is not null && width >= PlotSideChrome + MinimumPlotWidth && maximum >= band + MinimumPlotHeight)
        {
            GraphContent.Visibility = Visibility.Visible;
            SetContent(selected.Graph);
        }

        double minimum = band + Math.Ceiling(selected?.Graph.DockChromeHeight(width) ?? 0) + MinimumPlotHeight;
        if (selected is null || width < PlotSideChrome + MinimumPlotWidth || maximum < minimum)
        {
            SetContent(null);
            GraphContent.Visibility = Visibility.Collapsed;
            return (band, band, band, false);
        }

        return (Math.Clamp(RequestedHeight, minimum, maximum), minimum, maximum, true);
    }

    private void AddTab(Entry entry)
    {
        entry.Graph.SetDocked(true);
        GraphTabs.Items.Add(entry.Tab);
        GraphTabs.SelectedItem = entry.Tab;
        RequestLayout();
    }

    private void RemoveTab(Entry entry)
    {
        int index = GraphTabs.Items.IndexOf(entry.Tab);
        if (index < 0)
            return;

        _changingTabs = true;
        try
        {
            bool selected = ReferenceEquals(GraphTabs.SelectedItem, entry.Tab);
            GraphTabs.Items.RemoveAt(index);
            if (selected && GraphTabs.Items.Count > 0)
                GraphTabs.SelectedIndex = Math.Min(index, GraphTabs.Items.Count - 1);
        }
        finally
        {
            _changingTabs = false;
        }
        if (ReferenceEquals(GraphContent.Child, entry.Graph))
            SetContent(null);
        RequestLayout();
    }

    private void RemoveClosed(GraphKind kind, Entry entry)
    {
        entry.Graph.ChromeChanged -= OnChromeChanged;
        _open.Remove(kind);
        RemoveTab(entry);
    }

    private void ShowFloating(GraphKind kind, Entry entry)
    {
        entry.Graph.SetDocked(false);
        var owner = Window.GetWindow(this);
        GraphWindow window = entry.Graph switch
        {
            LineProfileView profile => new LineProfileWindow(profile),
            ProjectionGraphView projection => new ProjectionWindow(projection),
            _ => throw new InvalidOperationException(),
        };
        window.Owner = owner;
        window.ShowActivated = false;
        GraphWindow? other = _open.Values.Select(e => e.Window)
            .LastOrDefault(w => w is { IsVisible: true });
        if (other is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = other.Left + 40;
            window.Top = other.Top + 40;
        }
        var dock = new Button
        {
            Content = "ドックに戻す",
            Margin = new Thickness(10, 6, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        dock.Click += (_, _) =>
        {
            entry.Graph.PlotView.EndPointerInteraction();
            window.Detach();
            entry.Window = null;
            window.Close();
            _floating.Remove(kind);
            AddTab(entry);
        };
        window.AddDockButton(dock);
        entry.Window = window;
        window.Show();
    }

    private void OnCloseGraph(object sender, RoutedEventArgs e)
    {
        if (SelectedKind is GraphKind kind)
            CloseGraph(kind);
    }

    private void OnPopOutGraph(object sender, RoutedEventArgs e)
    {
        if (SelectedKind is GraphKind kind && _open.TryGetValue(kind, out Entry? entry))
        {
            RemoveTab(entry);
            _floating.Add(kind);
            ShowFloating(kind, entry);
        }
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, GraphTabs) && !_changingTabs)
            RequestLayout();
    }

    private void OnChromeChanged()
    {
        if (!_compare)
            RequestLayout();
    }

    private void RequestLayout()
    {
        if (_layoutScheduled || _shuttingDown)
            return;
        _layoutScheduled = true;
        _ = Dispatcher.InvokeAsync(() =>
        {
            _layoutScheduled = false;
            if (!_shuttingDown)
                LayoutRequested?.Invoke();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SetContent(GraphView? graph)
    {
        if (ReferenceEquals(GraphContent.Child, graph))
            return;
        (GraphContent.Child as GraphView)?.PlotView.EndPointerInteraction();
        GraphContent.Child = graph;
    }

    private static string TabLabel(GraphKind kind) => kind switch
    {
        GraphKind.LineProfile => "断面",
        GraphKind.HorizontalProjection => "水平",
        _ => "垂直",
    };

    private sealed class Entry(GraphView graph, TabItem tab)
    {
        internal GraphView Graph { get; } = graph;
        internal TabItem Tab { get; } = tab;
        internal GraphWindow? Window { get; set; }
    }
}
