namespace RawViewer.Core;

/// <summary>
/// カラー現像パラメータ。
/// </summary>
/// <param name="BlackLevel">黒レベル(16bitフルスケール値域)。減算後に正規化する。</param>
/// <param name="GainR">Rチャネルのホワイトバランスゲイン。</param>
/// <param name="GainB">Bチャネルのホワイトバランスゲイン。</param>
/// <param name="Gamma">表示ガンマ。出力 = x^(1/Gamma)。</param>
public sealed record DevelopParameters(
    ushort BlackLevel = 0,
    double GainR = 1.0,
    double GainB = 1.0,
    double Gamma = 2.2);

/// <summary>
/// カラー現像用のチャネル別65536エントリLUT。
/// 黒レベル減算→WBゲイン→ガンマを1回のテーブル参照に焼き込む。
/// </summary>
public sealed class DevelopLuts
{
    private DevelopLuts(byte[] r, byte[] g, byte[] b, DevelopParameters parameters)
    {
        R = r;
        G = g;
        B = b;
        Parameters = parameters;
    }

    /// <summary>Rチャネル用LUT。</summary>
    public byte[] R { get; }

    /// <summary>Gチャネル用LUT。</summary>
    public byte[] G { get; }

    /// <summary>Bチャネル用LUT。</summary>
    public byte[] B { get; }

    /// <summary>生成に使ったパラメータ。</summary>
    public DevelopParameters Parameters { get; }

    /// <summary>
    /// パラメータからLUTを生成する。
    /// </summary>
    /// <param name="parameters">現像パラメータ。</param>
    /// <returns>生成されたLUT。</returns>
    /// <exception cref="ArgumentOutOfRangeException">Gammaが0以下、またはゲインが負の場合。</exception>
    public static DevelopLuts Create(DevelopParameters parameters)
    {
        if (parameters.Gamma <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Gammaは正の値である必要があります。");
        }

        if (parameters.GainR < 0 || parameters.GainB < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "WBゲインは非負である必要があります。");
        }

        return new DevelopLuts(
            BuildChannel(parameters, parameters.GainR),
            BuildChannel(parameters, 1.0),
            BuildChannel(parameters, parameters.GainB),
            parameters);
    }

    private static byte[] BuildChannel(DevelopParameters p, double gain)
    {
        var table = new byte[65536];
        double black = p.BlackLevel;
        double invRange = black < 65535 ? 1.0 / (65535 - black) : 0.0;
        double invGamma = 1.0 / p.Gamma;
        for (int v = 0; v < 65536; v++)
        {
            double x = (v - black) * invRange * gain;
            x = Math.Clamp(x, 0.0, 1.0);
            table[v] = (byte)Math.Round(Math.Pow(x, invGamma) * 255.0);
        }

        return table;
    }
}

/// <summary>
/// Bayerモザイクからのカラー生成(デモザイク・ブロック平均)。
/// </summary>
public static class ColorPipeline
{
    /// <summary>
    /// 2x2 Bayerブロック(v00=左上, v10=右上, v01=左下, v11=右下)をRGBへ変換する。
    /// Gは2画素の平均。ブロックは偶数座標に整列していること。
    /// </summary>
    /// <param name="pattern">Bayerパターン。</param>
    /// <param name="v00">左上画素値。</param>
    /// <param name="v10">右上画素値。</param>
    /// <param name="v01">左下画素値。</param>
    /// <param name="v11">右下画素値。</param>
    /// <param name="r">R値。</param>
    /// <param name="g">G値(Gr/Gb平均)。</param>
    /// <param name="b">B値。</param>
    public static void BlockToRgb(
        BayerPattern pattern,
        ushort v00, ushort v10, ushort v01, ushort v11,
        out ushort r, out ushort g, out ushort b)
    {
        switch (pattern)
        {
            case BayerPattern.Rggb:
                r = v00;
                g = (ushort)((v10 + v01) / 2);
                b = v11;
                break;
            case BayerPattern.Bggr:
                b = v00;
                g = (ushort)((v10 + v01) / 2);
                r = v11;
                break;
            case BayerPattern.Grbg:
                g = (ushort)((v00 + v11) / 2);
                r = v10;
                b = v01;
                break;
            case BayerPattern.Gbrg:
                g = (ushort)((v00 + v11) / 2);
                b = v10;
                r = v01;
                break;
            default:
                r = g = b = v00;
                break;
        }
    }

