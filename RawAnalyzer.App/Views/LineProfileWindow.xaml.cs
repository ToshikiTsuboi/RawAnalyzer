namespace RawAnalyzer.App.Views;

/// <summary>ラインプロファイルを表示する独立ウィンドウ。</summary>
public sealed class LineProfileWindow : GraphWindow
{
    /// <summary>ラインプロファイル用のウィンドウを生成する。</summary>
    public LineProfileWindow() : this(new LineProfileView())
    {
    }

    internal LineProfileWindow(LineProfileView view) : base(view, 480, 360)
    {
    }

    internal LineProfileView View => (LineProfileView)Graph;
}
