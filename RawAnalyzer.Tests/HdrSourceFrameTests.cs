using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// HDR派生ビュー(分割・合成)の元にした元画像のフレームと、Raw表示へ戻るときに
/// 表示し直すフレーム(MainWindow の CaptureHdrSourceFrame / RestoreMainImageAsync の判定)。
/// </summary>
public class HdrSourceFrameTests
{
    [Fact]
    public void LineInterleavedSequence_SplitMergeThenRaw_RestoresSourceFrame()
    {
        // 25フレームの行交互HDRで3フレーム目(インデックス2)を表示中に分割 → 合成 → Raw表示。
        // 復帰で先頭フレームに戻ると、分割・合成した撮影とは別時刻のフレームが表示され、
        // シーケンスUIも「1 / 25」に戻っていた
        var source = new HdrSourceFrame();

        Assert.Equal(2, source.Capture(viewportFrame: 2, derivedViewShown: false));

        // 分割表示中のビューポートは派生画像(フレーム0)を指す。合成へ切り替えても元フレームを引き継ぐ
        Assert.Equal(2, source.Capture(viewportFrame: 0, derivedViewShown: true));

        Assert.Equal(2, source.ResolveRestoreFrame(frameCount: 25));
    }

    [Fact]
    public void FrameSequential_RestoresFrameShownBeforeHdr()
    {
        // フレーム連結はフレーム=露光で、分割・合成には全フレームを使う。戻り先は
        // HDR表示に入る直前に表示していたフレーム(ここでは短秒のフレーム1)
        var source = new HdrSourceFrame();
        source.Capture(viewportFrame: 1, derivedViewShown: false);

        Assert.Equal(1, source.ResolveRestoreFrame(frameCount: 2));
    }

    [Fact]
    public void NextHdrEntry_CapturesNewlyShownFrame()
    {
        // Raw表示へ戻ってから別フレームへ送り、再びHDR表示にしたらそのフレームを控え直す
        var source = new HdrSourceFrame();
        source.Capture(viewportFrame: 2, derivedViewShown: false);
        source.Capture(viewportFrame: 7, derivedViewShown: false);

        Assert.Equal(7, source.ResolveRestoreFrame(frameCount: 25));
    }

    [Theory]
    [InlineData(3, 1)]  // 単一フレームの画像
    [InlineData(4, 4)]  // フレーム数と同じ番号(範囲外)
    public void CapturedFrameOutsideImage_RestoresFirstFrame(int captured, int frameCount)
    {
        var source = new HdrSourceFrame();
        source.Capture(viewportFrame: captured, derivedViewShown: false);

        Assert.Equal(0, source.ResolveRestoreFrame(frameCount));
    }
}
