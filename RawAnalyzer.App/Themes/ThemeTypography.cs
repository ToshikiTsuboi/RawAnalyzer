namespace RawAnalyzer.App.Themes;

/// <summary>XAML と描画コードが共有する DIP 単位の文字サイズ。</summary>
internal static class ThemeTypography
{
    public const double FontSizeCaption = 11;
    public const double FontSizeBody = 12;
    public const double FontSizeEmphasis = 13;
    public const double FontSizeTitle = 16;

    // 画素セル内の数値は画面の文字と別に、セルに収まる大きさを使う。
    internal const double RawValueMinSize = 9;
    internal const double RawValueMaxSize = 15;
}
