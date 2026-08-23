namespace RawAnalyzer.Core;

/// <summary>
/// 3x3カラーマトリクス(行優先: 出力R = M11*R + M12*G + M13*B)。
/// WBゲイン適用後の線形RGBに乗算する。
/// </summary>
/// <param name="M11">1行1列。</param>
/// <param name="M12">1行2列。</param>
/// <param name="M13">1行3列。</param>
/// <param name="M21">2行1列。</param>
/// <param name="M22">2行2列。</param>
/// <param name="M23">2行3列。</param>
/// <param name="M31">3行1列。</param>
/// <param name="M32">3行2列。</param>
/// <param name="M33">3行3列。</param>
public sealed record ColorMatrix(
    double M11, double M12, double M13,
    double M21, double M22, double M23,
    double M31, double M32, double M33)
{
    /// <summary>単位行列(色変換なし)。</summary>
    public static readonly ColorMatrix Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    /// <summary>係数がすべて有限であることを確かめる。</summary>
    /// <exception cref="ArgumentException">NaN や無限大が含まれる場合。</exception>
    public void Validate()
    {
        // NaN は大小比較を素通りするため、範囲チェックだけでは防げない。
        // 1要素でも非有限だと現像後の全画素が壊れる
        foreach (double value in ToArray())
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentException(
                    $"カラーマトリクスに有限でない値が含まれています: {value}");
            }
        }
    }

    /// <summary>単位行列かどうか。</summary>
    public bool IsIdentity => this == Identity;

    /// <summary>行優先の配列(9要素)へ変換する。</summary>
    /// <returns>[M11..M33]の配列。</returns>
    public double[] ToArray()
    {
        return new[] { M11, M12, M13, M21, M22, M23, M31, M32, M33 };
    }
}

/// <summary>
/// カラー現像パラメータ。
/// 適用順: 黒/白点正規化 → WBゲイン → 全体ゲイン → カラーマトリクス → コントラスト → ガンマ。
/// </summary>
/// <param name="BlackLevel">黒レベル(16bitフルスケール値域)。減算後に正規化する。</param>
/// <param name="GainR">Rチャネルのホワイトバランスゲイン。</param>
/// <param name="GainG">Gチャネルのホワイトバランスゲイン(通常1.0基準)。</param>
/// <param name="GainB">Bチャネルのホワイトバランスゲイン。</param>
/// <param name="Gamma">表示ガンマ。出力 = x^(1/Gamma)。</param>
/// <param name="Matrix">カラーマトリクス(null=単位行列)。</param>
/// <param name="WhitePoint">白点(16bitフルスケール値域)。この値以上が最大にマップされる。</param>
/// <param name="Gain">全チャネル共通の線形ゲイン(1.0=等倍)。</param>
/// <param name="Contrast">コントラスト(1.0=等倍)。中心0.5まわりの傾き。</param>
public sealed record DevelopParameters(
    ushort BlackLevel = 0,
    double GainR = 1.0,
    double GainG = 1.0,
    double GainB = 1.0,
    double Gamma = 2.2,
    ColorMatrix? Matrix = null,
    ushort WhitePoint = 65535,
    double Gain = 1.0,
    double Contrast = 1.0);

/// <summary>
/// カラー現像用のチャネル別65536エントリLUT。
/// 黒レベル減算→WBゲイン→ガンマを1回のテーブル参照に焼き込む。
/// </summary>
public sealed class DevelopLuts
{
    // γ=2.2の暗部はLUT入力の量子化が見えやすい(4096では出力が0,6,8,10…と跳び、
    // 非マトリクス経路の65536エントリ直接計算と最大4コードずれる)。
    // 65536にして非マトリクス経路と同じ入力分解能を確保する
    private const int GammaLutSize = 65536;

    private readonly float[]? _linearR;
    private readonly float[]? _linearG;
    private readonly float[]? _linearB;
    private readonly byte[]? _gammaLut;
    private readonly float[]? _matrix;

    private DevelopLuts(
        byte[] r, byte[] g, byte[] b, DevelopParameters parameters,
        float[]? linearR, float[]? linearG, float[]? linearB,
        byte[]? gammaLut, float[]? matrix)
    {
        R = r;
        G = g;
        B = b;
        Parameters = parameters;
        _linearR = linearR;
        _linearG = linearG;
        _linearB = linearB;
        _gammaLut = gammaLut;
        _matrix = matrix;
    }

    /// <summary>Rチャネル用LUT(マトリクスなし時)。</summary>
    public byte[] R { get; }

    /// <summary>Gチャネル用LUT(マトリクスなし時)。</summary>
    public byte[] G { get; }

    /// <summary>Bチャネル用LUT(マトリクスなし時)。</summary>
    public byte[] B { get; }

    /// <summary>生成に使ったパラメータ。</summary>
    public DevelopParameters Parameters { get; }

    /// <summary>カラーマトリクスが適用されるか。</summary>
    public bool HasMatrix => _matrix is not null;

