namespace RawAnalyzer.Core;

/// <summary>
/// 整数の画素数・和・二乗和から分散を求める共通の計算(ROI 統計・ヒストグラム・チャネル別解析・ノイズ測定で共有する)。
/// </summary>
internal static class Moments
{
    /// <summary>
    /// 母分散 (n·ΣX² − (ΣX)²) / n²。分子を整数のまま厳密に求めてから一度だけ double にする。
    /// </summary>
    /// <remarks>
    /// ΣX²/n − (ΣX/n)² を double で求めると、ΣX² = n·v² が 2^53 を超えたところで double への変換が丸まり、
    /// 全画素が同じ値でも ulp 程度の分散(16bit の 65535 で σ ≈ 6.9e-4)が残って、σ=0 を「DR なし」とする判定を
    /// すり抜けた(領域の寸法しだいで DR が『—』になったり 159.5 dB になったりした)。整数のままなら一定値の分子は
    /// 厳密に0になる。n ≤ 約2^31・|X| ≤ 65535 なら n·ΣX² と (ΣX)² はどちらも約2^94 以下で、128bit に収まる。
    /// 分子は Cauchy–Schwarz の不等式で常に0以上。
    /// </remarks>
    /// <param name="count">値の個数(0なら分散0)。</param>
    /// <param name="sum">値の和(2枚の差分では負にもなる)。</param>
    /// <param name="sumOfSquares">値の二乗和。</param>
    /// <returns>母分散(0以上)。</returns>
    internal static double PopulationVariance(long count, long sum, UInt128 sumOfSquares)
    {
        if (count <= 0)
        {
            return 0;
        }

        ulong magnitude = sum < 0 ? (ulong)(-sum) : (ulong)sum;
        UInt128 scaled = (UInt128)(ulong)count * sumOfSquares;
        UInt128 squaredSum = (UInt128)magnitude * magnitude;

        // 和と二乗和が別の集合から来た(呼び出し側の誤り)場合も負の分散を返さない
        if (scaled <= squaredSum)
        {
            return 0;
        }

        double n = count;
        return (double)(scaled - squaredSum) / n / n;
    }
}
