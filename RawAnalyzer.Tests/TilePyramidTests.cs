using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class TilePyramidTests
{
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
