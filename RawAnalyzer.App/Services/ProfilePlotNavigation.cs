using System.Windows;

namespace RawAnalyzer.App.Services;

/// <summary>プロファイルの座標範囲・ホイール拡大・パン。画像データは変更しない。</summary>
internal static class ProfilePlotNavigation
{
    internal static ProfileAxisRange FullHorizontal(int count) => count > 1
        ? new ProfileAxisRange(0, count - 1) : new ProfileAxisRange(-0.5, 0.5);

    internal static ProfileAxisRange Zoom(
        ProfileAxisRange current, double anchorFraction, double factor,
        double minimumSpan, double maximumSpan, ProfileAxisRange? bounds = null)
    {
        if (!double.IsFinite(anchorFraction) || !double.IsFinite(factor) || factor <= 0) return current;
        double span = current.Maximum - current.Minimum;
        double size = Math.Clamp(span * factor, minimumSpan, maximumSpan);
        double fraction = Math.Clamp(anchorFraction, 0, 1);
        double anchor = current.Minimum + span * fraction;
        return Constrain(current, anchor - size * fraction, size, bounds);
    }

    internal static ProfileAxisRange Pan(ProfileAxisRange current, double fraction, ProfileAxisRange? bounds = null)
    {
        double span = current.Maximum - current.Minimum;
        return Constrain(current, current.Minimum + span * fraction, span, bounds);
    }

    private static ProfileAxisRange Constrain(
        ProfileAxisRange previous, double minimum, double span, ProfileAxisRange? bounds)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(span) || span <= 0) return previous;
        if (bounds is { } limit)
        {
            if (span >= limit.Maximum - limit.Minimum) return limit;
            minimum = Math.Clamp(minimum, limit.Minimum, limit.Maximum - span);
        }

        double maximum = minimum + span;
        // 極小範囲での丸めによる0幅や、非常に大きい手動値での桁あふれを防ぐ。
        return double.IsFinite(maximum) && maximum > minimum
            ? new ProfileAxisRange(minimum, maximum) : previous;
    }

    internal static double ToCanvasX(double index, ProfileAxisRange horizontal, double width) =>
        (index - horizontal.Minimum) / (horizontal.Maximum - horizontal.Minimum) * width;

    /// <summary>表示中の区間だけを読む。間引く場合も列ごとの極値とその出現順を保つ。</summary>
    internal static IReadOnlyList<Point> SampleVisible(double[] data, ProfileAxisRange horizontal, double width)
    {
        var points = new List<Point>();
        if (data.Length == 0 || width < 1) return points;
        // int へ変換する前に範囲へ収める(手動の横軸に 2^31 以上を入れると、変換の桁あふれで線が消えていた)
        int start = (int)Math.Clamp(Math.Floor(horizontal.Minimum), 0, data.Length - 1);
        int end = (int)Math.Clamp(Math.Ceiling(horizontal.Maximum), start, data.Length - 1);
        int count = end - start + 1;
        int columns = Math.Max(1, (int)width);
        void Add(int index)
        {
            var point = new Point(ToCanvasX(index, horizontal, width), data[index]);
            if (points.Count == 0 || points[^1] != point) points.Add(point);
        }

        if (count <= columns)
        {
            for (int i = start; i <= end; i++) Add(i);
        }
        else
        {
            Add(start); // 表示境界の線形補間に必要な隣接点を残す
            for (int column = 0; column < columns; column++)
            {
                int first = start + (int)((long)column * count / columns);
                int last = start + (int)((long)(column + 1) * count / columns);
                int min = first, max = first;
                for (int i = first + 1; i < last; i++)
                {
                    if (data[i] < data[min]) min = i;
                    if (data[i] > data[max]) max = i;
                }

                Add(Math.Min(min, max));
                Add(Math.Max(min, max));
            }

            Add(end);
        }

        return points;
    }
}