    /// <summary>
    /// パラメータからLUTを生成する。カラーマトリクス指定時は
    /// 線形LUT+ガンマLUTによるマトリクス経路も構築する。
    /// </summary>
    /// <param name="parameters">現像パラメータ。</param>
    /// <returns>生成されたLUT。</returns>
    /// <exception cref="ArgumentOutOfRangeException">Gammaが0以下、またはゲインが負の場合。</exception>
    public static DevelopLuts Create(DevelopParameters parameters)
    {
        // NaN は以降の大小比較をすべて素通りし、現像後の全画素を壊す
        if (!double.IsFinite(parameters.Gamma)
            || !double.IsFinite(parameters.GainR)
            || !double.IsFinite(parameters.GainG)
            || !double.IsFinite(parameters.GainB)
            || !double.IsFinite(parameters.Gain)
            || !double.IsFinite(parameters.Contrast))
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameters), "現像パラメータに有限でない値が含まれています。");
        }

        parameters.Matrix?.Validate();

        if (parameters.Gamma <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Gammaは正の値である必要があります。");
        }

        if (parameters.GainR < 0 || parameters.GainG < 0 || parameters.GainB < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "WBゲインは非負である必要があります。");
        }

        if (parameters.Gain < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Gainは非負である必要があります。");
        }

        if (parameters.Contrast < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Contrastは非負である必要があります。");
        }

        if (parameters.Matrix is not null && !parameters.Matrix.IsIdentity)
        {
            // マトリクス経路ではチャネル別byte LUTを参照しないので構築しない
            // (65536×3回の Math.Pow を丸ごと省ける)
            float[] matrix = Array.ConvertAll(parameters.Matrix.ToArray(), v => (float)v);
            return new DevelopLuts(
                Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), parameters,
                BuildLinearChannel(parameters, (float)parameters.GainR),
                BuildLinearChannel(parameters, (float)parameters.GainG),
                BuildLinearChannel(parameters, (float)parameters.GainB),
                BuildGammaLut(parameters), matrix);
        }

        return new DevelopLuts(
            BuildChannel(parameters, parameters.GainR),
            BuildChannel(parameters, parameters.GainG),
            BuildChannel(parameters, parameters.GainB),
            parameters, null, null, null, null, null);
    }

    /// <summary>マトリクス後の 0〜1 値へコントラストとガンマを適用するLUT。</summary>
    private static byte[] BuildGammaLut(DevelopParameters p)
    {
        var gammaLut = new byte[GammaLutSize];
        double invGamma = 1.0 / p.Gamma;
        for (int i = 0; i < GammaLutSize; i++)
        {
            double x = (double)i / (GammaLutSize - 1);
            x = (x - 0.5) * p.Contrast + 0.5;
            x = Math.Clamp(x, 0.0, 1.0);
            gammaLut[i] = (byte)Math.Round(Math.Pow(x, invGamma) * 255.0);
        }

        return gammaLut;
    }

    /// <summary>
    /// 線形RGB(16bit)を8bit表示値へ変換する。
    /// マトリクスなしはチャネル別LUT、ありは線形化→行列→ガンマで変換する。
    /// </summary>
    /// <param name="r16">R(16bitフルスケール)。</param>
    /// <param name="g16">G(16bitフルスケール)。</param>
    /// <param name="b16">B(16bitフルスケール)。</param>
    /// <param name="r8">出力R。</param>
    /// <param name="g8">出力G。</param>
    /// <param name="b8">出力B。</param>
    public void Convert(ushort r16, ushort g16, ushort b16, out byte r8, out byte g8, out byte b8)
    {
        if (_matrix is null)
        {
            r8 = R[r16];
            g8 = G[g16];
            b8 = B[b16];
            return;
        }

        float lr = _linearR![r16];
        float lg = _linearG![g16];
        float lb = _linearB![b16];
        float[] m = _matrix;
        float outR = Math.Clamp(m[0] * lr + m[1] * lg + m[2] * lb, 0f, 1f);
        float outG = Math.Clamp(m[3] * lr + m[4] * lg + m[5] * lb, 0f, 1f);
        float outB = Math.Clamp(m[6] * lr + m[7] * lg + m[8] * lb, 0f, 1f);
        byte[] gamma = _gammaLut!;
        r8 = gamma[(int)(outR * (GammaLutSize - 1) + 0.5f)];
        g8 = gamma[(int)(outG * (GammaLutSize - 1) + 0.5f)];
        b8 = gamma[(int)(outB * (GammaLutSize - 1) + 0.5f)];
    }

    private static byte[] BuildChannel(DevelopParameters p, double wbGain)
    {
        var table = new byte[65536];
        double black = p.BlackLevel;
        double white = p.WhitePoint;
        double invRange = white > black ? 1.0 / (white - black) : 0.0;
        double invGamma = 1.0 / p.Gamma;
        double gain = wbGain * p.Gain;
        for (int v = 0; v < 65536; v++)
        {
            double x = invRange > 0 ? (v - black) * invRange : v > black ? 1.0 : 0.0;
            x *= gain;
            x = (x - 0.5) * p.Contrast + 0.5;
            x = Math.Clamp(x, 0.0, 1.0);
            table[v] = (byte)Math.Round(Math.Pow(x, invGamma) * 255.0);
        }

        return table;
    }

    private static float[] BuildLinearChannel(DevelopParameters p, float wbGain)
    {
        // コントラストとガンマはマトリクス適用後にかけるため、ここは線形段のみ
        var table = new float[65536];
        float black = p.BlackLevel;
        float white = p.WhitePoint;
        float invRange = white > black ? 1f / (white - black) : 0f;
        float gain = wbGain * (float)p.Gain;
        for (int v = 0; v < 65536; v++)
        {
            float x = invRange > 0 ? (v - black) * invRange : v > black ? 1f : 0f;
            table[v] = Math.Clamp(x * gain, 0f, 1f);
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
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <exception cref="ArgumentException">バッファ長が不足する場合。</exception>
    public static void DemosaicBilinear(
        ushort[] mosaic, int width, int height, int originX, int originY,
        BayerPattern pattern, ushort[] rgb, CancellationToken cancellationToken = default)
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

        // 同ファイル内の他の並列ループと同じく、キャンセル済みなら早期に降りる。
        // パン中は可視領域ぶんのデモザイクが数十〜200ms 無駄に完走していた
        Parallel.For(0, height, (y, state) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                state.Stop();
                return;
            }

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
