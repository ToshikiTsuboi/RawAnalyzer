using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// HDR派生画像の縮小ピラミッドの生成(MainWindow の BuildDerivedPyramidAsync)が、派生画像の差し替え・
/// Raw表示への復帰で取り消され、派生画像の破棄と競合しても例外を漏らさないことの検証。
/// </summary>
/// <remarks>
/// 取り消しの検証は UI スレッド(Dispatcher)で行う。生成の完了後の続きは Dispatcher へ戻ってから
/// 走るので、生成が取り消しより先に終わっていても、結果を受け取るのは取り消しの後になる
/// (MainWindow と同じ順序)。
/// </remarks>
[Collection("WPF UI")]
public class DerivedPyramidBuildTests
{
    [Fact]
    public async Task Build_ReturnsPyramidOfDerivedImage()
    {
        var build = new DerivedPyramidBuild();
        using RawImage derived = MakeImage(64, 32);

        TilePyramid? pyramid = await build.CreateAsync(derived);

        Assert.NotNull(pyramid);
        Assert.Equal(64, pyramid.SourceWidth);
        Assert.Equal(32, pyramid.SourceHeight);
    }

    [Fact]
    public async Task DerivedImageDisposed_EndsWithoutException()
    {
        // 生成中に派生画像が破棄されると、Parallel.For の中の読み出しが ObjectDisposedException になり
        // AggregateException に包まれて出てくる。以前は OperationCanceledException しか受けず、
        // 投げっぱなしのタスクから漏れて UnobservedTaskException としてログに残っていた
        var build = new DerivedPyramidBuild();
        RawImage derived = MakeImage(256, 256);
        derived.Dispose();

        TilePyramid? pyramid = await build.CreateAsync(derived);

        Assert.Null(pyramid);
    }

    [Fact]
    public Task Cancel_DiscardsBuildForDiscardedDerivedImage() => WpfTestHost.Run(async () =>
    {
        // Raw表示への復帰。派生画像を破棄する前に生成を取り消し、結果も受け取らない
        var build = new DerivedPyramidBuild();
        using RawImage derived = MakeImage(1024, 1024);

        Task<TilePyramid?> job = build.CreateAsync(derived);
        build.Cancel();

        Assert.Null(await job);
    });

    [Fact]
    public Task NextDerivedImage_CancelsBuildForPreviousOne() => WpfTestHost.Run(async () =>
    {
        // 分割⇔合成を素早く切り替える。前の派生画像用の生成は取り消し、新しい派生画像の分だけを受け取る。
        // 以前は取り消せず、前の生成が最後まで走り続けていた
        var build = new DerivedPyramidBuild();
        using RawImage split = MakeImage(1024, 1024);
        using RawImage merged = MakeImage(1024, 1024);

        Task<TilePyramid?> first = build.CreateAsync(split);
        Task<TilePyramid?> second = build.CreateAsync(merged);

        Assert.Null(await first);
        TilePyramid? latest = await second;
        Assert.NotNull(latest);
        Assert.Equal(1024, latest.SourceWidth);
    });

    private static RawImage MakeImage(int width, int height) =>
        TestImages.FromCodes(new ushort[width * height], width, height);
}
