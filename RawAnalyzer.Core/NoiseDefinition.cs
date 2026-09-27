namespace RawAnalyzer.Core;

/// <summary>
/// raw code 値(または2枚の差分 A−B)の画素数・和・二乗和。ノイズ測定の集計単位。
/// </summary>
/// <remarks>
/// 和・二乗和を整数で持つので、同じ画素の集合なら走査の形や並列の分担によらず同じ値になる。
/// 16bitでは値の二乗が約4.3e9に達し、long の二乗和は約21億サンプルで桁あふれするため
/// 二乗和は UInt128 で持つ。
/// </remarks>
internal struct NoiseMoments
{
    /// <summary>画素数。</summary>
    public long Count;

    /// <summary>値の和。</summary>
    public long Sum;

    /// <summary>値の二乗和。</summary>
    public UInt128 SumOfSquares;

    /// <summary>値を1つ加える(差分は負にもなる)。</summary>
    /// <param name="value">raw code 値、または差分。</param>
    public void Add(int value)
    {
        Count++;
        Sum += value;
        SumOfSquares += (ulong)((long)value * value);
    }

    /// <summary>別の集計を合算する。</summary>
    /// <param name="other">合算する集計。</param>
    public void Add(in NoiseMoments other)
    {
        Count += other.Count;
        Sum += other.Sum;
        SumOfSquares += other.SumOfSquares;
    }
}

/// <summary>
/// ノイズ測定の定義(評価画素数・平均・σ_total の合成・差分からの時間ノイズ・FPN・飽和信号レベル)。
/// </summary>
/// <remarks>
/// 矩形版(<see cref="NoiseAnalysis"/>)と格子版(<see cref="ChannelRegionAnalysis"/>)は
/// 画素の走査だけを受け持ち、集計したモーメントから測定値を求める部分はここだけに置く。
/// 両者が別々に式を持つと、同じ画素集合を与えても結果が食い違い得るため。
/// </remarks>
internal static class NoiseDefinition
{
    /// <summary>
    /// 集計したモーメントから測定結果を求める。
    /// </summary>
    /// <param name="channels">
    /// 空間統計(σ_total の元)のモーメント。Bayerのチャネル別に分けた場合はチャネルごと、
    /// 分けない場合は1つ。σ_total はチャネル内分散を画素数重みでプールした
    /// √(Σ nᵢσᵢ² / Σ nᵢ) で、チャネル間の平均値差(感度差)を分散に入れない
    /// (1つだけならそのσ)。評価画素数と平均は全チャネルの合計から求める。
    /// </param>
    /// <param name="difference">
    /// 2枚の差分 A−B のモーメント(空間統計と同じ画素の集合)。単一フレームの測定ならnull。
    /// 時間ノイズは σ_temporal = σ(A−B)/√2、FPNは √(σ_total² − σ_temporal²)(負は0)。
    /// </param>
    /// <param name="saturationCode">飽和信号レベル(raw code)。</param>
    /// <param name="bitDepth">ビット深度(飽和信号レベルの上限)。</param>
    /// <returns>測定結果。画素0個なら画素数・平均・σはすべて0。単一フレームならσ_temporal・σ_FPNはNaN。</returns>
    internal static NoiseMeasurement Evaluate(
        ReadOnlySpan<NoiseMoments> channels,
        NoiseMoments? difference,
        double saturationCode,
        int bitDepth)
    {
        long count = 0;
        long sum = 0;
        foreach (NoiseMoments channel in channels)
        {
            count += channel.Count;
            sum += channel.Sum;
        }

        double pooledVariance = 0;
        foreach (NoiseMoments channel in channels)
        {
            if (channel.Count > 0)
            {
                // 重みを割合で持つと、1チャネルだけのとき重みがちょうど1になり、
                // そのチャネルの分散がそのまま σ_total² になる
                pooledVariance += (double)channel.Count / count * Variance(channel);
            }
        }

        double mean = count > 0 ? (double)sum / count : 0;
        double sigmaTotal = Math.Sqrt(pooledVariance);
        double sigmaTemporal = double.NaN;
        double sigmaFpn = double.NaN;
        if (difference is { } differenceMoments)
        {
            // 独立な2枚の差分は分散が2倍になるため √2 で割る。
            // 差分では固定パターン成分が相殺されるので、残りが時間ノイズ
            sigmaTemporal = Math.Sqrt(Variance(differenceMoments) / 2.0);
            sigmaFpn = Math.Sqrt(Math.Max(
                0, sigmaTotal * sigmaTotal - sigmaTemporal * sigmaTemporal));
        }

        return new NoiseMeasurement(
            count, mean, sigmaTotal, sigmaTemporal, sigmaFpn,
            ResolveSaturation(saturationCode, bitDepth));
    }

    /// <summary>
    /// 飽和信号レベルを決める。0以下・非有限ならビット深度の最大値、
    /// 最大値を超える指定は最大値へ切り詰める。
    /// </summary>
    /// <param name="saturationCode">指定された飽和信号レベル(raw code)。</param>
    /// <param name="bitDepth">ビット深度。</param>
    /// <returns>DR計算に使う飽和信号レベル。</returns>
    internal static double ResolveSaturation(double saturationCode, int bitDepth)
    {
        double max = (1 << bitDepth) - 1;

        // 前のファイル(より深いビット深度)の飽和コードが残っていても DR を過大評価しない。
        // NaN は比較を素通りするので有限性も確かめる(NaNだとDRがNaNになる)
        return double.IsFinite(saturationCode) && saturationCode > 0
            ? Math.Min(saturationCode, max)
            : max;
    }

    /// <summary>母分散 Σx²/n − μ²。丸めで負になった分は0、画素0個も0。</summary>
    private static double Variance(in NoiseMoments moments)
    {
        if (moments.Count == 0)
        {
            return 0;
        }

        double mean = (double)moments.Sum / moments.Count;
        return Math.Max(0, (double)moments.SumOfSquares / moments.Count - mean * mean);
    }
}
