using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// カラー現像表示の現像LUTを、作ったときのパラメータ(素材のビット深度を含む)と照合して作り直す
/// DevelopLutCache の検証(MainWindow の EnsureDevelopLuts が使う)。
/// </summary>
public class DevelopLutCacheTests
{
    /// <summary>行の和が1(白→白)で非対角が負の、典型的なカメラRGB→表示RGBの行列。</summary>
    private static readonly ColorMatrix TypicalCcm = new(
        1.6, -0.4, -0.2,
        -0.2, 1.4, -0.2,
        0.0, -0.5, 1.5);

    [Fact]
    public void BitDepthChange_RebuildsLutForSaturationCodeOfShownImage()
    {
        // レビュー指摘: 12bitのBayerをカラー現像で表示してLUTを作った後、HDR合成(16bit)へ切り替えて
        // カラー現像を選ぶと、作り直しの印が立たず12bitのLUTのまま描いていた。HDR出力 65520 は16bitでは
        // 白点未満なので WB(2,1,1.5)・典型的なCCMで色が付くが、12bitのLUTでは白飛びとして白になる
        // (保存は現在のパラメータでLUTを作るので、画面と保存の色も食い違った)
        var cache = new DevelopLutCache();
        var raw12 = new DevelopParameters(
            GainR: 2.0, GainB: 1.5, Gamma: 1.0, Matrix: TypicalCcm, SourceBitDepth: 12);
        Assert.Equal((255, 255, 255), Convert(cache.Refresh(raw12), 65520));

        // HDR合成の表示(16bit)。ビット深度だけが変わっても作り直す
        Assert.Equal((255, 178, 255), Convert(cache.Refresh(raw12 with { SourceBitDepth = 16 }), 65520));

        // 元画像(12bit)へ戻ったら12bitのLUTを作り直す
        Assert.Equal((255, 255, 255), Convert(cache.Refresh(raw12), 65520));
    }

    [Fact]
    public void SameParameters_KeepsLut_AnyChangeRebuilds()
    {
        // 同じパラメータ(別インスタンスでも値が同じ)なら作り直さない(間引きの後・同じビット深度の画像の
        // 送りで、65536エントリのLUTを作り直さない)
        var cache = new DevelopLutCache();
        Assert.NotNull(cache.Refresh(new DevelopParameters(
            BlackLevel: 1024, GainR: 2.0, Matrix: new ColorMatrix(1.2, -0.2, 0, 0, 1, 0, 0, 0, 1),
            SourceBitDepth: 12)));

        Assert.Null(cache.Refresh(new DevelopParameters(
            BlackLevel: 1024, GainR: 2.0, Matrix: new ColorMatrix(1.2, -0.2, 0, 0, 1, 0, 0, 0, 1),
            SourceBitDepth: 12)));

        // HDR合成ビューの表示黒点(0)へ替わった
        DevelopLuts? rebuilt = cache.Refresh(new DevelopParameters(
            BlackLevel: 0, GainR: 2.0, Matrix: new ColorMatrix(1.2, -0.2, 0, 0, 1, 0, 0, 0, 1),
            SourceBitDepth: 12));
        Assert.NotNull(rebuilt);
        Assert.Equal(0, rebuilt.Parameters.BlackLevel);
    }

    private static (int R, int G, int B) Convert(DevelopLuts? luts, ushort value)
    {
        Assert.NotNull(luts);
        luts.Convert(value, value, value, out byte r, out byte g, out byte b);
        return (r, g, b);
    }
}
