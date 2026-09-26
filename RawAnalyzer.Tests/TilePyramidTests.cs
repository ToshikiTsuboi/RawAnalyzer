using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class TilePyramidTests
{
    [Fact]
    public void Create_4x4_Level2IsBlockAverage()
    {
        ushort[] values =
        {
            0, 10, 20, 30,
            40, 50, 60, 70,
            80, 90, 100, 110,
            120, 130, 140, 150,
        };
        using RawImage image = TestImages.FromCodes(values, 4, 4);
        TilePyramid pyramid = TilePyramid.Create(image);

        PyramidLevel? level2 = pyramid.GetLevel(2);
        Assert.NotNull(level2);
        Assert.Equal(2, level2.Width);
        Assert.Equal(2, level2.Height);
        Assert.Equal((0 + 10 + 40 + 50) / 4, level2.GetPixel(0, 0));
        Assert.Equal((20 + 30 + 60 + 70) / 4, level2.GetPixel(1, 0));
        Assert.Equal((80 + 90 + 120 + 130) / 4, level2.GetPixel(0, 1));
        Assert.Equal((100 + 110 + 140 + 150) / 4, level2.GetPixel(1, 1));
    }

    [Fact]
    public void Create_MaxLevelPixels_SkipsOversizedLevelsButKeepsCoarser()
    {
        ushort[] values = TestData.MakePattern(16 * 16, 16);
        using RawImage image = TestImages.FromCodes(values, 16, 16);

        // L2=8x8=64px は上限16を超えるのでスキップ、L4=4x4=16pxから生成
        // (f=32,64 は1x1に縮退するが上限内なので生成される)
        TilePyramid pyramid = TilePyramid.Create(image, maxLevelPixels: 16);
        Assert.Equal(new[] { 4, 8, 16, 32, 64 }, pyramid.Levels.Select(l => l.Factor));

        // L4は元画像から直接4x4平均で生成される
        PyramidLevel level4 = pyramid.GetLevel(4)!;
        long sum = 0;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                sum += values[y * 16 + x];
            }
        }

        Assert.Equal((ushort)(sum / 16), level4.GetPixel(0, 0));
    }

    [Fact]
    public void SelectFactor_PicksLargestFactorNotExceedingInverseZoom()
    {
        ushort[] values = TestData.MakePattern(128 * 128, 16);
        using RawImage image = TestImages.FromCodes(values, 128, 128);
        TilePyramid pyramid = TilePyramid.Create(image);

        // 128x128 では 1/64(2x2)まで全レベルが生成される
        Assert.Equal(new[] { 2, 4, 8, 16, 32, 64 }, pyramid.Levels.Select(l => l.Factor));

        // 等倍以上は常に1、それ以外は 1/zoom を超えない最大の縮小率
        Assert.Equal(1, pyramid.SelectFactor(1.0));
        Assert.Equal(1, pyramid.SelectFactor(0.6));
        Assert.Equal(2, pyramid.SelectFactor(0.5));
        Assert.Equal(2, pyramid.SelectFactor(0.3));
        Assert.Equal(64, pyramid.SelectFactor(0.01));
    }

    [Fact]
    public void Create_CanceledToken_Throws()
    {
        ushort[] values = TestData.MakePattern(8 * 8, 16);
        using RawImage image = TestImages.FromCodes(values, 8, 8);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(
            () => TilePyramid.Create(image, cancellationToken: cts.Token));
    }
}
