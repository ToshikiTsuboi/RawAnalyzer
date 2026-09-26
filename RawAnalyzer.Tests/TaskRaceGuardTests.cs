using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 画像の差し替え・キャンセルと競合した処理の例外を見分ける TaskRaceGuard の検証
/// (全体レビュー 2026-08-23 の回帰)。
/// </summary>
public class TaskRaceGuardTests
{
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
