using RawAnalyzer.App.Rendering;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// チャネル分割表示(R/Gr/Gb/Bの2x2タイル)の描画。象限の境目は等倍でも縮小表示でも
/// 元画像の幅・高さ(偶数へ切り詰め)の半分で、ROIの象限判定と同じでなければならない。
/// </summary>
public class ChannelSplitRenderTests
{
    private const byte Background = ViewportRenderer.BackgroundGray;

    private static readonly DisplayLut Lut = DisplayLut.Create(new DisplayParameters());

    // 画素の中心がちょうど画素・ブロックの境目に乗らない原点(ずらし方を変えて何通りも描く)
    private static readonly double[] OriginsX = { -3.3, -1.7, 0.3, 1.3, 2.7 };
    private static readonly double[] OriginsY = { -2.7, -0.3, 0.7, 1.7 };

    [Theory]
    [InlineData(22, 22, 4, 0.25)]  // 象限 11×11。L4 は 2×2 ブロック(端数 3)
    [InlineData(23, 21, 4, 0.2)]   // 奇数寸法: タイル 22×20、象限 11×10
    [InlineData(40, 36, 8, 0.125)] // 象限 20×18。L8 は 2×2 ブロック(端数 4 / 2)
    [InlineData(14, 10, 2, 0.4)]   // 象限 7×5。L2 は 3×2 ブロック(端数 1)
    public void FromBayerLevel_ShowsBlockUnderEachPixelInItsQuadrant(
        int width, int height, int factor, double zoom)
    {
        // 画面の各画素は、中心のタイル座標が属する象限(等倍と同じ境目で分ける)のチャネルの、
        // 中心の画素を含む 縮小率×縮小率 ブロック(縮小レベルの1画素)を示す。
        // レベルが切り捨てた象限の端数は、そのチャネルの最終ブロックで埋める。
        // 以前はレベルのタイルを一様に並べたため、右・下の象限が端数ぶん手前から始まり、
        // 画像の右端・下端も端数の2倍ぶん欠けていた
        int tiledWidth = width & ~1;
        int tiledHeight = height & ~1;
        int quadWidth = tiledWidth / 2;
        int quadHeight = tiledHeight / 2;
        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16, Bayer = BayerPattern.Rggb,
        };
        using RawImage image = TestImages.FromCodes(BlockCodes(width, height, factor), format);
        using BayerPyramid pyramid =
            BayerPyramid.Create(image, format, maxLevelPixels: long.MaxValue);
        RawImage level = pyramid.GetLevel(factor)!;
        int levelQuadWidth = level.Width / 2;
        int levelQuadHeight = level.Height / 2;
        Assert.NotEqual(0, quadWidth % factor); // 象限の幅が縮小率で割り切れない寸法

        var source = new ChannelSplitRenderSource(level, 0, factor, tiledWidth, tiledHeight);
        bool checkedBandX = false;
        bool checkedBandY = false;
        foreach (double originX in OriginsX)
        {
            foreach (double originY in OriginsY)
            {
                const int size = 12;
                byte[] pixels = Render(source, zoom, originX, originY, size, size);
                for (int dy = 0; dy < size; dy++)
                {
                    for (int dx = 0; dx < size; dx++)
                    {
                        // 描画と同じ式で、画面画素の中心のタイル座標を求める
                        double tiledX = originX + ((dx + 0.5) * (1.0 / zoom));
                        double tiledY = originY + ((dy + 0.5) * (1.0 / zoom));
                        byte expected = Background;
                        if (tiledX >= 0 && tiledX < tiledWidth && tiledY >= 0 && tiledY < tiledHeight)
                        {
                            int quadX = tiledX < quadWidth ? 0 : 1;
                            int quadY = tiledY < quadHeight ? 0 : 1;
                            int blockX = Math.Min(
                                (int)(tiledX - quadX * quadWidth) / factor, levelQuadWidth - 1);
                            int blockY = Math.Min(
                                (int)(tiledY - quadY * quadHeight) / factor, levelQuadHeight - 1);
                            expected = Lut.Map(BlockValue(quadY * 2 + quadX, blockX, blockY));
                            checkedBandX |= tiledX >= levelQuadWidth * factor && tiledX < quadWidth;
                            checkedBandY |= tiledY >= levelQuadHeight * factor && tiledY < quadHeight;
                        }

                        byte actual = pixels[(dy * size + dx) * 4];
                        Assert.True(
                            expected == actual,
                            $"origin=({originX},{originY}) タイル座標({tiledX},{tiledY}): " +
                            $"{actual}(期待 {expected})");
                    }
                }
            }
        }

