using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class HistogramToolsTests
{
    private static long[] MakeBins(int size, params (int Code, long Count)[] entries)
    {
        var bins = new long[size];
        foreach ((int code, long count) in entries)
        {
            bins[code] = count;
        }

        return bins;
    }

    [Fact]
    public void ComputeAutoLevels_EmptyHistogram_ReturnsNull()
    {
        Assert.Null(HistogramTools.ComputeAutoLevels(new long[256]));
    }

    [Fact]
    public void ComputeAutoLevels_SingleValue_ReturnsNull()
    {
        // 全画素が同じ値だと黒点<白点にできない
        long[] bins = MakeBins(256, (128, 1000));
        Assert.Null(HistogramTools.ComputeAutoLevels(bins));
    }

    [Fact]
    public void ComputeAutoLevels_ClipsTails()
    {
        // 両端に1%ずつ外れ値、本体は100〜200
        var bins = new long[256];
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
        long[] bins = MakeBins(256, (10, 5), (240, 5));

        AutoLevels? levels = HistogramTools.ComputeAutoLevels(bins, clipRatio: 0);

        Assert.Equal(new AutoLevels(10, 240), levels);
    }

    [Fact]
    public void ComputeAutoLevels_CountsBeyondUintRange_DoNotWrap()
    {
        // ギガピクセルのフラット画像では単一ビンが uint の範囲(約4.29e9)を超える。
        // ビンがuintのままだと巻き戻って黒/白点が別の位置に飛ぶ(全体レビュー 2026-08-23 の回帰)
        long[] bins = MakeBins(256, (100, 5_000_000_000L), (200, 5_000_000_000L));

        AutoLevels? levels = HistogramTools.ComputeAutoLevels(bins, clipRatio: 0.01);

        Assert.NotNull(levels);
        Assert.Equal(100, levels!.Value.BlackCode);
        Assert.Equal(200, levels.Value.WhiteCode);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(0.5)]
    public void ComputeAutoLevels_InvalidClipRatio_Throws(double ratio)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HistogramTools.ComputeAutoLevels(new long[16], ratio));
    }

    [Fact]
    public void Aggregate_SumsIntoColumns()
    {
        var bins = new long[8];
        for (int i = 0; i < bins.Length; i++)
        {
            bins[i] = 1;
        }

        double[] columns = HistogramTools.Aggregate(bins, 4);

        Assert.Equal(4, columns.Length);
        Assert.Equal(8, columns.Sum());

        // 均等配分され、最終列が空にならないこと(旧実装は3/3/2/0だった)
        Assert.All(columns, c => Assert.Equal(2, c));
    }

    [Fact]
    public void Aggregate_LastBinMapsToLastColumn()
    {
        // 飽和スパイク(最終ビン)は必ず右端の列に描画されること。
        // 旧実装は 256ビン/210列 で列127へ写像され、ヒストグラムが左に圧縮されていた
        var bins = new long[256];
        bins[0] = 5;
        bins[^1] = 7;

        double[] columns = HistogramTools.Aggregate(bins, 210);

        Assert.Equal(5, columns[0]);
        Assert.Equal(7, columns[^1]);
        Assert.Equal(12, columns.Sum());
    }

    [Fact]
    public void Aggregate_FewerBinsThanColumns_PreservesEndpoints()
    {
        // ビン数 < 列数 でも先頭・末尾の位置関係が保たれること
        var bins = new long[16];
        bins[0] = 1;
        bins[15] = 2;

        double[] columns = HistogramTools.Aggregate(bins, 210);

        Assert.Equal(1, columns[0]);
        Assert.Equal(2, columns[196]); // 15*210/16 = 196
        Assert.Equal(3, columns.Sum());
    }

    [Fact]
    public void Aggregate_Cumulative_IsMonotonicAndEndsAtTotal()
    {
        long[] bins = MakeBins(64, (0, 3), (20, 5), (63, 2));

        double[] columns = HistogramTools.Aggregate(bins, 8, cumulative: true);

        for (int i = 1; i < columns.Length; i++)
        {
            Assert.True(columns[i] >= columns[i - 1], "累積は単調非減少であること");
        }

        Assert.Equal(10, columns[^1]);
    }

    [Fact]
    public void Aggregate_KeepsCountsBeyondUintRange()
    {
        // 単一ビンが uint の範囲を超えても列の値が巻き戻らないこと(全体レビュー 2026-08-23 の回帰)
        long[] bins = MakeBins(4, (0, 6_000_000_000L), (3, 3_000_000_000L));

        double[] columns = HistogramTools.Aggregate(bins, 4);

        Assert.Equal(4, columns.Length);
        Assert.Equal(6_000_000_000d, columns[0], 0);
        Assert.Equal(3_000_000_000d, columns[3], 0);
    }

    [Fact]
    public void Aggregate_InvalidColumns_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HistogramTools.Aggregate(new long[16], 0));
    }

    [Fact]
    public void BuildTable_WithoutChannels_HasCumulativeColumn()
    {
        long[] bins = MakeBins(4, (0, 2), (2, 3));

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
        long[] bins = MakeBins(2, (0, 4));
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
        long[] bins = MakeBins(4, (0, 1));
        var channels = new List<ChannelHistogram>
        {
            MakeChannel(BayerChannel.R, new long[2]),
            MakeChannel(BayerChannel.Gr, new long[4]),
            MakeChannel(BayerChannel.Gb, new long[4]),
            MakeChannel(BayerChannel.B, new long[4]),
        };

        Assert.Throws<ArgumentException>(() => HistogramTools.BuildTable(bins, ',', channels));
    }

    private static ChannelHistogram MakeChannel(BayerChannel channel, long[] bins) => new()
    {
        Channel = channel,
        Bins = bins,
        Statistics = new RegionStatistics(0, 0, 0, 0, 0),
    };
}
