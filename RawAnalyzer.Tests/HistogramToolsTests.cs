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

    [Theory]
    [InlineData(8)]  // 256ビン → 164列が1ビン、46列が2ビン
    [InlineData(10)] // 1024ビン → 184列が5ビン、26列が4ビン
    [InlineData(12)] // 4096ビン → 106列が20ビン、104列が19ビン
    public void AggregateForDisplay_FlatHistogram_IsFlatAcrossColumns(int bitDepth)
    {
        // 回帰テスト: 表示は列ごとの度数の合計をそのまま棒の高さにしていたので、ビン数が列数(210)の整数倍でない
        // 8/10/12bit では、すべてのコードが同数の平らな分布でも担当ビンの多い列が周期的に高く(低く)描かれ、
        // ミッシングコード・DNL のような偽の櫛に見えた。1ビンあたりの度数で描けば平らになる
        var bins = new long[1 << bitDepth];
        Array.Fill(bins, 1000L);

        double[] columns = HistogramTools.AggregateForDisplay(bins, 210, cumulative: false);

        Assert.Equal(210, columns.Length);
        double nominal = 1000.0 * bins.Length / 210;
        Assert.All(columns, c => Assert.Equal(nominal, c, 6));
    }

    [Fact]
    public void AggregateForDisplay_DivisibleBins_SameAsAggregate_AndCumulativeUnchanged()
    {
        // 割り切れるとき(全列の担当ビン数が同じ)は合計と同じ値(対数表示の形も変わらない)。
        // 累積は各列の最後のビンまでの累積で櫛にならないので、合計のまま
        long[] bins = MakeBins(64, (0, 3), (20, 5), (21, 1), (63, 2));
        long[] uneven = MakeBins(256, (0, 5), (100, 9), (255, 7));

        Assert.Equal(HistogramTools.Aggregate(bins, 8), HistogramTools.AggregateForDisplay(bins, 8, false));
        Assert.Equal(
            HistogramTools.Aggregate(uneven, 210, cumulative: true),
            HistogramTools.AggregateForDisplay(uneven, 210, cumulative: true));
    }

    [Fact]
    public void AggregateForDisplay_FewerBinsThanColumns_LeavesUnassignedColumnsEmpty()
    {
        var bins = new long[16];
        bins[0] = 1;
        bins[15] = 2;

        double[] columns = HistogramTools.AggregateForDisplay(bins, 210, cumulative: false);

        // 名目の列幅 16/210 を掛ける(担当ビンのない列は0)
        Assert.Equal(16.0 / 210, columns[0], 9);
        Assert.Equal(2 * 16.0 / 210, columns[196], 9);
        Assert.Equal(0, columns[1]);
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
    public void BuildTable_Sampled_PrependsNoteThatCountsAreSamples()
    {
        // 回帰テスト: 1000万画素を超えると間引いて集計するので、count は画素数ではなくサンプル数になる。
        // 画面は sampled バッジで示すが、CSV・コピーの表には何も書かれず、1200万画素で実際の約1/9 の値を
        // 画素数として読ませていた。間引いたときだけ先頭にサンプル数と対象の画素数を書く(表の列は変えない)
        long[] bins = MakeBins(2, (0, 3), (1, 1));

        string sampled = HistogramTools.BuildTable(bins, ',', populationCount: 36);
        string[] lines = sampled.TrimEnd().Split(Environment.NewLine);

        Assert.Equal("# sampled: count is the number of sampled pixels (4 of 36 pixels, about 1/9.0)", lines[0]);
        Assert.Equal("raw_code,count,cumulative", lines[1]);
        Assert.Equal("0,3,3", lines[2]);

        // 全画素を集計したとき(対象の画素数=サンプル数、または対象を渡さないとき)は従来どおり表だけ
        Assert.Equal(HistogramTools.BuildTable(bins, ','), HistogramTools.BuildTable(bins, ',', populationCount: 4));
        Assert.StartsWith("raw_code,", HistogramTools.BuildTable(bins, ','));
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
