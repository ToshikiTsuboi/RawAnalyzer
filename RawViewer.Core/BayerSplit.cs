namespace RawViewer.Core;

/// <summary>
/// Bayer 4チャネル(R/Gr/Gb/B)の抽出と2x2タイル並置画像の生成。
/// タイル画像は左上/右上/左下/右下の各象限が、それぞれ元画像パリティ
/// (0,0)/(1,0)/(0,1)/(1,1) のチャネル画像(各 W/2×H/2)になる。
/// </summary>
public static class BayerSplit
{
    /// <summary>
    /// タイル画像の座標を元画像の座標へ写像する。
    /// </summary>
    /// <param name="x">タイル画像X座標。</param>
    /// <param name="y">タイル画像Y座標。</param>
    /// <param name="tiledWidth">タイル画像の幅(偶数)。</param>
    /// <param name="tiledHeight">タイル画像の高さ(偶数)。</param>
    /// <returns>元画像の座標。</returns>
    public static (int X, int Y) MapTiledToSource(int x, int y, int tiledWidth, int tiledHeight)
    {
        int quadWidth = tiledWidth / 2;
        int quadHeight = tiledHeight / 2;
        int quadX = x / quadWidth;
        int quadY = y / quadHeight;
        int innerX = x - quadX * quadWidth;
        int innerY = y - quadY * quadHeight;
        return (innerX * 2 + quadX, innerY * 2 + quadY);
    }

    /// <summary>
    /// 指定象限が表示するチャネルを返す。
    /// </summary>
    /// <param name="pattern">Bayerパターン。</param>
    /// <param name="quadX">象限X(0=左, 1=右)。</param>
    /// <param name="quadY">象限Y(0=上, 1=下)。</param>
    /// <returns>チャネル。</returns>
    public static BayerChannel GetQuadrantChannel(BayerPattern pattern, int quadX, int quadY)
    {
        return BayerHelper.GetChannel(pattern, quadX, quadY);
    }

    /// <summary>
    /// 2x2タイル並置画像を生成する(行並列)。奇数サイズの端は切り捨てる。
    /// </summary>
    /// <param name="image">元画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <returns>タイル画像の画素(偶数幅×偶数高さ)。</returns>
    public static ushort[] CreateTiled(RawImage image, int frame = 0)
    {
        int width = image.Width & ~1;
        int height = image.Height & ~1;
        var tiled = new ushort[(long)width * height];

        Parallel.For(
            0,
            height,
            () => new ushort[width],
            (y, loopState, sourceRow) =>
            {
                (_, int sourceY) = MapTiledToSource(0, y, width, height);
                image.CopyRegion(frame, 0, sourceY, width, 1, sourceRow);
                int quadWidth = width / 2;
                long rowOffset = (long)y * width;
                for (int x = 0; x < width; x++)
                {
                    int quadX = x / quadWidth;
                    int innerX = x - quadX * quadWidth;
                    tiled[rowOffset + x] = sourceRow[innerX * 2 + quadX];
                }

                return sourceRow;
            },
            _ => { });

        return tiled;
    }
}
