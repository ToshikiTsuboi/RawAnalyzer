using RawAnalyzer.App.Controls;

namespace RawAnalyzer.App.Services;

internal static class ViewportModeHint
{
    internal static string? For(ViewportInteractionMode mode) => mode switch
    {
        ViewportInteractionMode.RoiSelect => "ドラッグで範囲を選択 · Esc で終了",
        ViewportInteractionMode.LineProfile => "クリックで行を選択 · Esc で終了",
        ViewportInteractionMode.WhiteBalancePick => "無彩色の点をクリック · Esc で終了",
        _ => null,
    };
}
