namespace RawAnalyzer.Core;

/// <summary>
/// 元画像上で1つのBayerチャネルだけから成る格子状の領域。
/// 画素 (X + 2i, Y + 2j)(0 ≤ i &lt; Width, 0 ≤ j &lt; Height)の集合を表す。
/// </summary>
/// <remarks>
/// チャネル分割表示(<see cref="BayerSplit"/>)の1象限の中で選んだ矩形は、元画像では
/// このような2画素刻みの格子になる。元画像の矩形(<see cref="RegionOfInterest"/>)として
/// 扱うと、表示されていない他チャネルの画素まで集計してしまう。
/// X・Y の偶奇がBayer配列上の位相(どのチャネルか)を決める。
/// </remarks>
/// <param name="X">先頭画素の元画像X座標。</param>
/// <param name="Y">先頭画素の元画像Y座標。</param>
/// <param name="Width">横方向の画素数(元画像では2画素刻み)。</param>
/// <param name="Height">縦方向の画素数(元画像では2画素刻み)。</param>
public readonly record struct ChannelRegion(int X, int Y, int Width, int Height)
{
    /// <summary>領域の画素数。</summary>
    public long PixelCount => (long)Width * Height;

    /// <summary>
    /// 領域の画素がすべて画像の範囲内にあるかを返す。空の領域は常に範囲内とみなす。
    /// </summary>
    /// <param name="imageWidth">画像の幅。</param>
    /// <param name="imageHeight">画像の高さ。</param>
    /// <returns>範囲内ならtrue。</returns>
    public bool IsWithin(int imageWidth, int imageHeight)
    {
        if (Width < 0 || Height < 0)
        {
            return false;
        }

        if (Width == 0 || Height == 0)
        {
            return true;
        }

        return X >= 0 && Y >= 0
            && X + 2L * (Width - 1) < imageWidth
            && Y + 2L * (Height - 1) < imageHeight;
    }
}
