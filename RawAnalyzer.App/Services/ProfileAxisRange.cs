using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>ラインプロファイルの表示専用の縦軸。入力データや統計値は変更しない。</summary>
internal readonly record struct ProfileAxisRange(double Minimum, double Maximum)
{
    internal static ProfileAxisRange Full(int maxCode) => new(0, Math.Max(1, maxCode));

    internal static ProfileAxisRange Auto(ProfileStatistics stats, int maxCode) =>
        stats.Count == 0 ? Full(maxCode) : Auto(stats.Min, stats.Max, maxCode);

    /// <summary>データの最小〜最大に合わせた縦軸(射影の最大・最小の線も入れるときに使う)。</summary>
    internal static ProfileAxisRange Auto(double minimum, double maximum, int maxCode)
    {
        ProfileAxisRange full = Full(maxCode);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum)) return full;
        double low = Math.Clamp(minimum, full.Minimum, full.Maximum);
        double high = Math.Clamp(maximum, low, full.Maximum);
        // 平坦なラインにも幅を確保する。通常はデータ幅の5%を上下の余白にする。
        double padding = high > low ? (high - low) * 0.05 : 0.5;
        return new ProfileAxisRange(Math.Max(0, low - padding), Math.Min(full.Maximum, high + padding));
    }

    internal static bool TryParse(string minimum, string maximum, CultureInfo culture, out ProfileAxisRange range)
    {
        range = default;
        if (!TryParseLimit(minimum, culture, out double min) || !TryParseLimit(maximum, culture, out double max)
            || !(min < max) || !double.IsFinite(max - min)) return false;
        range = new ProfileAxisRange(min, max);
        return true;
    }

    // 表示の言語の書き方(de-DE の "1,5" など)で読めなければ、他の数値入力欄と同じく NumericInput で読む
    // (IME がオンのまま打った全角の数字・記号と3桁区切りも受け付ける)。入力欄ごとの不正の表示にも使う
    internal static bool TryParseLimit(string text, CultureInfo culture, out double number) =>
        (double.TryParse(text, NumberStyles.Float, culture, out number) && double.IsFinite(number))
        || NumericInput.TryParseFinite(text, out number);

    internal bool Contains(double value) => double.IsFinite(value) && value >= Minimum && value <= Maximum;

    // 範囲内の値だけを変換する。非常に狭い手動範囲でも巨大なWPF座標を生成しない。
    internal double ToCanvasY(double value, double height) =>
        height - 1 - Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1) * (height - 2);

    internal bool TryClipSegment(Point first, Point second, out Point start, out Point end)
    {
        start = first;
        end = second;
        if (!double.IsFinite(first.X) || !double.IsFinite(second.X)
            || !double.IsFinite(first.Y) || !double.IsFinite(second.Y)
            || !double.IsFinite(second.Y - first.Y)
            || (first.Y < Minimum && second.Y < Minimum)
            || (first.Y > Maximum && second.Y > Maximum)) return false;

        // 元の値域で線分をクリップする。範囲外の点を単に上下端へ押し込むと
        // 本来見えない区間に偽の水平線を作るため、交点だけを求める。
        if (first.Y < Minimum) start = Intersect(first, second, Minimum);
        else if (first.Y > Maximum) start = Intersect(first, second, Maximum);
        if (second.Y < Minimum) end = Intersect(first, second, Minimum);
        else if (second.Y > Maximum) end = Intersect(first, second, Maximum);
        return true;
    }

    private static Point Intersect(Point first, Point second, double value)
    {
        double fraction = (value - first.Y) / (second.Y - first.Y);
        return new Point(first.X + (second.X - first.X) * fraction, value);
    }

    internal StreamGeometry BuildGeometry(IReadOnlyList<Point> samples, double height)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            Point? previousEnd = null;
            for (int i = 1; i < samples.Count; i++)
            {
                if (!TryClipSegment(samples[i - 1], samples[i], out Point start, out Point end))
                {
                    previousEnd = null;
                    continue;
                }

                var a = new Point(start.X, ToCanvasY(start.Y, height));
                var b = new Point(end.X, ToCanvasY(end.Y, height));
                if (previousEnd != a) context.BeginFigure(a, isFilled: false, isClosed: false);
                context.LineTo(b, isStroked: true, isSmoothJoin: false);
                previousEnd = b;
            }
        }

        geometry.Freeze();
        return geometry;
    }
}
