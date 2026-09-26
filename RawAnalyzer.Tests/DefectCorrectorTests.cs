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
        codes[2 * size + 2] = 60000; // 中央に白点
        codes[0] = 60000; // 左上隅にも白点(範囲内の近傍3画素だけで補間する)

        using RawImage image = TestImages.FromCodes(codes, size, size);
        var defects = new[]
        {
            new DefectPixel(0, 0, 60000, DefectType.Hot),
            new DefectPixel(2, 2, 60000, DefectType.Hot),
        };

        using RawImage result = DefectCorrector.Correct(image, defects, BayerPattern.None);

        Assert.Equal(1000, result.GetPixel(2, 2));
        Assert.Equal(1000, result.GetPixel(0, 0));
        Assert.Equal(1000, result.GetPixel(4, 4)); // 他画素は不変
    }

    [Fact]
    public void Correct_BayerUsesSameChannelNeighbors()
    {
        // R=1000, G=8000, B=3000 のRGGBモザイクでR画素に白点
        const int size = 8;
        ushort[] codes = ColorPipelineTests.BuildConstantMosaic(
            size, size, BayerPattern.Rggb, r: 1000, g: 8000, b: 3000);
        codes[2 * size + 2] = 60000; // (2,2) はR画素

        using RawImage image = TestImages.FromCodes(codes, size, size, bayer: BayerPattern.Rggb);
        var defects = new[] { new DefectPixel(2, 2, 60000, DefectType.Hot) };

        using RawImage result = DefectCorrector.Correct(image, defects, BayerPattern.Rggb);

        // 隣接G(8000)ではなく同色R(1000)で補間されること
        Assert.Equal(1000, result.GetPixel(2, 2));
    }

    [Fact]
    public void Correct_ClusterDefects_ExcludesOtherDefectsFromReference()
    {
        const int size = 7;
        var codes = new ushort[size * size];
        Array.Fill(codes, (ushort)500);
        // 隣り合う2画素が両方欠陥
        codes[3 * size + 3] = 60000;
        codes[3 * size + 4] = 60000;

        using RawImage image = TestImages.FromCodes(codes, size, size);
        var defects = new[]
        {
            new DefectPixel(3, 3, 60000, DefectType.Hot),
            new DefectPixel(4, 3, 60000, DefectType.Hot),
        };

        using RawImage result = DefectCorrector.Correct(image, defects, BayerPattern.None);

        // 互いを参照せず健全画素(500)から補間される
        Assert.Equal(500, result.GetPixel(3, 3));
        Assert.Equal(500, result.GetPixel(4, 3));
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
    }
}
