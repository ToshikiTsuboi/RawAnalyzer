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
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(8, 6)]
    [InlineData(6, 10)]
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

    [Fact]
    public void TryMapSourceToTiledAxis_MatchesCreatedTiles()
    {
        // 行・列ごとの写像が、実際に並べたタイル画像の位置と一致する。
        // 元画像の1行はタイル画像の1行に、1列は1列に並ぶ(値 = y*6+x で座標を識別)
        ushort[] values = Enumerable.Range(0, 6 * 4).Select(i => (ushort)i).ToArray();
        using RawImage image = TestImages.FromCodes(values, 6, 4);

        ushort[] tiled = BayerSplit.CreateTiled(image);

        for (int sourceY = 0; sourceY < 4; sourceY++)
        {
            Assert.True(BayerSplit.TryMapSourceToTiledAxis(sourceY, 4, out int tiledY));
            for (int sourceX = 0; sourceX < 6; sourceX++)
            {
                Assert.True(BayerSplit.TryMapSourceToTiledAxis(sourceX, 6, out int tiledX));
                Assert.Equal(sourceY * 6 + sourceX, tiled[tiledY * 6 + tiledX]);
            }
        }

        Assert.False(BayerSplit.TryMapSourceToTiledAxis(6, 6, out _));
        Assert.False(BayerSplit.TryMapSourceToTiledAxis(6, 7, out _)); // 奇数長は偶数へ切り詰めて扱う
    }

    [Fact]
    public void CreateTiled_4x4_PlacesChannelsInQuadrants()
    {
        // 値 = y*4+x で座標を識別
        ushort[] values = Enumerable.Range(0, 16).Select(i => (ushort)i).ToArray();
        using RawImage image = TestImages.FromCodes(values, 4, 4);

        ushort[] tiled = BayerSplit.CreateTiled(image);

        // 左上象限(パリティ0,0): source(0,0)=0, (2,0)=2, (0,2)=8, (2,2)=10
        Assert.Equal(0, tiled[0 * 4 + 0]);
        Assert.Equal(2, tiled[0 * 4 + 1]);
        Assert.Equal(8, tiled[1 * 4 + 0]);
        Assert.Equal(10, tiled[1 * 4 + 1]);

        // 右上象限(パリティ1,0): source(1,0)=1, (3,0)=3, (1,2)=9, (3,2)=11
        Assert.Equal(1, tiled[0 * 4 + 2]);
        Assert.Equal(3, tiled[0 * 4 + 3]);
        Assert.Equal(9, tiled[1 * 4 + 2]);
        Assert.Equal(11, tiled[1 * 4 + 3]);

        // 左下象限(パリティ0,1): source(0,1)=4, (2,1)=6, (0,3)=12, (2,3)=14
        Assert.Equal(4, tiled[2 * 4 + 0]);
        Assert.Equal(6, tiled[2 * 4 + 1]);
        Assert.Equal(12, tiled[3 * 4 + 0]);
        Assert.Equal(14, tiled[3 * 4 + 1]);

        // 右下象限(パリティ1,1): source(1,1)=5, (3,3)=15
        Assert.Equal(5, tiled[2 * 4 + 2]);
        Assert.Equal(15, tiled[3 * 4 + 3]);
    }

    [Fact]
    public void CreateTiled_OddSize_TruncatesToEven()
    {
        ushort[] values = Enumerable.Range(0, 5 * 3).Select(i => (ushort)i).ToArray();
        using RawImage image = TestImages.FromCodes(values, 5, 3);

        ushort[] tiled = BayerSplit.CreateTiled(image);

        Assert.Equal(4 * 2, tiled.Length);
    }
}