        // 以前ずれていた帯(レベルの象限の終わり〜等倍の境目)の画素を実際に確かめている
        Assert.True(checkedBandX);
        Assert.True(checkedBandY || quadHeight % factor == 0);
    }

    [Theory]
    [InlineData(8, 6, 3.0)]  // 拡大
    [InlineData(9, 7, 0.7)]  // 縮小レベルを使わない縮小、奇数寸法: 最終列・最終行はどの象限にも並ばない
    public void FromActualSize_ShowsSourcePixelOfEachTilePixel(int width, int height, double zoom)
    {
        // 等倍のデータから描くときは、画面画素の中心のタイル画素を MapTiledToSource で
        // 元画像へ写した画素を示す(ROI・カーソル位置の写像と同じ規約)
        int tiledWidth = width & ~1;
        int tiledHeight = height & ~1;
        var codes = new ushort[width * height];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(500 + i * 1000); // 表示LUTを通しても区別できる間隔
        }

        using RawImage image = TestImages.FromCodes(codes, width, height, bayer: BayerPattern.Rggb);
        var source = new ChannelSplitRenderSource(image, 0);
        foreach (double originX in OriginsX)
        {
            foreach (double originY in OriginsY)
            {
                const int size = 32;
                byte[] pixels = Render(source, zoom, originX, originY, size, size);
                for (int dy = 0; dy < size; dy++)
                {
                    for (int dx = 0; dx < size; dx++)
                    {
                        double tiledX = originX + ((dx + 0.5) * (1.0 / zoom));
                        double tiledY = originY + ((dy + 0.5) * (1.0 / zoom));
                        byte expected = Background;
                        if (tiledX >= 0 && tiledX < tiledWidth && tiledY >= 0 && tiledY < tiledHeight)
                        {
                            (int sourceX, int sourceY) = BayerSplit.MapTiledToSource(
                                (int)tiledX, (int)tiledY, tiledWidth, tiledHeight);
                            expected = Lut.Map(codes[sourceY * width + sourceX]);
                        }

                        Assert.Equal(expected, pixels[(dy * size + dx) * 4]);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(0.7)]
    public void RawSource_ShowsPixelUnderEachCenter(double zoom)
    {
        // 継ぎ目のないソース(通常のRaw表示)の描画は変わらない
        const int width = 9;
        const int height = 7;
        var codes = new ushort[width * height];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(500 + i * 1000);
        }

        using RawImage image = TestImages.FromCodes(codes, width, height);
        var source = new RawImageRenderSource(image, 0);
        foreach (double originX in OriginsX)
        {
            foreach (double originY in OriginsY)
            {
                const int size = 32;
                byte[] pixels = Render(source, zoom, originX, originY, size, size);
                for (int dy = 0; dy < size; dy++)
                {
                    for (int dx = 0; dx < size; dx++)
                    {
                        double x = originX + ((dx + 0.5) * (1.0 / zoom));
                        double y = originY + ((dy + 0.5) * (1.0 / zoom));
                        byte expected = x >= 0 && x < width && y >= 0 && y < height
                            ? Lut.Map(codes[(int)y * width + (int)x])
                            : Background;
                        Assert.Equal(expected, pixels[(dy * size + dx) * 4]);
                    }
                }
            }
        }
    }

    [Fact]
    public void GrayPyramidLevel_ShowsLevelPixelUnderEachCenter()
    {
        // 継ぎ目のない縮小レベル(通常のRaw表示のピラミッド)の描画は変わらない
        const int width = 13;
        const int height = 11;
        var codes = new ushort[width * height];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)((i * 7919) % 65536);
        }

        using RawImage image = TestImages.FromCodes(codes, width, height);
        PyramidLevel level = TilePyramid.Create(image, maxLevelPixels: long.MaxValue)
            .Levels.Single(l => l.Factor == 2);
        var source = new PyramidLevelRenderSource(level, width, height);
        const double zoom = 0.4;
        foreach (double originX in OriginsX)
        {
            foreach (double originY in OriginsY)
            {
                const int size = 12;
                byte[] pixels = Render(source, zoom, originX, originY, size, size);
                for (int dy = 0; dy < size; dy++)
                {
                    for (int dx = 0; dx < size; dx++)
                    {
                        double x = originX + ((dx + 0.5) * (1.0 / zoom));
                        double y = originY + ((dy + 0.5) * (1.0 / zoom));
                        byte expected = Background;
                        if (x >= 0 && x < width && y >= 0 && y < height)
                        {
                            int levelX = Math.Min((int)(x / 2), level.Width - 1);
                            int levelY = Math.Min((int)(y / 2), level.Height - 1);
                            expected = Lut.Map(level.GetRow(levelY)[levelX]);
                        }

                        Assert.Equal(expected, pixels[(dy * size + dx) * 4]);
                    }
                }
            }
        }
    }

    private static byte[] Render(
        RenderSource source, double zoom, double originX, double originY,
        int destWidth, int destHeight)
    {
        var request = new RenderRequest
        {
            Source = source,
            Lut = Lut,
            Mode = source is ChannelSplitRenderSource
                ? ViewportDisplayMode.ChannelSplit
                : ViewportDisplayMode.Raw,
            Pattern = BayerPattern.Rggb,
        };
        var destination = new byte[destWidth * destHeight * 4];
        ViewportRenderer.Render(
            request, zoom, originX, originY, destWidth, destHeight, destination,
            CancellationToken.None);
        return destination;
    }

    /// <summary>
    /// 各チャネルの 縮小率×縮小率 ブロックごとに一定値の画像(縮小レベルの各画素がその値になる)。
    /// </summary>
    private static ushort[] BlockCodes(int width, int height, int factor)
    {
        var codes = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                codes[y * width + x] = BlockValue(
                    (y & 1) * 2 + (x & 1), (x >> 1) / factor, (y >> 1) / factor);
            }
        }

        return codes;
    }

    /// <summary>
    /// チャネルとブロックごとに異なる値。表示LUTを通しても区別できるよう1000ずつ離す
    /// (ブロック番号が各軸 0〜3 に収まる寸法で使う)。
    /// </summary>
    private static ushort BlockValue(int channel, int blockX, int blockY)
    {
        return (ushort)(1000 + ((channel * 16) + (blockY * 4) + blockX) * 1000);
    }
}
