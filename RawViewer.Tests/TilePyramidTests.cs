using RawViewer.Core;
using Xunit;

namespace RawViewer.Tests;

public class TilePyramidTests
{
    private static RawImage LoadImage(ushort[] values16, int width, int height)
    {
        var format = new RawFormat { Width = width, Height = height, BitDepth = 16 };
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(values16, format));
        try
        {
            return RawLoader.Load(path, format);
        }
        finally
        {
            File.Delete(path);
        }
    }

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
        using RawImage image = LoadImage(values, 4, 4);
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
    public void Create_OddDimensions_AveragesPartialEdgeBlocks()
    {
        // 5x3: レベル2は3x2。右端は1列、下端は1行のみの部分ブロック
        ushort[] values =
        {
            10, 20, 30, 40, 50,
            60, 70, 80, 90, 100,
            110, 120, 130, 140, 150,
        };
        using RawImage image = LoadImage(values, 5, 3);
        TilePyramid pyramid = TilePyramid.Create(image);

        PyramidLevel? level2 = pyramid.GetLevel(2);
        Assert.NotNull(level2);
        Assert.Equal(3, level2.Width);
        Assert.Equal(2, level2.Height);
        Assert.Equal((10 + 20 + 60 + 70) / 4, level2.GetPixel(0, 0));
        Assert.Equal((50 + 100) / 2, level2.GetPixel(2, 0));
        Assert.Equal((110 + 120) / 2, level2.GetPixel(0, 1));
        Assert.Equal(150, level2.GetPixel(2, 1));
    }

    [Fact]
    public void Create_GeneratesAllFactorsUpTo64()
    {
        ushort[] values = TestData.MakePattern(128 * 128, 16);
        using RawImage image = LoadImage(values, 128, 128);
        TilePyramid pyramid = TilePyramid.Create(image);

        Assert.Equal(new[] { 2, 4, 8, 16, 32, 64 }, pyramid.Levels.Select(l => l.Factor));
        Assert.Equal(2, pyramid.GetLevel(64)!.Width);
        Assert.Equal(2, pyramid.GetLevel(64)!.Height);
    }

    [Fact]
    public void Create_CascadedLevels_MatchDirectAverage()
    {
        // L4はL2経由のカスケード生成。4x4ブロック直接平均と一致すること
        // (ブロックサイズが割り切れる場合、2x2平均の2段は4x4平均と一致する)
        ushort[] values = TestData.MakePattern(16 * 16, 12);
        for (int i = 0; i < values.Length; i++)
        {
            values[i] <<= 4;
        }

        using RawImage image = LoadImage(values, 16, 16);
        TilePyramid pyramid = TilePyramid.Create(image);

        PyramidLevel level4 = pyramid.GetLevel(4)!;
        for (int by = 0; by < 4; by++)
        {
            for (int bx = 0; bx < 4; bx++)
            {
                long sum = 0;
                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        sum += values[(by * 4 + y) * 16 + bx * 4 + x];
                    }
                }

                // 2x2平均を2回重ねると切り捨て誤差が最大3載る
                int direct = (int)(sum / 16);
                Assert.InRange(level4.GetPixel(bx, by), direct - 3, direct + 3);
            }
        }
    }

    [Fact]
    public void Create_MaxLevelPixels_SkipsOversizedLevelsButKeepsCoarser()
    {
        ushort[] values = TestData.MakePattern(16 * 16, 16);
        using RawImage image = LoadImage(values, 16, 16);

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

    [Theory]
    [InlineData(2.0, 1)]
    [InlineData(1.0, 1)]
    [InlineData(0.6, 1)]
    [InlineData(0.5, 2)]
    [InlineData(0.3, 2)]
    [InlineData(0.25, 4)]
    [InlineData(0.01, 64)]
    public void SelectFactor_PicksLargestFactorNotExceedingInverseZoom(double zoom, int expected)
    {
        ushort[] values = TestData.MakePattern(128 * 128, 16);
        using RawImage image = LoadImage(values, 128, 128);
        TilePyramid pyramid = TilePyramid.Create(image);
        Assert.Equal(expected, pyramid.SelectFactor(zoom));
    }

    [Fact]
    public void Create_CanceledToken_Throws()
    {
        ushort[] values = TestData.MakePattern(8 * 8, 16);
        using RawImage image = LoadImage(values, 8, 8);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(
            () => TilePyramid.Create(image, cancellationToken: cts.Token));
    }
}
