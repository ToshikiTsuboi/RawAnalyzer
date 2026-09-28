using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 縮小ピラミッドの生成を同じ画像・フレーム・世代について1本にまとめる PyramidBuildCoalescer の検証
/// (MainWindow の BuildPyramidAsync / EnsureBayerPyramidAsync が頼る規約)。
/// </summary>
public class PyramidBuildCoalescerTests
{
    [Fact]
    public async Task SameImageFrameAndGeneration_JoinsRunningBuildInsteadOfStartingAnother()
    {
        // Bayerカラーの生成中に現像へ切り替える(同じ画像・フレーム・世代のピラミッドを重ねて求める)。
        // 以前は2本目の全走査を始め、後から終わった方が先に取り付けた方を描画中に破棄していた
        var builds = new PyramidBuildCoalescer();
        var image = new object();
        using var generation = new CancellationTokenSource();
        var build = new TaskCompletionSource();
        int started = 0;

        Task first = builds.RunAsync(image, 0, generation.Token, () =>
        {
            started++;
            return build.Task;
        });
        Task second = builds.RunAsync(image, 0, generation.Token, () =>
        {
            started++;
            return Task.CompletedTask;
        });

        Assert.Equal(1, started);
        Assert.Equal(1, builds.RunningCount);

        // 相乗りした側も、進行中の生成の完了(取り付け)まで待つ
        Assert.False(second.IsCompleted);
        build.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, started);
        Assert.Equal(0, builds.RunningCount);
    }

    [Fact]
    public async Task NewGeneration_DoesNotJoinBuildOfCanceledGeneration()
    {
        // フレームを移して戻る・画像を差し替えると世代(読み込みのトークン)が進み、旧世代の生成は
        // 取り消されて完了時に結果を捨てる。同じ画像・フレームでも旧世代の生成に相乗りせず新しく始める
        var builds = new PyramidBuildCoalescer();
        var image = new object();
        using var oldGeneration = new CancellationTokenSource();
        using var newGeneration = new CancellationTokenSource();
        var oldBuild = new TaskCompletionSource();
        var newBuild = new TaskCompletionSource();
        int started = 0;

        Task old = builds.RunAsync(image, 1, oldGeneration.Token, () =>
        {
            started++;
            return oldBuild.Task;
        });
        oldGeneration.Cancel();
        Task current = builds.RunAsync(image, 1, newGeneration.Token, () =>
        {
            started++;
            return newBuild.Task;
        });
        Task joined = builds.RunAsync(image, 1, newGeneration.Token, () =>
        {
            started++;
            return Task.CompletedTask;
        });

        Assert.Equal(2, started);
        Assert.Equal(2, builds.RunningCount);

        // 新しい世代の生成は旧世代の生成の完了を待たない
        newBuild.SetResult();
        await current.WaitAsync(TimeSpan.FromSeconds(10));
        await joined.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(old.IsCompleted);
        Assert.Equal(1, builds.RunningCount);

        oldBuild.SetResult();
        await old.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, builds.RunningCount);
    }

    [Fact]
    public async Task DifferentImageOrFrame_StartsSeparateBuild()
    {
        // 生成元の画像(元画像とHDR派生ビュー)・フレームが違えば別のピラミッド
        var builds = new PyramidBuildCoalescer();
        var main = new object();
        var derived = new object();
        using var generation = new CancellationTokenSource();
        var pending = new TaskCompletionSource();
        int started = 0;
        Func<Task> start = () =>
        {
            started++;
            return pending.Task;
        };

        Task mainFrame0 = builds.RunAsync(main, 0, generation.Token, start);
        Task mainFrame1 = builds.RunAsync(main, 1, generation.Token, start);
        Task derivedFrame0 = builds.RunAsync(derived, 0, generation.Token, start);

        Assert.Equal(3, started);
        Assert.Equal(3, builds.RunningCount);

        pending.SetResult();
        await Task.WhenAll(mainFrame0, mainFrame1, derivedFrame0).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, builds.RunningCount);
    }

    [Fact]
    public async Task FinishedBuild_IsReleased_AndNextRequestStartsAgain()
    {
        // 完了した生成は手放す(生成元の画像を持ち続けない)。その後の要求は、取り付け済みかどうかを
        // 見る呼び出し側が必要と判断したものなので、新しく始める
        var builds = new PyramidBuildCoalescer();
        var image = new object();
        using var generation = new CancellationTokenSource();
        var build = new TaskCompletionSource();
        int started = 0;

        Task first = builds.RunAsync(image, 0, generation.Token, () =>
        {
            started++;
            return build.Task;
        });
        build.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, builds.RunningCount);

        Task again = builds.RunAsync(image, 0, generation.Token, () =>
        {
            started++;
            return Task.CompletedTask;
        });
        await again.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, started);
        Assert.Equal(0, builds.RunningCount);
    }

    [Fact]
    public async Task FailedBuild_IsReleased_AndFailureReachesJoinedWaiter()
    {
        // 生成が失敗しても進行中の扱いを残さない(以後の要求が失敗した生成を待ち続けない)
        var builds = new PyramidBuildCoalescer();
        var image = new object();
        using var generation = new CancellationTokenSource();
        var build = new TaskCompletionSource();

        Task first = builds.RunAsync(image, 0, generation.Token, () => build.Task);
        Task joined = builds.RunAsync(image, 0, generation.Token, () => Task.CompletedTask);
        build.SetException(new InvalidOperationException("failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => joined.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, builds.RunningCount);
    }

    [Fact]
    public async Task SynchronouslyThrowingStart_IsReleased()
    {
        var builds = new PyramidBuildCoalescer();
        var image = new object();

        Task failed = builds.RunAsync(image, 0, default, () => throw new InvalidOperationException("failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, builds.RunningCount);
    }
}
