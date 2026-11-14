using System.Windows;

namespace RawAnalyzer.App.Services;

internal static class SplitLabelPlacement
{
    private const double Inset = 4;

    /// <summary>区画の可視矩形から、左上に置くラベルの位置を求める。</summary>
    internal static Point? Find(Rect visible, Size label)
    {
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0)
        {
            return null;
        }

        return new Point(visible.Left + Math.Clamp(visible.Width - label.Width, 0, Inset),
            visible.Top + Math.Clamp(visible.Height - label.Height, 0, Inset));
    }
}
