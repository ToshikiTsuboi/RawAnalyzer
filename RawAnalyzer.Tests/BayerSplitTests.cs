using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class BayerSplitTests
{
    [Theory]
    [InlineData(1, 0, 2, 0)]   // 象限内オフセット → 元画像では2画素刻み
    [InlineData(2, 0, 1, 0)]   // 右上象限 → パリティ(1,0)
    [InlineData(0, 2, 0, 1)]   // 左下象限 → パリティ(0,1)
    [InlineData(3, 3, 3, 3)]   // 右下象限 + 象限内オフセット(両軸同時)
    public void MapTiledToSource_4x4_MapsQuadrants(int tx, int ty, int sx, int sy)
    {
        Assert.Equal((sx, sy), BayerSplit.MapTiledToSource(tx, ty, 4, 4));
    }

    [Theory]
    [InlineData(8, 6)]   // 非正方: 幅と高さを取り違えると落ちる
    [InlineData(6, 10)]  // 奇数の象限長(3)を含む
    public void TryMapSourceToTiled_IsInverseOfMapTiledToSource(int tiledWidth, int tiledHeight)
    {
        // タイルの全画素と、タイルに並ぶ元画像の全画素が1対1に対応する
        for (int y = 0; y < tiledHeight; y++)
        {
            for (int x = 0; x < tiledWidth; x++)
            {
                (int sourceX, int sourceY) = BayerSplit.MapTiledToSource(x, y, tiledWidth, tiledHeight);
                Assert.True(BayerSplit.TryMapSourceToTiled(
                    sourceX, sourceY, tiledWidth, tiledHeight, out int tiledX, out int tiledY));
                Assert.Equal((x, y), (tiledX, tiledY));

                Assert.True(BayerSplit.TryMapSourceToTiled(
                    x, y, tiledWidth, tiledHeight, out tiledX, out tiledY));
                Assert.Equal((x, y), BayerSplit.MapTiledToSource(tiledX, tiledY, tiledWidth, tiledHeight));
            }
        }
    }

    [Theory]
    [InlineData(4, 0)]   // 最終列(奇数幅の端)
    [InlineData(0, 2)]   // 最終行(奇数高さの端)
    [InlineData(4, 2)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void TryMapSourceToTiled_PixelNotInTiles_ReturnsFalse(int x, int y)
    {
        // 5×3 は 4×2 のタイルとして並べる(CreateTiled と同じく奇数の端は切り捨てる)
        Assert.False(BayerSplit.TryMapSourceToTiled(x, y, 5 & ~1, 3 & ~1, out int tiledX, out int tiledY));
        Assert.Equal((0, 0), (tiledX, tiledY));
    }
}
