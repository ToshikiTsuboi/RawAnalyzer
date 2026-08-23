using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 全体レビュー(2026-08-23)で見つかった不具合の回帰テスト。
/// </summary>
public class ReviewFixTests
{
    [Theory]
    [InlineData(@"\\nas\share\dark.raw", true)]
    [InlineData(@"\\192.168.0.10\raw\a.bin", true)]
    [InlineData(@"C:\Temp\dark.raw", false)]
    public void IsNetworkPath_DetectsUncPaths(string path, bool expected)
    {
        // ネットワーク上のファイルをMMFで開くと、転送中の切断が
        // EXCEPTION_IN_PAGE_ERROR となり.NETでは捕捉できずプロセスごと落ちる。
        // 判定できたものはヒープ展開へ振り分ける
        Assert.Equal(expected, RawLoader.IsNetworkPath(path));
    }

    [Fact]
    public void IsNetworkPath_InvalidPath_TreatedAsLocal()
    {
        // 判定できない場合は従来動作(ローカル扱い)へ倒す
        Assert.False(RawLoader.IsNetworkPath("|<>invalid"));
    }

    [Fact]
    public void ComputeAutoLevels_CountsBeyondUintRange_DoNotWrap()
    {
        // ギガピクセルのフラット画像では単一ビンが uint の範囲(約4.29e9)を超える。
        // ビンがuintのままだと巻き戻って黒/白点が別の位置に飛ぶ
        var bins = new long[256];
        bins[100] = 5_000_000_000L;
        bins[200] = 5_000_000_000L;

        AutoLevels? levels = HistogramTools.ComputeAutoLevels(bins, clipRatio: 0.01);

        Assert.NotNull(levels);
        Assert.Equal(100, levels!.Value.BlackCode);
        Assert.Equal(200, levels.Value.WhiteCode);
    }

    [Fact]
    public void Aggregate_KeepsCountsBeyondUintRange()
    {
        var bins = new long[4];
        bins[0] = 6_000_000_000L;
        bins[3] = 3_000_000_000L;

        double[] columns = HistogramTools.Aggregate(bins, 4);

        Assert.Equal(4, columns.Length);
        Assert.Equal(6_000_000_000d, columns[0], 0);
        Assert.Equal(3_000_000_000d, columns[3], 0);
    }

    [Fact]
    public void IsAbandoned_UnwrapsParallelForAggregates()
    {
        // Parallel.For は本体の例外を AggregateException に包んで投げ直すので、
        // catch (ObjectDisposedException) だけでは競合由来の例外がすり抜ける
        Assert.True(TaskRaceGuard.IsAbandoned(new ObjectDisposedException("image")));
        Assert.True(TaskRaceGuard.IsAbandoned(new OperationCanceledException()));
        Assert.True(TaskRaceGuard.IsAbandoned(
            new AggregateException(new ObjectDisposedException("image"))));
        Assert.True(TaskRaceGuard.IsAbandoned(new AggregateException(
            new ObjectDisposedException("image"), new OperationCanceledException())));
    }

    [Fact]
    public void IsAbandoned_KeepsRealFailures()
    {
        Assert.False(TaskRaceGuard.IsAbandoned(new InvalidDataException("壊れたファイル")));
        Assert.False(TaskRaceGuard.IsAbandoned(new AggregateException(
            new ObjectDisposedException("image"), new InvalidDataException("壊れたファイル"))));

        // 中身のないAggregateExceptionは握りつぶさない(原因不明のまま消える)
        Assert.False(TaskRaceGuard.IsAbandoned(new AggregateException()));
    }
}
