using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class WhiteBalanceTests
{
    private static RawImage MakeMosaic(BayerPattern pattern, ushort r, ushort g, ushort b)
    {
        const int size = 8;
        ushort[] mosaic = ColorPipelineTests.BuildConstantMosaic(size, size, pattern, r, g, b);
        return TestImages.FromCodes(mosaic, size, size, bayer: pattern);
    }

    [Fact]
    public void ComputeSpotGains_OddSizedImage_KeepsBayerPhaseAtLastColumn()
    {
        // クランプ上限が奇数のままだとブロックが1画素ずれ、
        // 最終列/最終行で R/B が緑画素から算出される
        const int width = 7;
        const int height = 7;
        ushort[] mosaic = ColorPipelineTests.BuildConstantMosaic(
            width, height, BayerPattern.Rggb, r: 500, g: 1000, b: 2000);
        using RawImage image = TestImages.FromCodes(
            mosaic, width, height, bayer: BayerPattern.Rggb);

        WhiteBalanceGains gains = WhiteBalance.ComputeSpotGains(
            image, 0, BayerPattern.Rggb, width - 1, height - 1);

        Assert.Equal(2.0, gains.GainR, 10);
        Assert.Equal(0.5, gains.GainB, 10);
    }

    /// <summary>推定したゲインを同じ黒レベルの現像LUTへ入れ、各チャネルの出力を返す。</summary>
    private static (byte R, byte G, byte B) Develop(
        WhiteBalanceGains gains, ushort blackLevel, ushort r, ushort g, ushort b)
    {
        var luts = DevelopLuts.Create(new DevelopParameters(
            BlackLevel: blackLevel, GainR: gains.GainR, GainB: gains.GainB, Gamma: 1.0));
        return (luts.R[r], luts.G[g], luts.B[b]);
    }

    [Fact]
    public void ComputeSpotGains_WithBlackLevel_DevelopsToNeutral()
    {
        // レビュー指摘#7の再現値: RGGB=(20000,30000;30000,40000)、黒点10000、Gamma=1。
        // 現像は黒減算後にWBを掛けるので、推定も黒減算後の比でないと中性にならない
        // (黒減算前の比 R1.5/B0.75 では (69,92,103) になっていた)
        using RawImage image = MakeMosaic(BayerPattern.Rggb, r: 20000, g: 30000, b: 40000);

        WhiteBalanceGains gains = WhiteBalance.ComputeSpotGains(
            image, 0, BayerPattern.Rggb, 2, 2, blackLevel: 10000);

        Assert.Equal(2.0, gains.GainR, 10);
        Assert.Equal(2.0 / 3.0, gains.GainB, 10);
        (byte r, byte g, byte b) = Develop(gains, 10000, 20000, 30000, 40000);
        Assert.Equal(92, g);
        Assert.Equal(g, r);
        Assert.Equal(g, b);
    }

    [Theory]
    [InlineData(BayerPattern.Rggb)]
    [InlineData(BayerPattern.Bggr)]
    public void ComputeGrayWorld_WithBlackLevel_DevelopsToNeutral(BayerPattern pattern)
    {
        // 黒レベルは現像と同じ16bitフルスケール値域で渡す。
        // 12bit素材 R/G/B = 1250/1875/2500 は内部値 20000/30000/40000、黒点10000 は code 625
        const int size = 8;
        ushort[] mosaic = ColorPipelineTests.BuildConstantMosaic(
            size, size, pattern, 1250, 1875, 2500);
        using RawImage image = TestImages.FromCodes(
            mosaic, size, size, bitDepth: 12, bayer: pattern);

        WhiteBalanceGains gains = WhiteBalance.ComputeGrayWorld(
            image, 0, pattern, blackLevel: 10000);

        Assert.Equal(2.0, gains.GainR, 10);
        Assert.Equal(2.0 / 3.0, gains.GainB, 10);
        (byte r, byte g, byte b) = Develop(gains, 10000, 20000, 30000, 40000);
        Assert.Equal(g, r);
        Assert.Equal(g, b);
    }

    [Fact]
    public void ComputeSpotGains_ChannelAtOrBelowBlack_ReturnsUnity()
    {
        // 黒点以下のチャネルを含む画素(暗部)では比が0・負・無限大になり中性を作れない。
        // 負のゲインは現像LUTの生成で例外になるため、判定不能としてゲイン1を返す
        using RawImage darkGreen = MakeMosaic(BayerPattern.Rggb, r: 20000, g: 9000, b: 40000);
        using RawImage darkRed = MakeMosaic(BayerPattern.Rggb, r: 10000, g: 30000, b: 40000);

        WhiteBalanceGains greenBelow = WhiteBalance.ComputeSpotGains(
            darkGreen, 0, BayerPattern.Rggb, 0, 0, blackLevel: 10000);
        WhiteBalanceGains redAtBlack = WhiteBalance.ComputeSpotGains(
            darkRed, 0, BayerPattern.Rggb, 0, 0, blackLevel: 10000);

        Assert.Equal(new WhiteBalanceGains(1.0, 1.0), greenBelow);
        Assert.Equal(1.0, redAtBlack.GainR);
        Assert.Equal(2.0 / 3.0, redAtBlack.GainB, 10);
    }

    [Fact]
    public void ComputeSpotGains_TinyImage_ReturnsUnity()
    {
        // 1画素幅では2x2ブロックが取れず GetPixel(1, ...) が範囲外例外になっていた
        using RawImage image = TestImages.FromCodes(
            new ushort[] { 1234 }, 1, 1, bayer: BayerPattern.Rggb);

        WhiteBalanceGains gains = WhiteBalance.ComputeSpotGains(
            image, 0, BayerPattern.Rggb, 0, 0);

        Assert.Equal(1.0, gains.GainR);
        Assert.Equal(1.0, gains.GainB);
    }
}
