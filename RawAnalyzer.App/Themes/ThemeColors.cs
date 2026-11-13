using System.Windows.Media;

namespace RawAnalyzer.App.Themes;

/// <summary>XAML と描画コードが共有するダークテーマの配色。</summary>
internal static class ThemeColors
{
    public const byte CanvasGray = 0x16;

    public static Color WindowBackground { get; } = Color.FromRgb(0x25, 0x25, 0x28);
    public static Color Panel { get; } = Color.FromRgb(0x2E, 0x2E, 0x32);
    public static Color Panel2 { get; } = Color.FromRgb(0x38, 0x38, 0x3D);
    public static Color Hover { get; } = Color.FromRgb(0x45, 0x45, 0x4B);
    public static Color Pressed { get; } = Color.FromRgb(0x2A, 0x2A, 0x2E);
    public static Color BorderDark { get; } = Color.FromRgb(0x48, 0x48, 0x4E);
    public static Color BorderLight { get; } = Color.FromRgb(0x5A, 0x5A, 0x61);
    public static Color Text { get; } = Color.FromRgb(0xD8, 0xD8, 0xD4);
    public static Color TextDim { get; } = Color.FromRgb(0x9A, 0x9A, 0x95);
    public static Color TextFaint { get; } = Color.FromRgb(0x95, 0x95, 0x90);
    public static Color Accent { get; } = Color.FromRgb(0x5B, 0x9D, 0xD9);
    public static Color AccentSoft { get; } = WithAlpha(Accent, 0x33);
    public static Color Canvas { get; } = Color.FromRgb(CanvasGray, CanvasGray, 0x18);
    public static Color ScrollThumb { get; } = Color.FromRgb(0x4A, 0x4A, 0x50);
    public static Color ScrollThumbHover { get; } = Color.FromRgb(0x5F, 0x5F, 0x66);
    public static Color Error { get; } = Color.FromRgb(0xD9, 0x60, 0x5B);
    public static Color Success { get; } = Color.FromRgb(0x7E, 0xCB, 0x72);
    public static Color Warning { get; } = Color.FromRgb(0xD9, 0x9B, 0x5B);
    public static Color OverlayScrim { get; } = WithAlpha(Colors.Black, 0x99);
    public static Color OverlayPanel { get; } = WithAlpha(Panel, 0xE6);
    public static Color SplitLabelBackground { get; } = WithAlpha(Canvas, 0xD9);
    public static Color ChannelR { get; } = Color.FromRgb(0xE0, 0x52, 0x4E);
    public static Color ChannelGr => Success;
    public static Color ChannelGb { get; } = Color.FromRgb(0x4E, 0x9A, 0x4E);
    public static Color ChannelB => Accent;
    public static Color PlotMax { get; } = Color.FromRgb(0xE0, 0x88, 0x4E);
    public static Color PlotMin { get; } = Color.FromRgb(0x9F, 0x86, 0xE0);
    public static Color PlotTick => Colors.Gray;
    public static Color PlotLabel => Colors.DarkGray;
    public static Color Histogram { get; } = Color.FromRgb(0x8A, 0x8A, 0x85);
    public static Color HotPixel { get; } = Color.FromRgb(0xE6, 0x50, 0x3C);
    public static Color DeadPixel { get; } = Color.FromRgb(0x3C, 0x78, 0xE6);
    public static Color PrimaryHover { get; } = Color.FromRgb(0x79, 0xB4, 0xE6);
    public static Color PrimaryPressed { get; } = Color.FromRgb(0x50, 0x8F, 0xC8);

    internal static Color KeyCursor { get; } = WithAlpha(Success, 0xE6);
    internal static Color KeyCursorGuide { get; } = WithAlpha(Success, 0x50);
    internal static Color GhostCursor { get; } = WithAlpha(Warning, 0xC8);
    internal static Color ProfileActive { get; } = WithAlpha(Warning, 0xE6);
    internal static Color ProfileInactive { get; } = WithAlpha(Warning, 0x60);
    internal static Color RoiFill { get; } = WithAlpha(Accent, 0x20);
    internal static Color CompareDifferenceFill { get; } = WithAlpha(Warning, 0x28);
    internal static Color MajorGrid { get; } = WithAlpha(TextDim, 0x28);
    internal static Color MinorGrid { get; } = WithAlpha(TextDim, 0x12);
    internal static Color MeanGuide { get; } = WithAlpha(Success, 0x70);
    internal static Color SpreadGuide { get; } = WithAlpha(TextDim, 0x40);
    internal static Color PlotMarker { get; } = WithAlpha(Warning, 0x80);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    internal static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
