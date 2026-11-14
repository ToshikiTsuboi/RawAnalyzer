using System.Windows;

namespace RawAnalyzer.App.Services;

internal static class NavigatorGeometry
{
    internal static Rect ViewFrame(Size image, Size thumbnail, Rect visible)
    {
        Rect clipped = Rect.Intersect(new Rect(image), visible);
        return clipped.IsEmpty ? Rect.Empty : new Rect(
            clipped.X * thumbnail.Width / image.Width, clipped.Y * thumbnail.Height / image.Height,
            clipped.Width * thumbnail.Width / image.Width, clipped.Height * thumbnail.Height / image.Height);
    }

    internal static Point ImagePoint(Size image, Size thumbnail, Point point) => new(
        Math.Clamp(point.X / thumbnail.Width, 0, 1) * image.Width,
        Math.Clamp(point.Y / thumbnail.Height, 0, 1) * image.Height);

    internal static bool Fits(Size image, Rect visible) =>
        visible.X <= 0.0001 && visible.Y <= 0.0001
        && visible.Right >= image.Width - 0.0001 && visible.Bottom >= image.Height - 0.0001;
}