    /// <summary>
    /// Bayerモザイク領域をバイリニアデモザイクしてRGB(16bit)を生成する。行並列。
    /// </summary>
    /// <param name="mosaic">モザイク画素(width×height)。</param>
    /// <param name="width">領域の幅。</param>
    /// <param name="height">領域の高さ。</param>
    /// <param name="originX">領域左上の元画像X座標(Bayer位相の決定に使用)。</param>
    /// <param name="originY">領域左上の元画像Y座標。</param>
    /// <param name="pattern">Bayerパターン。</param>
    /// <param name="rgb">出力RGB(width×height×3、R,G,Bの順)。</param>
    /// <exception cref="ArgumentException">バッファ長が不足する場合。</exception>
    public static void DemosaicBilinear(
        ushort[] mosaic, int width, int height, int originX, int originY,
        BayerPattern pattern, ushort[] rgb)
    {
        if (mosaic.Length < (long)width * height)
        {
            throw new ArgumentException("モザイクバッファが不足しています。", nameof(mosaic));
        }

        if (rgb.Length < (long)width * height * 3)
        {
            throw new ArgumentException("RGB出力バッファが不足しています。", nameof(rgb));
        }

        // 絶対座標パリティ→チャネルの2x2マップ
        Span<BayerChannel> channelMap = stackalloc BayerChannel[4];
        for (int py = 0; py < 2; py++)
        {
            for (int px = 0; px < 2; px++)
            {
                channelMap[py * 2 + px] = BayerHelper.GetChannel(pattern, px, py);
            }
        }

        BayerChannel[] map = channelMap.ToArray();

        Parallel.For(0, height, y =>
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                ushort v = mosaic[rowOffset + x];
                BayerChannel own = map[(((originY + y) & 1) << 1) | ((originX + x) & 1)];

                long sumR = 0;
                long sumG = 0;
                long sumB = 0;
                int cntR = 0;
                int cntG = 0;
                int cntB = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height)
                    {
                        continue;
                    }

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        int nx = x + dx;
                        if (nx < 0 || nx >= width)
                        {
                            continue;
                        }

                        BayerChannel nch = map[(((originY + ny) & 1) << 1) | ((originX + nx) & 1)];
                        ushort nv = mosaic[ny * width + nx];
                        switch (nch)
                        {
                            case BayerChannel.R:
                                sumR += nv;
                                cntR++;
                                break;
                            case BayerChannel.B:
                                sumB += nv;
                                cntB++;
                                break;
                            default:
                                sumG += nv;
                                cntG++;
                                break;
                        }
                    }
                }

                ushort r;
                ushort g;
                ushort b;
                if (own == BayerChannel.R)
                {
                    r = v;
                    g = cntG > 0 ? (ushort)(sumG / cntG) : v;
                    b = cntB > 0 ? (ushort)(sumB / cntB) : v;
                }
                else if (own == BayerChannel.B)
                {
                    b = v;
                    g = cntG > 0 ? (ushort)(sumG / cntG) : v;
                    r = cntR > 0 ? (ushort)(sumR / cntR) : v;
                }
                else
                {
                    g = v;
                    r = cntR > 0 ? (ushort)(sumR / cntR) : v;
                    b = cntB > 0 ? (ushort)(sumB / cntB) : v;
                }

                int outIndex = (rowOffset + x) * 3;
                rgb[outIndex] = r;
                rgb[outIndex + 1] = g;
                rgb[outIndex + 2] = b;
            }
        });
    }
}
