using System.Windows.Controls;
using RawAnalyzer.App;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Themes;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class HistogramPlotTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Create_UsesCommonScaleAndPreservesBins(bool channels, bool log, bool cumulative)
    {
        long[] bins = [0, 1, 5_000_000_000, 9_000_000_000];
        long[][] source = [bins, [1, 2, 3, 4], [1, 0, 0, 0], [0, 0, 0, 0]];
        ChannelHistogram[] inputs = source.Select((values, i) => new ChannelHistogram
        {
            Bins = values, Channel = (BayerChannel)i, Statistics = new RegionStatistics(0, 0, 0, 0, 0),
        }).ToArray();
        long[][] originals = source.Select(values => values.ToArray()).ToArray();
        HistogramPlot plot = await Task.Run(() =>
            HistogramPlot.Create(bins, channels ? inputs : null, 4, log, cumulative, CancellationToken.None));
        double Scale(double value) => log ? Math.Log(1 + value) : value;
        double max = cumulative ? bins.Sum() : bins.Max();
        Assert.Equal(channels ? 4 : 1, plot.Series.Length);
        Assert.Equal(channels ? ThemeColors.ChannelR : cumulative ? ThemeColors.Accent : ThemeColors.Histogram,
            plot.Colors[0]);
        for (int s = 0; s < plot.Series.Length; s++)
        {
            long total = 0;
            for (int i = 0; i < 4; i++)
            {
                total += source[s][i];
                Assert.Equal(Scale(cumulative ? total : source[s][i]) / Scale(max), plot.Series[s][i], 12);
            }
            Assert.Equal(originals[s], source[s]);
        }
    }

    [Fact]
    public void Create_EmptyHistogramHasZeroColumns()
    {
        HistogramPlot plot = HistogramPlot.Create(new long[256], null, 219, true, true, CancellationToken.None);
        Assert.Equal(219, plot.Series[0].Length);
        Assert.All(plot.Series[0], value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1024, 1024)]
    [InlineData(4095, 4095)]
    [InlineData(-5, 0)]
    [InlineData(4200, 4095)]
    public void CodePosition_RoundTripsAndClamps(double code, double expected)
    {
        double x = HistogramPlot.XForCode(code, 4095, 173);
        Assert.Equal(expected, HistogramPlot.CodeForX(x, 4095, 173));
    }

    [Fact]
    public Task DraggingHandles_UpdatesWindowLevels() => WpfTestHost.Run(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        var window = new MainWindow(directory) { ShowActivated = false };
        try
        {
            window.Show();
            var vm = (MainViewModel)window.DataContext;
            vm.HasImage = true;
            vm.BlackLevelMax = 255;
            vm.WhiteLevel = 255;
            var chart = (HistogramView)window.FindName("HistogramChart");
            chart.SetPlot(HistogramPlot.Create(Enumerable.Range(0, 256).Select(i => (long)i).ToArray(),
                null, chart.ColumnCount, true, false, CancellationToken.None));
            window.UpdateLayout();
            double width = chart.ActualWidth - 8;
            Assert.True(chart.BeginLevelDrag(4));
            chart.MoveLevelDrag(4 + width / 4);
            chart.EndLevelDrag();
            Assert.True(chart.BeginLevelDrag(4 + width));
            chart.MoveLevelDrag(4 + 3 * width / 4);
            chart.EndLevelDrag();
            Assert.Equal(64, vm.BlackLevel);
            Assert.Equal(191, vm.WhiteLevel);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            WpfTestHost.CaptureIfRequested((System.Windows.FrameworkElement)window.Content, "histogram-handles");
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    });
}
