namespace RawViewer.Core;

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
    /// 画像全体(サンプリング)の各チャネル平均から R/Bゲイン = G平均 / 各平均 を求める。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="pattern">Bayerパターン。</param>
    /// <param name="maxSamples">サンプリング画素数の上限。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>WBゲイン。チャネル平均が0の場合はゲイン1。</returns>
    public static WhiteBalanceGains ComputeGrayWorld(
        RawImage image,
        int frame,
        BayerPattern pattern,
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

        double meanR = cntR > 0 ? (double)sumR / cntR : 0;
        double meanG = cntG > 0 ? (double)sumG / cntG : 0;
        double meanB = cntB > 0 ? (double)sumB / cntB : 0;
        return new WhiteBalanceGains(
            meanR > 0 ? meanG / meanR : 1.0,
            meanB > 0 ? meanG / meanB : 1.0);
    }

    /// <summary>
    /// 指定画素を含む2x2ブロックを白(グレー)とみなしてWBゲインを計算する(スポイト)。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="pattern">Bayerパターン。</param>
    /// <param name="x">基準画素X。</param>
    /// <param name="y">基準画素Y。</param>
    /// <returns>WBゲイン。チャネル値が0の場合はゲイン1。</returns>
    public static WhiteBalanceGains ComputeSpotGains(
        RawImage image, int frame, BayerPattern pattern, int x, int y)
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
        return new WhiteBalanceGains(
            r > 0 ? (double)g / r : 1.0,
            b > 0 ? (double)g / b : 1.0);
    }
}
