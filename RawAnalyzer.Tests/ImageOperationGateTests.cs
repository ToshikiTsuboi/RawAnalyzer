using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 通常の読み込みと、表示画像を使う操作(保存・演算など)の排他を管理する
/// ImageOperationGate の検証(レビュー指摘 #3 の回帰)。
/// </summary>
public class ImageOperationGateTests
{
    [Fact]
    public void Idle_NothingPendingAndIdleTaskCompleted()
    {
        var gate = new ImageOperationGate();

        Assert.False(gate.IsOperationRunning);
        Assert.False(gate.IsLoadPending);
        Assert.True(gate.WhenOperationsIdleAsync().IsCompleted);
    }

    [Fact]
    public void BeginLoad_IsPendingUntilDisposed_AndDoubleDisposeIsIgnored()
    {
        // 読み込みは開始から確定(または破棄)まで「確定待ち」。この間は操作を始めさせない
        var gate = new ImageOperationGate();

        IDisposable first = gate.BeginLoad();
        IDisposable second = gate.BeginLoad(); // 先の読み込みを後発が追い越す場合
        Assert.True(gate.IsLoadPending);
        Assert.False(gate.IsOperationRunning); // 読み込み自身は操作として数えない
        Assert.True(gate.WhenOperationsIdleAsync().IsCompleted);

        first.Dispose();
        Assert.True(gate.IsLoadPending);

        // 確定時の明示 Dispose と using の Dispose が重なっても数がずれない
        second.Dispose();
        second.Dispose();
        first.Dispose();
        Assert.False(gate.IsLoadPending);

        using IDisposable third = gate.BeginLoad();
        Assert.True(gate.IsLoadPending);
    }

    [Fact]
    public async Task WhenOperationsIdle_WaitsForOutermostNestedOperation()
    {
        // 保存ダイアログ→実行、画像演算ダイアログ→実行のように操作は入れ子になる。
        // 読み込みの確定は一番外側の操作が終わるまで待たせる
        var gate = new ImageOperationGate();
        IDisposable outer = gate.EnterOperation();
        IDisposable inner = gate.EnterOperation();

        Task idle = gate.WhenOperationsIdleAsync();
        Assert.True(gate.IsOperationRunning);
        Assert.False(gate.IsLoadPending);
        Assert.False(idle.IsCompleted);

        inner.Dispose();
        inner.Dispose(); // 二重の Dispose で外側の分まで抜けない
        Assert.True(gate.IsOperationRunning);
        Assert.False(idle.IsCompleted);

        outer.Dispose();
        Assert.False(gate.IsOperationRunning);
        await idle.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenOperationsIdle_CanWaitAgainAfterNextOperationStarts()
    {
        // 再開までに次の操作が始まっていた場合、呼び出し側は待ち直す
        var gate = new ImageOperationGate();
        IDisposable first = gate.EnterOperation();
        Task firstIdle = gate.WhenOperationsIdleAsync();
        first.Dispose();
        await firstIdle.WaitAsync(TimeSpan.FromSeconds(10));

        IDisposable second = gate.EnterOperation();
        Task secondIdle = gate.WhenOperationsIdleAsync();
        Assert.False(secondIdle.IsCompleted);

        second.Dispose();
        await secondIdle.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenOperationsIdle_DoesNotResumeWaiterInsideDispose()
    {
        // 待機側(読み込みの確定)が、最後の操作を抜ける Dispose の中で同期的に走ると、
        // 操作の後始末の途中へ画像の差し替えが割り込む。Dispose は待機側を実行せずに戻ること
        var gate = new ImageOperationGate();
        IDisposable operation = gate.EnterOperation();
        using var release = new ManualResetEventSlim();
        Task waiter = gate.WhenOperationsIdleAsync().ContinueWith(
            _ => release.Wait(TimeSpan.FromSeconds(30)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // 待機側が同期実行されると release を待って Dispose が戻らない
        Task dispose = Task.Run(operation.Dispose);
        Task first = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10)));
        release.Set();

        Assert.Same(dispose, first);
        await waiter.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ActivityStamp_AdvancesWhenOperationOrLoadStarts_NotWhenItEnds()
    {
        // ファイル連番の送りは操作・読み込みに譲る。次のファイルを読んでいる間に始まった操作
        // (HDR分割など)は差し替えの時点で終わっていることもあるため、終わっていても
        // 「始まった」ことが分かること(実行中かどうかだけでは見分けられない)
        var gate = new ImageOperationGate();
        int initial = gate.ActivityStamp;
        Assert.True(gate.WhenOperationsIdleAsync().IsCompleted);
        Assert.Equal(initial, gate.ActivityStamp); // 問い合わせでは進まない

        IDisposable operation = gate.EnterOperation();
        int afterOperation = gate.ActivityStamp;
        Assert.NotEqual(initial, afterOperation);

        operation.Dispose();
        Assert.False(gate.IsOperationRunning);
        Assert.Equal(afterOperation, gate.ActivityStamp); // 終了では戻らない

        IDisposable load = gate.BeginLoad();
        int afterLoad = gate.ActivityStamp;
        Assert.NotEqual(afterOperation, afterLoad);

        load.Dispose();
        load.Dispose();
        Assert.Equal(afterLoad, gate.ActivityStamp);

        // 入れ子の操作(ダイアログ→実行)も開始として数える
        using IDisposable outer = gate.EnterOperation();
        int afterOuter = gate.ActivityStamp;
        using IDisposable inner = gate.EnterOperation();
        Assert.NotEqual(afterOuter, gate.ActivityStamp);
    }
}
