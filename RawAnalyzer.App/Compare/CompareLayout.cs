namespace RawAnalyzer.App.Compare;

/// <summary>各画像がアスペクト比を保って収まる比較グリッドを選ぶ。</summary>
internal static class CompareLayout
{
    internal static (int Rows, int Columns) Choose(
        double areaWidth, double areaHeight, IReadOnlyList<double> imageAspects,
        double paneChromeWidth, double paneChromeHeight, (int Rows, int Columns) current)
    {
        int count = imageAspects.Count;
        if (count <= 1)
        {
            return (1, 1);
        }

        (int Rows, int Columns) best = count >= 3 ? (2, 2) : (1, count);
        double bestArea = Score(best);
        Consider((1, count));
        Consider((count, 1));
        return best;

        void Consider((int Rows, int Columns) layout)
        {
            double area = Score(layout);
            double tolerance = Math.Max(bestArea, area) * 1e-9;
            if (area > bestArea + tolerance || (Math.Abs(area - bestArea) <= tolerance && layout == current))
            {
                best = layout;
                bestArea = area;
            }
        }

        double Score((int Rows, int Columns) layout) =>
            MinimumImageArea(imageAspects, areaWidth / layout.Columns - paneChromeWidth,
                areaHeight / layout.Rows - paneChromeHeight);
    }

    private static double MinimumImageArea(IReadOnlyList<double> aspects, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        double minimum = double.PositiveInfinity;
        foreach (double aspect in aspects)
        {
            double fittedWidth = Math.Min(width, height * aspect);
            minimum = Math.Min(minimum, fittedWidth * (fittedWidth / aspect));
        }

        return minimum;
    }
}
