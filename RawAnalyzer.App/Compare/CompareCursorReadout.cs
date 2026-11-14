using System.Globalization;

namespace RawAnalyzer.App.Compare;

internal static class CompareCursorReadout
{
    // MapCursor は画素中心を 0…size-1 に取る。その範囲を外れた位置は対応画素なしとする。
    internal static (int X, int Y)? CorrespondingPixel(double x, double y, int width, int height) =>
        x >= 0 && x <= width - 1 && y >= 0 && y <= height - 1
            ? ((int)Math.Floor(x + 0.5), (int)Math.Floor(y + 0.5))
            : null;

    internal static string Format(int x, int y, IReadOnlyList<(string Label, int? Code)> pixels)
    {
        var parts = new List<string>(pixels.Count + 3)
        {
            FormattableString.Invariant($"x {x}"),
            FormattableString.Invariant($"y {y}"),
        };
        foreach (var (label, code) in pixels)
        {
            parts.Add($"{label} {code?.ToString(CultureInfo.InvariantCulture) ?? "—"}");
        }

        if (pixels.Count == 2 && pixels[0].Code is int first && pixels[1].Code is int second)
        {
            string delta = (second - first).ToString("+0;−0;0", CultureInfo.InvariantCulture);
            parts.Add($"Δ {delta}");
        }

        return string.Join(" · ", parts);
    }
}
