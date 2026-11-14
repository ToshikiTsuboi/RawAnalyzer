using System.Globalization;

namespace RawAnalyzer.App.Compare;

internal static class CompareCursorReadout
{
    // 画素中心は整数座標で、各画素は中心の前後 0.5 までを占める。
    internal static (int X, int Y)? CorrespondingPixel(double x, double y, int width, int height) =>
        x >= -0.5 && x < width - 0.5 && y >= -0.5 && y < height - 0.5
            ? (Math.Clamp((int)Math.Floor(x + 0.5), 0, width - 1),
                Math.Clamp((int)Math.Floor(y + 0.5), 0, height - 1))
            : null;

    internal static string Format(int x, int y, IReadOnlyList<(string Label, int? Code, int BitDepth)> pixels)
    {
        var parts = new List<string>(pixels.Count + 3)
        {
            FormattableString.Invariant($"x {x}"),
            FormattableString.Invariant($"y {y}"),
        };
        foreach (var (label, code, _) in pixels)
        {
            parts.Add($"{label} {code?.ToString(CultureInfo.InvariantCulture) ?? "—"}");
        }

        if (pixels.Count == 2 && pixels[0].BitDepth == pixels[1].BitDepth
            && pixels[0].Code is int first && pixels[1].Code is int second)
        {
            string delta = (second - first).ToString("+0;−0;0", CultureInfo.InvariantCulture);
            parts.Add($"Δ {delta}");
        }

        return string.Join(" · ", parts);
    }
}
