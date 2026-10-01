using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// HDR合成ビューの表示黒点・白点と、合成ビューの外(元画像・分割ビュー)の黒点・白点の受け渡し
/// (MainWindow の ApplyDerivedViewAsync / RestoreMainImageAsync / 画像を開くときの判定)。
/// 黒/白レベルのスライダーは、受け渡した黒点・白点を表示中の画像のビット深度のコード値で示す
/// (SyncLevelControlsToActiveBitDepth → DisplayLevels.ToCodes)。
/// </summary>
public class MergedViewLevelsTests
{
    // 元12bitの黒コード64。合成域のフルスケールは (65535 − 1024) × 16 = 1032176 で、65535へ量子化する
    private const ushort SourceBlack = 64 << 4;

    [Fact]
    public void MergedView_StartsAtBlackZeroAndFullScaleWhite_InsteadOfSourceLevels()
    {
        // 合成画像は黒レベル減算済み。元12bitコード1000・黒コード64(内部1024)・2段・露光比16で合成すると
        // 14976 → 16bit量子化 951 になる。以前は元画像の黒点1024のまま表示LUTを作って黒を二重に引き、
        // 表示ゲイン16でも出力が0(黒つぶれ)になっていた(7c9a14a)。
        // 元画像(12bit)で白レベルを2000へ下げたまま合成へ切り替える(長秒の中間調を見るために白を下げるのはよくある)。
        // 以前は白点を共有し、合成画像(合成域のフルスケールを65535へ量子化)でも白点32015のままだったので、
        // 長秒が飽和して短秒(コード3000)から取り戻した高輝度まで白飛び(255)として切っていた。スライダーも
        // 16bitのコード値で白32015という、合成画像では意味のない位置を示していた
        var format = new RawFormat { Width = 2, Height = 2, BitDepth = 12 };
        using RawImage longFrame = TestImages.FromCodes([4095, 1000, 4095, 4095], format);
        using RawImage shortFrame = TestImages.FromCodes([3000, 62, 3000, 3000], format);
        using RawImage quantized = Merge(longFrame, shortFrame);
        ushort highlight = quantized.GetPixel(0, 0);
        ushort shadow = quantized.GetPixel(1, 0);
        Assert.Equal(47722, highlight); // (3000 × 16 − 1024) × 16 = 751616 を 1032176 → 65535 へ量子化
        Assert.Equal(951, shadow);      // 1000 × 16 − 1024 = 14976 を 1032176 → 65535 へ量子化

        (ushort sourceBlack, ushort sourceWhite) = DisplayLevels.ToPoints(64, 2000, bitDepth: 12);
        var levels = new MergedViewLevels();
        (ushort black, ushort white) = levels.Enter(sourceBlack, sourceWhite);

        Assert.Equal((0, 65535), DisplayLevels.ToCodes(black, white, bitDepth: 16));
        Assert.Equal(186, DisplayLut.Create(new DisplayParameters(black, white)).Map(highlight));
        Assert.Equal(59, DisplayLut.Create(new DisplayParameters(black, white, Gain: 16)).Map(shadow));
    }

    [Fact]
    public void SplitMergeRoundTrip_SplitFramesUseSourceLevels()
    {
        // 分割フレームは黒レベル未減算で元画像と同じ値域。合成 → 分割では元画像の黒点・白点へ戻し(合成ビューの調整は
        // 捨てる)、分割ビューで動かした値は元画像の値として保ったまま、分割 → 合成で控え直す。
        // Raw表示へ戻るときも同じく元画像の黒点・白点へ戻す(以前は白点を持ち越し、合成ビューで動かした白40000が
        // 12bitの元画像では白コード2500になっていた)
        var levels = new MergedViewLevels();

        Assert.Equal(Points(0, 65535), levels.Enter(1024, 32015));    // Raw表示 → 合成
        Assert.Equal(Points(1024, 32015), levels.Leave(300, 50000));  // 合成 → 分割
        Assert.Equal(Points(0, 65535), levels.Enter(1024, 30000));    // 分割(白を30000へ) → 合成
        Assert.Equal(Points(1024, 30000), levels.Leave(0, 65535));    // 合成 → Raw表示

        // 戻った後は控えがない(分割ビュー → Raw表示のように合成ビューを通らない抜け方では、黒点・白点はそのまま)
        Assert.Equal(Points(512, 30000), levels.Leave(512, 30000));
    }

    [Fact]
    public void OpeningAnotherImage_DiscardsRememberedLevels()
    {
        // 合成ビューの表示中に別の画像を開いたら(黒点・白点は開いた画像の既定 0・65535 へ戻る)、控えた値を後で
        // 戻さない。残すと、開いた画像を分割表示したときに前の画像の黒点・白点(1024・32015)を戻してしまう
        var levels = new MergedViewLevels();
        levels.Enter(1024, 32015);

        levels.Reset();

        Assert.Equal(Points(0, 65535), levels.Leave(0, 65535));
    }

    /// <summary>元12bit・黒コード64・2段・露光比16で合成し、合成ビューと同じく16bitへ量子化する。</summary>
    private static RawImage Merge(RawImage longFrame, RawImage shortFrame)
    {
        HdrImage merged = HdrMerger.Merge([longFrame, shortFrame], new HdrMergeParameters(16, SourceBlack));
        return merged.ToRawImage16();
    }

    private static (ushort BlackPoint, ushort WhitePoint) Points(ushort black, ushort white) => (black, white);
}
