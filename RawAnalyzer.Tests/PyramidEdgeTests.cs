using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class PyramidEdgeTests
{
    private static RawImage LoadImage(ushort[] codes, int width, int height)
    {
        var format = new RawFormat { Width = width, Height = height, BitDepth = 16 };
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(codes, format));
        try
        {
            return RawLoader.Load(path, format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>元画像の指定ブロックの平均(切り捨て)。</summary>
    private static ushort BlockAverage(
        ushort[] codes, int width, int height, int blockX, int blockY, int factor)
    {
        long sum = 0;
        int count = 0;
        for (int y = blockY * factor; y < Math.Min(height, (blockY + 1) * factor); y++)
        {
            for (int x = blockX * factor; x < Math.Min(width, (blockX + 1) * factor); x++)
            {
                sum += codes[y * width + x];
                count++;
            }
        }

        return (ushort)(sum / count);
    }

    [Theory]
    [InlineData(7, 7)]
    [InlineData(9, 5)]
    [InlineData(5, 9)]
    [InlineData(8, 8)]
    public void Levels_EdgeBlocks_MatchDirectBlockAverage(int width, int height)
    {
        // 前段から縮小を重ねると、端のブロックが少ない画素数のまま
        // 同じ重みで平均され、直接ブロック平均とずれていた
        var codes = new ushort[width * height];
        for (int i = 0; i < codes.Length; i++)
        {
            // 端を目立たせるため右下ほど大きな値にする
            codes[i] = (ushort)(1000 + i * 613 % 60000);
        }

        using RawImage image = LoadImage(codes, width, height);
        TilePyramid pyramid = TilePyramid.Create(image, maxLevelPixels: long.MaxValue);

        Assert.NotEmpty(pyramid.Levels);
        foreach (PyramidLevel level in pyramid.Levels)
        {
            var buffer = new ushort[level.Width];
            for (int y = 0; y < level.Height; y++)
            {
                level.CopyRegion(0, y, level.Width, 1, buffer);
                for (int x = 0; x < level.Width; x++)
                {
                    ushort expected = BlockAverage(codes, width, height, x, y, level.Factor);

                    // 整数除算の丸めぶんだけ許容する
                    Assert.True(
                        Math.Abs(buffer[x] - expected) <= 1,
                        $"factor={level.Factor} ({x},{y}): {buffer[x]} vs {expected}");
                }
            }
        }
    }
}
