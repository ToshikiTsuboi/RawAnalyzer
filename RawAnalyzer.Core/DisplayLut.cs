namespace RawAnalyzer.Core;

/// <summary>
/// 表示変換パラメータ。
/// </summary>
/// <param name="BlackPoint">黒点。この値以下は0にマップされる。</param>
/// <param name="WhitePoint">白点。この値以上は255にマップされる(ゲイン・ガンマ適用前)。</param>
/// <param name="Gain">線形ゲイン(1.0=等倍)。</param>
/// <param name="Gamma">ガンマ値(1.0=リニア)。出力 = x^(1/Gamma)。</param>
/// <param name="Contrast">コントラスト(1.0=等倍)。中心0.5まわりの傾き。</param>
public sealed record DisplayParameters(
    ushort BlackPoint = 0,
    ushort WhitePoint = 65535,
    double Gain = 1.0,
    double Gamma = 1.0,
    double Contrast = 1.0);

/// <summary>
/// 16bit画素値を8bit表示値へ変換する65536エントリのルックアップテーブル。
/// 表示パラメータ変更時はLUTを再生成するだけでよく、画素データの再処理は不要。
/// </summary>
public sealed class DisplayLut
{
    /// <summary>LUTのエントリ数(ushortの全値域)。</summary>
    public const int TableSize = 65536;

    private readonly byte[] _table;

    private DisplayLut(byte[] table, DisplayParameters parameters)
    {
        _table = table;
        Parameters = parameters;
    }

    /// <summary>このLUTの生成に使われたパラメータ。</summary>
    public DisplayParameters Parameters { get; }

    /// <summary>65536エントリの変換テーブル。</summary>
    public ReadOnlySpan<byte> Table => _table;

    /// <summary>
    /// 表示パラメータからLUTを生成する。
    /// 変換は 黒/白点正規化 → ゲイン → コントラスト → ガンマ の順に適用される。
    /// </summary>
    /// <param name="parameters">表示パラメータ。</param>
    /// <returns>生成されたLUT。</returns>
    /// <exception cref="ArgumentOutOfRangeException">Gammaが0以下、またはGain/Contrastが負の場合。</exception>
    public static DisplayLut Create(DisplayParameters parameters)
    {
        if (parameters.Gamma <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Gammaは正の値である必要があります。");
        }

        if (parameters.Gain < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Gainは非負である必要があります。");
        }

        if (parameters.Contrast < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Contrastは非負である必要があります。");
        }

        var table = new byte[TableSize];
        double black = parameters.BlackPoint;
        double white = parameters.WhitePoint;
        double invRange = white > black ? 1.0 / (white - black) : 0.0;
        double invGamma = 1.0 / parameters.Gamma;

        for (int v = 0; v < TableSize; v++)
        {
            double x;
            if (invRange > 0)
            {
                x = (v - black) * invRange;
            }
            else
            {
                x = v > black ? 1.0 : 0.0;
            }

            x *= parameters.Gain;
            x = (x - 0.5) * parameters.Contrast + 0.5;
            x = Math.Clamp(x, 0.0, 1.0);
            x = Math.Pow(x, invGamma);
            table[v] = (byte)Math.Round(x * 255.0);
        }

        return new DisplayLut(table, parameters);
    }

    /// <summary>
    /// 単一の16bit値を8bit表示値へ変換する。
    /// </summary>
    /// <param name="value">16bitフルスケールの画素値。</param>
    /// <returns>8bit表示値。</returns>
    public byte Map(ushort value)
    {
        return _table[value];
    }

    /// <summary>
    /// 16bit画素列を8bit表示値列へ一括変換する。
    /// </summary>
    /// <param name="source">入力画素列。</param>
    /// <param name="destination">source以上の長さの出力バッファ。</param>
    /// <exception cref="ArgumentException">destinationが短すぎる場合。</exception>
    public void Apply(ReadOnlySpan<ushort> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("出力バッファが入力より短いです。", nameof(destination));
        }

        byte[] table = _table;
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = table[source[i]];
        }
    }
}
