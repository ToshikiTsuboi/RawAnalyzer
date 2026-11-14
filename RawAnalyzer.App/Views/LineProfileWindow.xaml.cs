namespace RawAnalyzer.App.Views;

/// <summary>ラインプロファイルを表示する独立ウィンドウ。</summary>
public sealed class LineProfileWindow : GraphWindow
{
    /// <summary>ラインプロファイル用のウィンドウを生成する。</summary>
    public LineProfileWindow() : base(new LineProfileView(), 480, 360)
    {
    }

    internal LineProfileView View => (LineProfileView)Content;
}
