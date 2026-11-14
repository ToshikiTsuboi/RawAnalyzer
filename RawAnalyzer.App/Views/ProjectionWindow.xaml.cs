using RawAnalyzer.App.Services;

namespace RawAnalyzer.App.Views;

/// <summary>水平射影または垂直射影を表示する独立ウィンドウ。</summary>
public sealed class ProjectionWindow : GraphWindow
{
    internal ProjectionWindow(ProjectionDirection direction) : base(new ProjectionGraphView(direction), 500, 380)
    {
    }

    internal ProjectionGraphView View => (ProjectionGraphView)Content;
}
