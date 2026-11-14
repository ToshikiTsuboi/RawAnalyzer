namespace RawAnalyzer.App.Compare;

/// <summary>ステータスバーに表示するアクティブペインの情報。</summary>
internal readonly record struct ComparePaneStatus(
    string Label, string FileName, int Width, int Height, int BitDepth, double DeviceZoom);
