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

    [Theory]
    [InlineData(3, 1)]  // 単一フレームの画像
    [InlineData(4, 4)]  // フレーム数と同じ番号(範囲外)
    public void CapturedFrameOutsideImage_RestoresFirstFrame(int captured, int frameCount)
    {
        var source = new HdrSourceFrame();
        source.Capture(viewportFrame: captured, derivedViewShown: false);

        Assert.Equal(0, source.ResolveRestoreFrame(frameCount));
    }

    [Fact]
    public void EnteringFromRawView_SourceFrameStillShown_IsCurrent()
    {
        // Raw表示のフレーム2から分割・合成を計算し、適用の直前もフレーム2を表示している
        var source = new HdrSourceFrame();
        int sourceFrame = source.Capture(viewportFrame: 2, derivedViewShown: false);

        Assert.True(source.IsCurrent(sourceFrame, viewportFrame: 2, derivedViewShown: false));
    }

    [Fact]
    public void EnteringFromRawView_ViewportMovedToAnotherFrame_IsNotCurrent()
    {
        // 計算の間に元画像の表示フレームが替わった。結果は表示中のフレームの撮影ではなく、
        // Raw表示へ戻るときも別のフレームへ戻ってしまうので適用しない
        var source = new HdrSourceFrame();
        int sourceFrame = source.Capture(viewportFrame: 2, derivedViewShown: false);

        Assert.False(source.IsCurrent(sourceFrame, viewportFrame: 3, derivedViewShown: false));
    }

    [Fact]
    public void SplitMergeSwitch_ComparesCapturedFrameInsteadOfDerivedViewport()
    {
        // 分割表示(ビューポートは派生画像のフレーム0)から合成へ切り替える。元画像のフレームは控えた2のまま
        var source = new HdrSourceFrame();
        source.Capture(viewportFrame: 2, derivedViewShown: false);
        int sourceFrame = source.Capture(viewportFrame: 0, derivedViewShown: true);

        Assert.True(source.IsCurrent(sourceFrame, viewportFrame: 0, derivedViewShown: true));
        Assert.False(source.IsCurrent(sourceFrame: 0, viewportFrame: 0, derivedViewShown: true));
    }

    [Fact]
    public void CapturedFrameReplacedByAnotherEntry_IsNotCurrent()
    {
        // フレーム2から始めた計算の適用前に、別のHDR表示の開始がフレーム5を控え直した。
        // Raw表示へ戻るときの戻り先は5になるので、表示がフレーム2へ戻っていてもフレーム2の結果は適用しない
        var source = new HdrSourceFrame();
        int sourceFrame = source.Capture(viewportFrame: 2, derivedViewShown: false);
        source.Capture(viewportFrame: 5, derivedViewShown: false);

        Assert.False(source.IsCurrent(sourceFrame, viewportFrame: 0, derivedViewShown: true));
        Assert.False(source.IsCurrent(sourceFrame, viewportFrame: 2, derivedViewShown: false));
    }
}
