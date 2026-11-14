using System.Windows.Media;
using RawAnalyzer.App.Themes;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Rendering;

internal sealed record HistogramPlot(double[][] Series, Color[] Colors, bool ByChannel)
{
    private static readonly Color[] ChannelColors =
        [ThemeColors.ChannelR, ThemeColors.ChannelGr, ThemeColors.ChannelGb, ThemeColors.ChannelB];

    internal static HistogramPlot Create(long[] bins, IReadOnlyList<ChannelHistogram>? channels,
        int columns, bool logScale, bool cumulative, CancellationToken cancellationToken)
    {
        bool byChannel = channels is { Count: 4 };
        long[][] input = byChannel ? channels!.Select(channel => channel.Bins).ToArray() : [bins];
        var series = new double[input.Length][];
        double max = 0;
        for (int i = 0; i < input.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            series[i] = HistogramTools.AggregateForDisplay(input[i], columns, cumulative);
            max = Math.Max(max, series[i].Max());
        }

        if (max > 0)
        {
            double scale = logScale ? Math.Log(1 + max) : max;
            foreach (double[] curve in series)
            {
                for (int i = 0; i < curve.Length; i++)
                {
                    curve[i] = (logScale ? Math.Log(1 + curve[i]) : curve[i]) / scale;
                }
            }
        }

        return new HistogramPlot(series,
            byChannel ? ChannelColors : [cumulative ? ThemeColors.Accent : ThemeColors.Histogram], byChannel);
    }

    internal static double XForCode(double code, double max, double width) =>
        max > 0 ? Math.Clamp(code / max, 0, 1) * width : 0;

    internal static double CodeForX(double x, double max, double width) =>
        width > 0 ? Math.Round(Math.Clamp(x / width, 0, 1) * max) : 0;
}
