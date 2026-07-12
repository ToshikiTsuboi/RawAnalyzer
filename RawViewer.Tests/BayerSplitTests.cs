using RawViewer.Core;
using Xunit;

namespace RawViewer.Tests;

public class BayerSplitTests
{
    private static RawImage LoadImage(ushort[] values, int width, int height)
    {
        var format = new RawFormat { Width = width, Height = height, BitDepth = 16 };
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(values, format));
        try
        {
            return RawLoader.Load(path, format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]   // 左上象限 → パリティ(0,0)
    [InlineData(1, 0, 2, 0)]
    [InlineData(2, 0, 1, 0)]   // 右上象限 → パリティ(1,0)
    [InlineData(3, 0, 3, 0)]
    [InlineData(0, 2, 0, 1)]   // 左下象限 → パリティ(0,1)
    [InlineData(2, 2, 1, 1)]   // 右下象限 → パリティ(1,1)
    [InlineData(3, 3, 3, 3)]
    public void MapTiledToSource_4x4_MapsQuadrants(int tx, int ty, int sx, int sy)
    {
        Assert.Equal((sx, sy), BayerSplit.MapTiledToSource(tx, ty, 4, 4));
    }

    [Fact]
    public void CreateTiled_4x4_PlacesChannelsInQuadrants()
    {
        // 値 = y*4+x で座標を識別
        ushort[] values = Enumerable.Range(0, 16).Select(i => (ushort)i).ToArray();
        using RawImage image = LoadImage(values, 4, 4);

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
        using RawImage image = LoadImage(values, 5, 3);

        ushort[] tiled = BayerSplit.CreateTiled(image);

        Assert.Equal(4 * 2, tiled.Length);
    }

    [Fact]
    public void GetQuadrantChannel_Rggb_MatchesPattern()
    {
        Assert.Equal(BayerChannel.R, BayerSplit.GetQuadrantChannel(BayerPattern.Rggb, 0, 0));
        Assert.Equal(BayerChannel.Gr, BayerSplit.GetQuadrantChannel(BayerPattern.Rggb, 1, 0));
        Assert.Equal(BayerChannel.Gb, BayerSplit.GetQuadrantChannel(BayerPattern.Rggb, 0, 1));
        Assert.Equal(BayerChannel.B, BayerSplit.GetQuadrantChannel(BayerPattern.Rggb, 1, 1));
    }
}
