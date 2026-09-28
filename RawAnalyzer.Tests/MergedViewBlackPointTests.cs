using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// HDR合成ビューの表示黒点と、合成ビューの外(元画像・分割ビュー)の黒点の受け渡し
/// (MainWindow の ApplyDerivedViewAsync / RestoreMainImageAsync / 画像を開くときの判定)。
/// </summary>
public class MergedViewBlackPointTests
{
    [Fact]
    public void MergedView_ShowsBlackSubtractedImageWithoutSubtractingAgain()
    {
        // レビュー指摘: 元12bitコード1000・黒コード64(内部1024)・2段・露光比16で合成すると、合成画像は
        // 黒レベル減算済み(14976 → 16bit量子化 951)。元画像の黒点1024のまま表示LUTを作ると黒を二重に引き、
        // 表示ゲイン16でも出力が0(黒つぶれ)になっていた
        var format = new RawFormat { Width = 2, Height = 2, BitDepth = 12 };
        using RawImage longFrame = TestImages.FromCodes([1000, 1000, 1000, 1000], format);
        using RawImage shortFrame = TestImages.FromCodes([62, 62, 62, 62], format);
        const ushort sourceBlack = 64 << 4;
        HdrImage merged = HdrMerger.Merge([longFrame, shortFrame], new HdrMergeParameters(16, sourceBlack));
        using RawImage quantized = merged.ToRawImage16();
        ushort code = quantized.GetPixel(0, 0);
        Assert.Equal(951, code);

        var blackPoint = new MergedViewBlackPoint();
        ushort mergedBlack = blackPoint.Enter(sourceBlack);

        DisplayLut lut = DisplayLut.Create(new DisplayParameters(BlackPoint: mergedBlack, Gain: 16));
        Assert.Equal(0, mergedBlack);
        Assert.Equal(59, lut.Map(code));
    }

    [Fact]
    public void ReturnToRaw_RestoresSourceBlack_AndDropsMergedViewAdjustment()
    {
        // 合成ビューで動かした黒レベルは合成ビューの表示だけのもの。Raw表示へ戻ったら元画像の黒点で表示する
        // (以前は合成ビューの値を持ち越し、16bitの黒10が12bitの元画像では黒コード0になっていた)
        var blackPoint = new MergedViewBlackPoint();
        blackPoint.Enter(currentBlackPoint: 1024);

        Assert.Equal(1024, blackPoint.Leave(currentBlackPoint: 10));

        // 戻った後は控えがない(分割ビュー → Raw表示のように合成ビューを通らない抜け方では、黒点はそのまま)
        Assert.Equal(1024, blackPoint.Leave(currentBlackPoint: 1024));
        Assert.Equal(512, blackPoint.Leave(currentBlackPoint: 512));
    }

    [Fact]
    public void SplitMergeRoundTrip_SplitFramesUseSourceBlack()
    {
        // 分割フレームは黒レベル未減算。合成 → 分割では元画像の黒点へ戻し、分割 → 合成では控え直す
        var blackPoint = new MergedViewBlackPoint();

        Assert.Equal(0, blackPoint.Enter(currentBlackPoint: 1024));    // Raw表示 → 合成
        Assert.Equal(1024, blackPoint.Leave(currentBlackPoint: 300));  // 合成 → 分割
        Assert.Equal(0, blackPoint.Enter(currentBlackPoint: 1024));    // 分割 → 合成
        Assert.Equal(1024, blackPoint.Leave(currentBlackPoint: 0));    // 合成 → Raw表示
    }

    [Fact]
    public void OpeningAnotherImage_DiscardsRememberedBlack()
    {
        // 合成ビューの表示中に別の画像を開いたら(黒点は開いた画像の既定0へ戻る)、控えた黒点を後で戻さない。
        // 残すと、開いた画像を分割表示したときに前の画像の黒点(1024)を戻してしまう
        var blackPoint = new MergedViewBlackPoint();
        blackPoint.Enter(currentBlackPoint: 1024);

        blackPoint.Reset();

        Assert.Equal(0, blackPoint.Leave(currentBlackPoint: 0));
    }
}
