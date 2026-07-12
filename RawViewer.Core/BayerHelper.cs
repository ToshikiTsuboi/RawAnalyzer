namespace RawViewer.Core;

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
