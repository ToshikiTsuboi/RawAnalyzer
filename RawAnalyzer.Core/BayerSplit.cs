namespace RawAnalyzer.Core;

/// <summary>
/// Bayer 4チャネル(R/Gr/Gb/B)の2x2タイル並置表示(チャネル分割表示)と元画像の座標の写像。
/// タイル画像は左上/右上/左下/右下の各象限が、それぞれ元画像パリティ
/// (0,0)/(1,0)/(0,1)/(1,1) のチャネル画像(各 W/2×H/2)になる。
/// 奇数寸法の画像は偶数寸法へ切り詰めて並べる(最終列・最終行はどの象限にも現れない)。
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
    /// 元画像の座標を、タイル画像でその画素が表示されている座標へ写像する
    /// (<see cref="MapTiledToSource"/> の逆)。
    /// </summary>
    /// <remarks>
    /// タイル画像は偶数寸法へ切り詰めて並べるため、奇数寸法の画像の最終列・最終行の画素は
    /// どの象限にも現れない(falseを返す)。
    /// </remarks>
    /// <param name="x">元画像X座標。</param>
    /// <param name="y">元画像Y座標。</param>
    /// <param name="tiledWidth">タイル画像の幅(偶数)。</param>
    /// <param name="tiledHeight">タイル画像の高さ(偶数)。</param>
    /// <param name="tiledX">タイル画像X座標。falseのときは0。</param>
    /// <param name="tiledY">タイル画像Y座標。falseのときは0。</param>
    /// <returns>タイル画像に表示されている画素ならtrue。</returns>
    public static bool TryMapSourceToTiled(
        int x, int y, int tiledWidth, int tiledHeight, out int tiledX, out int tiledY)
    {
        if (TryMapSourceToTiledAxis(x, tiledWidth, out tiledX)
            && TryMapSourceToTiledAxis(y, tiledHeight, out tiledY))
        {
            return true;
        }

        tiledX = 0;
        tiledY = 0;
        return false;
    }

    /// <summary>
    /// 元画像の1軸の座標(列のX、または行のY)を、タイル画像でその列・行が並ぶ座標へ写像する。
    /// </summary>
    /// <remarks>
    /// タイル画像の1行には元画像の1行だけが(左右の象限に偶数列・奇数列として)並び、
    /// 1列には元画像の1列だけが(上下の象限に偶数行・奇数行として)並ぶ。
    /// そのため軸ごとに独立に写像できる(<see cref="MapTiledToSource"/> も軸ごとに独立)。
    /// </remarks>
    /// <param name="source">元画像の座標。</param>
    /// <param name="tiledLength">タイル画像のその軸の長さ(偶数)。</param>
    /// <param name="tiled">タイル画像の座標。falseのときは0。</param>
    /// <returns>タイル画像に並ぶ座標(0以上かつタイル画像の長さ未満)ならtrue。</returns>
    public static bool TryMapSourceToTiledAxis(int source, int tiledLength, out int tiled)
    {
        int quadLength = tiledLength / 2;
        if (source < 0 || source >= quadLength * 2)
        {
            tiled = 0;
            return false;
        }

        tiled = (source & 1) * quadLength + (source >> 1);
        return true;
    }

    /// <summary>
    /// タイル画像上の矩形を、元画像でそこに表示されている画素の集合(1チャネル分の格子)へ写像する。
    /// </summary>
    /// <remarks>
    /// 1つの象限に収まる矩形は、元画像ではその象限のチャネルだけを2画素刻みで並べた格子になる。
    /// 象限をまたぐ矩形は、別チャネルかつ元画像上で離れた位置の画素を隣に並べて見せているだけなので、
    /// ひとつの領域として扱わない(falseを返す)。
    /// </remarks>
    /// <param name="tiled">タイル画像上の矩形。タイル画像の範囲へクランプしてから写像する。</param>
    /// <param name="tiledWidth">タイル画像の幅(偶数)。</param>
    /// <param name="tiledHeight">タイル画像の高さ(偶数)。</param>
    /// <param name="region">写像先の格子領域。falseのときは既定値。</param>
    /// <returns>クランプ後の矩形が空でなく、1つの象限に収まっていればtrue。</returns>
    public static bool TryMapTiledRegion(
        RegionOfInterest tiled, int tiledWidth, int tiledHeight, out ChannelRegion region)
    {
        region = default;
        int quadWidth = tiledWidth / 2;
        int quadHeight = tiledHeight / 2;
        if (quadWidth <= 0 || quadHeight <= 0)
        {
            return false;
        }

        RegionOfInterest shown = tiled.Clamp(quadWidth * 2, quadHeight * 2);
        if (shown.PixelCount == 0)
        {
            return false;
        }

        int quadX = shown.X / quadWidth;
        int quadY = shown.Y / quadHeight;
        if ((shown.X + shown.Width - 1) / quadWidth != quadX
            || (shown.Y + shown.Height - 1) / quadHeight != quadY)
        {
            return false;
        }

        (int sourceX, int sourceY) = MapTiledToSource(
            shown.X, shown.Y, quadWidth * 2, quadHeight * 2);
        region = new ChannelRegion(sourceX, sourceY, shown.Width, shown.Height);
        return true;
    }
}
