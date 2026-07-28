namespace RawAnalyzer.Core;

/// <summary>
/// Bayer配列上の画素チャネル。
/// </summary>
public enum BayerChannel
{
    /// <summary>Bayerなし(モノクロ)。</summary>
    None,

    /// <summary>赤。</summary>
    R,

    /// <summary>R行の緑。</summary>
    Gr,

    /// <summary>B行の緑。</summary>
    Gb,

    /// <summary>青。</summary>
    B,
}

/// <summary>
/// Bayerパターンと画素座標からチャネルを求めるヘルパ。
/// </summary>
public static class BayerHelper
{
    /// <summary>
    /// 指定座標の画素チャネルを返す。
    /// </summary>
    /// <param name="pattern">Bayerパターン。</param>
    /// <param name="x">X座標。</param>
    /// <param name="y">Y座標。</param>
    /// <returns>チャネル。パターンがNoneの場合はBayerChannel.None。</returns>
    public static BayerChannel GetChannel(BayerPattern pattern, int x, int y)
    {
        int index = ((y & 1) << 1) | (x & 1);
        return pattern switch
        {
            BayerPattern.Rggb => index switch
            {
                0 => BayerChannel.R,
                1 => BayerChannel.Gr,
                2 => BayerChannel.Gb,
                _ => BayerChannel.B,
            },
            BayerPattern.Bggr => index switch
            {
                0 => BayerChannel.B,
                1 => BayerChannel.Gb,
                2 => BayerChannel.Gr,
                _ => BayerChannel.R,
            },
            BayerPattern.Grbg => index switch
            {
                0 => BayerChannel.Gr,
                1 => BayerChannel.R,
                2 => BayerChannel.B,
                _ => BayerChannel.Gb,
            },
            BayerPattern.Gbrg => index switch
            {
                0 => BayerChannel.Gb,
                1 => BayerChannel.B,
                2 => BayerChannel.R,
                _ => BayerChannel.Gr,
            },
            _ => BayerChannel.None,
        };
    }

    /// <summary>
    /// 原点を (dx, dy) だけずらして切り出したときのBayerパターンを返す。
    /// </summary>
    /// <remarks>
    /// DOLの行オフセット補正のように、奇数行から始まる部分画像を切り出すと
    /// モザイクの位相が変わる。切り出し後の画像に付けるべきパターンを求める。
    /// </remarks>
    /// <param name="pattern">元のパターン。</param>
    /// <param name="dx">切り出し原点のX。</param>
    /// <param name="dy">切り出し原点のY。</param>
    /// <returns>切り出し後のパターン。Noneはそのまま。</returns>
    public static BayerPattern ShiftOrigin(BayerPattern pattern, int dx, int dy)
    {
        if (pattern == BayerPattern.None)
        {
            return BayerPattern.None;
        }

        // 新しい原点の2x2ブロックがどの並びになるかで判定する
        BayerChannel topLeft = GetChannel(pattern, dx, dy);
        if (topLeft == BayerChannel.R)
        {
            return BayerPattern.Rggb;
        }

        if (topLeft == BayerChannel.B)
        {
            return BayerPattern.Bggr;
        }

        // 左上が緑なら、右隣がRかBかで決まる
        return GetChannel(pattern, dx + 1, dy) == BayerChannel.R
            ? BayerPattern.Grbg
            : BayerPattern.Gbrg;
    }

    /// <summary>
    /// チャネルの表示ラベル("R"/"Gr"/"Gb"/"B"/"-")を返す。
    /// </summary>
    /// <param name="channel">チャネル。</param>
    /// <returns>表示ラベル。</returns>
    public static string GetLabel(BayerChannel channel)
    {
        return channel switch
        {
            BayerChannel.R => "R",
            BayerChannel.Gr => "Gr",
            BayerChannel.Gb => "Gb",
            BayerChannel.B => "B",
            _ => "-",
        };
    }
}
