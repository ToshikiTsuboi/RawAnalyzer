namespace RawAnalyzer.Core;

/// <summary>ホワイトバランスゲイン(Gを基準にしたR/Bの倍率)。</summary>
/// <param name="GainR">Rゲイン。</param>
/// <param name="GainB">Bゲイン。</param>
public readonly record struct WhiteBalanceGains(double GainR, double GainB);

/// <summary>
/// ホワイトバランスゲインの自動計算。
/// </summary>
public static class WhiteBalance
{
    /// <summary>
    /// グレーワールド仮定でWBゲインを計算する。
    /// 画像全体(サンプリング)の各チャネル平均から黒レベルを引き、
    /// R/Bゲイン = G平均 / 各平均 を求める。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="pattern">Bayerパターン。</param>
    /// <param name="blackLevel">
    /// 黒レベル(16bitフルスケール値域)。現像は黒減算後にWBゲインを掛けるため、
    /// 現像に使う <see cref="DevelopParameters.BlackLevel"/> と同じ値を渡すと、
    /// 求めたゲインで現像したときに中性になる。
    /// </param>
    /// <param name="maxSamples">サンプリング画素数の上限。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>WBゲイン。黒減算後のチャネル平均(またはG平均)が0以下の場合はゲイン1。</returns>
    public static WhiteBalanceGains ComputeGrayWorld(
        RawImage image,
        int frame,
        BayerPattern pattern,
        ushort blackLevel = 0,
        long maxSamples = 4_000_000,
        CancellationToken cancellationToken = default)
    {
        if (pattern == BayerPattern.None)
        {
            return new WhiteBalanceGains(1.0, 1.0);
        }

        int width = image.Width & ~1;
        int height = image.Height & ~1;
        long totalPixels = (long)width * height;
        int rowPairStride = (int)Math.Max(1, totalPixels / Math.Max(1, maxSamples));

        long sumR = 0;
        long sumG = 0;
        long sumB = 0;
        long cntR = 0;
        long cntG = 0;
        long cntB = 0;
        var rowTop = new ushort[width];
        var rowBottom = new ushort[width];

        for (int blockY = 0; blockY * 2 < height; blockY += rowPairStride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int y = blockY * 2;
            image.CopyRegion(frame, 0, y, width, 1, rowTop);
            image.CopyRegion(frame, 0, y + 1, width, 1, rowBottom);
            for (int x = 0; x < width; x += 2)
            {
                ColorPipeline.BlockToRgb(
                    pattern, rowTop[x], rowTop[x + 1], rowBottom[x], rowBottom[x + 1],
                    out ushort r, out ushort g, out ushort b);
                sumR += r;
                sumG += g;
                sumB += b;
                cntR++;
                cntG++;
                cntB++;
            }
        }

        // 現像(DevelopLuts)は黒減算後の値にWBゲインを掛けるので、比も黒減算後の平均で取る。
        // 画素ごとに0で切ると暗部のノイズで平均が持ち上がるため、平均から差し引く
        double meanR = cntR > 0 ? (double)sumR / cntR - blackLevel : 0;
        double meanG = cntG > 0 ? (double)sumG / cntG - blackLevel : 0;
        double meanB = cntB > 0 ? (double)sumB / cntB - blackLevel : 0;
        return new WhiteBalanceGains(RatioOrUnity(meanG, meanR), RatioOrUnity(meanG, meanB));
    }

    /// <summary>
    /// 指定画素を含む2x2ブロックを白(グレー)とみなしてWBゲインを計算する(スポイト)。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="pattern">Bayerパターン。</param>
    /// <param name="x">基準画素X。</param>
    /// <param name="y">基準画素Y。</param>
    /// <param name="blackLevel">
    /// 黒レベル(16bitフルスケール値域)。現像に使う <see cref="DevelopParameters.BlackLevel"/>
    /// と同じ値を渡すと、求めたゲインで現像したときにスポイトした色が中性になる。
    /// </param>
    /// <returns>WBゲイン。黒減算後のチャネル値(またはG)が0以下の場合はゲイン1。</returns>
    public static WhiteBalanceGains ComputeSpotGains(
        RawImage image, int frame, BayerPattern pattern, int x, int y, ushort blackLevel = 0)
    {
        if (pattern == BayerPattern.None)
        {
            return new WhiteBalanceGains(1.0, 1.0);
        }

        // 2x2ブロックが取れないサイズでは判定不能
        if (image.Width < 2 || image.Height < 2)
        {
            return new WhiteBalanceGains(1.0, 1.0);
        }

        // クランプ上限も偶数へ落とさないと、奇数幅/高さの最終列・最終行で
        // ブロックが1画素ずれてR/Bが緑画素から算出される
        int blockX = Math.Clamp(x & ~1, 0, (image.Width - 2) & ~1);
        int blockY = Math.Clamp(y & ~1, 0, (image.Height - 2) & ~1);
        ushort v00 = image.GetPixel(blockX, blockY, frame);
        ushort v10 = image.GetPixel(blockX + 1, blockY, frame);
        ushort v01 = image.GetPixel(blockX, blockY + 1, frame);
        ushort v11 = image.GetPixel(blockX + 1, blockY + 1, frame);
        ColorPipeline.BlockToRgb(pattern, v00, v10, v01, v11,
            out ushort r, out ushort g, out ushort b);

        // 現像は黒減算後の値にWBゲインを掛けるため、黒減算前の比では中性にならない
        // (RGGB=(20000,30000;30000,40000)・黒10000 で R1.5/B0.75 → 現像(69,92,103))
        double green = g - blackLevel;
        return new WhiteBalanceGains(
            RatioOrUnity(green, r - blackLevel),
            RatioOrUnity(green, b - blackLevel));
    }

    /// <summary>
    /// G ÷ チャネル のゲインを返す。どちらかが0以下(黒レベル以下)なら中性を作れないので1。
    /// </summary>
    /// <remarks>
    /// 黒減算後は暗部で0や負になり得る。0除算の無限大や負のゲインは現像LUTの生成で
    /// 例外になるか、UI側のクランプで意味のない値に化けるため、判定不能として扱う。
    /// </remarks>
    private static double RatioOrUnity(double green, double channel)
    {
        return green > 0 && channel > 0 ? green / channel : 1.0;
    }
}
