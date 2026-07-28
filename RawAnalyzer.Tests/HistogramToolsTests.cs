using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class HistogramToolsTests
{
    private static uint[] MakeBins(int size, params (int Code, uint Count)[] entries)
    {
        var bins = new uint[size];
        foreach ((int code, uint count) in entries)
        {
            bins[code] = count;
        }

        return bins;
    }

    [Fact]
    public void ComputeAutoLevels_EmptyHistogram_ReturnsNull()
    {
        Assert.Null(HistogramTools.ComputeAutoLevels(new uint[256]));
    }

    [Fact]
    public void ComputeAutoLevels_SingleValue_ReturnsNull()
    {
        // 全画素が同じ値だと黒点<白点にできない
        uint[] bins = MakeBins(256, (128, 1000));
        Assert.Null(HistogramTools.ComputeAutoLevels(bins));
    }

    [Fact]
    public void ComputeAutoLevels_ClipsTails()
    {
        // 両端に1%ずつ外れ値、本体は100〜200
        var bins = new uint[256];
        bins[0] = 100;
        bins[255] = 100;
        for (int i = 100; i <= 200; i++)
        {
            bins[i] = 100;
        }

        // 総数 10300、clip = 206。外れ値100と本体の先頭ビン100を食って101で超える
        AutoLevels? levels = HistogramTools.ComputeAutoLevels(bins, clipRatio: 0.02);

        Assert.NotNull(levels);
        Assert.Equal(101, levels!.Value.BlackCode);
        Assert.Equal(199, levels.Value.WhiteCode);
    }

    [Fact]
    public void ComputeAutoLevels_ZeroClip_UsesFullRange()
    {
        uint[] bins = MakeBins(256, (10, 5), (240, 5));

        AutoLevels? levels = HistogramTools.ComputeAutoLevels(bins, clipRatio: 0);

        Assert.Equal(new AutoLevels(10, 240), levels);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void ComputeAutoLevels_InvalidClipRatio_Throws(double ratio)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HistogramTools.ComputeAutoLevels(new uint[16], ratio));
    }

    [Fact]
    public void Aggregate_SumsIntoColumns()
    {
        var bins = new uint[8];
        for (int i = 0; i < bins.Length; i++)
        {
            bins[i] = 1;
        }

        double[] columns = HistogramTools.Aggregate(bins, 4);

        Assert.Equal(4, columns.Length);
        Assert.Equal(8, columns.Sum());
    }

    [Fact]
    public void Aggregate_Cumulative_IsMonotonicAndEndsAtTotal()
    {
        uint[] bins = MakeBins(64, (0, 3), (20, 5), (63, 2));

        double[] columns = HistogramTools.Aggregate(bins, 8, cumulative: true);

        for (int i = 1; i < columns.Length; i++)
        {
            Assert.True(columns[i] >= columns[i - 1], "累積は単調非減少であること");
        }

        Assert.Equal(10, columns[^1]);
    }

    [Fact]
    public void Aggregate_InvalidColumns_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HistogramTools.Aggregate(new uint[16], 0));
    }

    [Fact]
    public void BuildTable_WithoutChannels_HasCumulativeColumn()
    {
        uint[] bins = MakeBins(4, (0, 2), (2, 3));

        string table = HistogramTools.BuildTable(bins, '\t');
        string[] lines = table.TrimEnd().Split(Environment.NewLine);

        Assert.Equal("raw_code\tcount\tcumulative", lines[0]);
        Assert.Equal("0\t2\t2", lines[1]);
        Assert.Equal("1\t0\t2", lines[2]);
        Assert.Equal("2\t3\t5", lines[3]);
        Assert.Equal("3\t0\t5", lines[4]);
    }

    [Fact]
    public void BuildTable_WithChannels_AppendsFourColumns()
    {
        uint[] bins = MakeBins(2, (0, 4));
        var channels = new List<ChannelHistogram>
        {
            MakeChannel(BayerChannel.R, MakeBins(2, (0, 1))),
            MakeChannel(BayerChannel.Gr, MakeBins(2, (0, 1))),
            MakeChannel(BayerChannel.Gb, MakeBins(2, (0, 1))),
            MakeChannel(BayerChannel.B, MakeBins(2, (0, 1))),
        };

        string table = HistogramTools.BuildTable(bins, ',', channels);
        string[] lines = table.TrimEnd().Split(Environment.NewLine);

        Assert.Equal("raw_code,count,cumulative,count_R,count_Gr,count_Gb,count_B", lines[0]);
        Assert.Equal("0,4,4,1,1,1,1", lines[1]);
    }

    [Fact]
    public void BuildTable_ChannelBinLengthMismatch_Throws()
    {
        uint[] bins = MakeBins(4, (0, 1));
        var channels = new List<ChannelHistogram>
        {
            MakeChannel(BayerChannel.R, new uint[2]),
            MakeChannel(BayerChannel.Gr, new uint[4]),
            MakeChannel(BayerChannel.Gb, new uint[4]),
            MakeChannel(BayerChannel.B, new uint[4]),
        };

        Assert.Throws<ArgumentException>(() => HistogramTools.BuildTable(bins, ',', channels));
    }

    private static ChannelHistogram MakeChannel(BayerChannel channel, uint[] bins) => new()
    {
        Channel = channel,
        Bins = bins,
        Statistics = new RegionStatistics(0, 0, 0, 0, 0),
    };
}
