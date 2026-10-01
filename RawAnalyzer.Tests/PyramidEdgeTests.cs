using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class PyramidEdgeTests
{
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
    [InlineData(5, 3)]
    [InlineData(8, 8)]
    [InlineData(13, 11)]
    [InlineData(50, 30)]
    [InlineData(37, 64)]
    public void Levels_NonDivisibleSizes_MatchDirectBlockAverageExactly(int width, int height)
    {
        // 連鎖縮小は平均値ではなくブロック合計から積み上げるので、
        // 端に半端なブロックがあっても直接ブロック平均と完全に一致する
        // (以前は割り切れないと連鎖を諦め、レベルごとに元画像を読み直していた。
        // さらにその前は、前段から縮小を重ねると端のブロックが少ない画素数のまま
        // 同じ重みで平均され、直接ブロック平均とずれていた)。
        // 5x3 は L2 が右端1列・下端1行・角1画素の部分ブロックになる最小例(L2 は 3x2)、
        // 8x8 は L2〜L8 が割り切れたあと L16 以降で1x1の部分ブロックになる
        var codes = new ushort[width * height];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)((i * 7919) % 65536);
        }

        using RawImage image = TestImages.FromCodes(codes, width, height);
        TilePyramid pyramid = TilePyramid.Create(image, maxLevelPixels: long.MaxValue);

        Assert.NotEmpty(pyramid.Levels);
        foreach (PyramidLevel level in pyramid.Levels)
        {
            // 元画像の全列・全行がちょうど1つのレベル画素に入る。端の部分ブロックの列・行を
            // 落とす(切り捨て)と、下のループはレベル自身の寸法で回るので画素の比較では気付けない
            Assert.InRange(width, ((level.Width - 1) * level.Factor) + 1, level.Width * level.Factor);
            Assert.InRange(height, ((level.Height - 1) * level.Factor) + 1, level.Height * level.Factor);

            var buffer = new ushort[level.Width];
            for (int y = 0; y < level.Height; y++)
            {
                level.CopyRegion(0, y, level.Width, 1, buffer);
                for (int x = 0; x < level.Width; x++)
                {
                    Assert.Equal(
                        BlockAverage(codes, width, height, x, y, level.Factor), buffer[x]);
                }
            }
        }
    }
}
