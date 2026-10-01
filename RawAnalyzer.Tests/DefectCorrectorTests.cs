using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class DefectCorrectorTests
{
    [Fact]
    public void Correct_MonoHotPixel_ReplacedByNeighborMedian()
    {
        const int size = 5;
        var codes = new ushort[size * size];
        Array.Fill(codes, (ushort)1000);

        // 中央 (2,2) の近傍8画素(偶数個)は [1000×3, 1100, 1300, 5000×3]。中央値は中央の2値の平均
        // (1100+1300)/2 = 1200 で、片方だけなら 1100・1300、平均なら 2550 になる。
        // 左上隅 (0,0) の近傍3画素(奇数個)は (1,0)・(0,1) の 1000 と (1,1) の 5000。中央値 1000(平均なら 2333)
        codes[1 * size + 1] = 5000;
        codes[1 * size + 2] = 5000;
        codes[1 * size + 3] = 5000;
        codes[2 * size + 1] = 1100;
        codes[2 * size + 3] = 1300;
        codes[2 * size + 2] = 60000; // 中央に白点
        codes[0] = 60000; // 左上隅にも白点(範囲内の近傍3画素だけで補間する)

        using RawImage image = TestImages.FromCodes(codes, size, size);
        var defects = new[]
        {
            new DefectPixel(0, 0, 60000, DefectType.Hot),
            new DefectPixel(2, 2, 60000, DefectType.Hot),
        };

        using RawImage result = DefectCorrector.Correct(image, defects, BayerPattern.None);

        Assert.Equal(1200, result.GetPixel(2, 2));
        Assert.Equal(1000, result.GetPixel(0, 0));
        Assert.Equal(1000, result.GetPixel(4, 4)); // 他画素は不変
    }

    [Fact]
    public void Correct_ResultCarriesPatternUsedForCorrection()
    {
        // RGGBとして読み込んだ後、右パネルでBGGRへ変更してから補正する
        // (パネルの変更は画像の Format には入らず、指定パターンとして渡される)。
        // 補正に使ったパターンが結果に引き継がれず、採用時にRGGBへ戻って
        // 以後のカラー表示・チャネル統計でR/Bが入れ替わっていた(レビュー指摘 #12)
        const int size = 8;
        ushort[] codes = ColorPipelineTests.BuildConstantMosaic(
            size, size, BayerPattern.Bggr, r: 1000, g: 8000, b: 3000);
        codes[2 * size + 2] = 60000; // (2,2) はBGGRのB画素

        using RawImage image = TestImages.FromCodes(codes, size, size, bayer: BayerPattern.Rggb);
        var defects = new[] { new DefectPixel(2, 2, 60000, DefectType.Hot) };

        using RawImage result = DefectCorrector.Correct(image, defects, BayerPattern.Bggr);

        // 隣接G(8000)ではなく同色(B)の近傍で補間する。Bayer では同色の近傍(1画素おき)を
        // 参照するという仕様(R/B で経路は同じ)も、この assert が兼ねている
        Assert.Equal(3000, result.GetPixel(2, 2));
        Assert.Equal(BayerPattern.Bggr, result.Format.Bayer);
    }

    [Fact]
    public void Correct_ClusterDefects_ExcludesOtherDefectsFromReference()
    {
        const int size = 7;
        var codes = new ushort[size * size];
        Array.Fill(codes, (ushort)500);

        // (2,2)〜(4,4) の 3×3 がすべて欠陥の塊
        var defects = new List<DefectPixel>();
        for (int y = 2; y <= 4; y++)
        {
            for (int x = 2; x <= 4; x++)
            {
                codes[y * size + x] = 60000;
                defects.Add(new DefectPixel(x, y, 60000, DefectType.Hot));
            }
        }

        using RawImage image = TestImages.FromCodes(codes, size, size);

        using RawImage result = DefectCorrector.Correct(image, defects, BayerPattern.None);

        // 互いを参照せず健全画素(500)から補間される。中央 (3,3) は半径1の近傍がすべて欠陥なので
        // 半径2へ広げて健全画素を探す(欠陥を参照すると 60000、広げなければ元の 60000 のまま)
        for (int y = 2; y <= 4; y++)
        {
            for (int x = 2; x <= 4; x++)
            {
                Assert.Equal(500, result.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void Correct_MeanMethod_AveragesNeighbors()
    {
        const int size = 5;
        var codes = new ushort[size * size];
        // 左半分1000・右半分2000のエッジ中央に欠陥
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                codes[y * size + x] = (ushort)(x < 2 ? 1000 : 2000);
            }
        }

        codes[2 * size + 2] = 60000;
        using RawImage image = TestImages.FromCodes(codes, size, size);
        var defects = new[] { new DefectPixel(2, 2, 60000, DefectType.Hot) };

        using RawImage result = DefectCorrector.Correct(
            image, defects, BayerPattern.None, DefectCorrectionMethod.Mean);

        // 近傍8画素: 1000×3 + 2000×5 = 8000 → 平均1625
        Assert.Equal(1625, result.GetPixel(2, 2));
    }

    [Fact]
    public void Correct_EmptyDefects_ReturnsCopy()
    {
        ushort[] codes = TestData.MakePattern(4 * 4, 16);
        using RawImage image = TestImages.FromCodes(codes, 4, 4, bayer: BayerPattern.Gbrg);

        using RawImage result = DefectCorrector.Correct(
            image, Array.Empty<DefectPixel>(), BayerPattern.Gbrg);

        Assert.Equal(BayerPattern.Gbrg, result.Format.Bayer);
        Assert.Equal(4, result.Width);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                Assert.Equal(image.GetPixel(x, y), result.GetPixel(x, y));
            }
        }

        // 欠陥0件の早期returnでも、指定パターン(パネルで変更した値)を引き継ぐ
        using RawImage changed = DefectCorrector.Correct(
            image, Array.Empty<DefectPixel>(), BayerPattern.Grbg);
        Assert.Equal(BayerPattern.Grbg, changed.Format.Bayer);
    }
}
