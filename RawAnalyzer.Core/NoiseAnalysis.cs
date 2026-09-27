namespace RawAnalyzer.Core;

/// <summary>
/// ノイズ分離とダイナミックレンジの測定結果(値はすべてraw code値域)。
/// </summary>
/// <param name="SampleCount">評価に使った画素数。</param>
/// <param name="Mean">対象フレームの平均。</param>
/// <param name="SigmaTotal">単一フレームの標準偏差(時間ノイズ+FPNを含む)。</param>
/// <param name="SigmaTemporal">時間ノイズ(2枚差分のσ/√2)。1枚のみの場合はNaN。</param>
/// <param name="SigmaFpn">固定パターンノイズ(√(σ_total²−σ_temporal²))。1枚のみならNaN。</param>
/// <param name="SaturationCode">飽和信号レベル(DR計算の分子)。</param>
public readonly record struct NoiseMeasurement(
    long SampleCount,
    double Mean,
    double SigmaTotal,
    double SigmaTemporal,
    double SigmaFpn,
    double SaturationCode)
{
    /// <summary>時間ノイズ基準のダイナミックレンジ[dB](EMVA1288準拠の定義)。</summary>
    public double DynamicRangeTemporalDb => ToDb(SaturationCode, SigmaTemporal);

    /// <summary>FPNを含む総ノイズ基準のダイナミックレンジ[dB]。</summary>
    public double DynamicRangeTotalDb => ToDb(SaturationCode, SigmaTotal);

    /// <summary>時間ノイズ基準のダイナミックレンジ[stop]。</summary>
    public double DynamicRangeTemporalStops => ToStops(SaturationCode, SigmaTemporal);

    /// <summary>FPNを含む総ノイズ基準のダイナミックレンジ[stop]。</summary>
    public double DynamicRangeTotalStops => ToStops(SaturationCode, SigmaTotal);

    private static double ToDb(double saturation, double sigma)
    {
        return sigma > 0 && saturation > 0 ? 20 * Math.Log10(saturation / sigma) : double.NaN;
    }

    private static double ToStops(double saturation, double sigma)
    {
        return sigma > 0 && saturation > 0 ? Math.Log2(saturation / sigma) : double.NaN;
    }
}

/// <summary>
/// ノイズ測定。時間ノイズは2枚のフレーム差分から σ_diff/√2 として求め、
/// 固定パターンノイズ(FPN)と分離する。ダイナミックレンジは
/// 飽和信号レベル ÷ 暗時ノイズ で定義する(EMVA1288の考え方)。
/// Bayerパターン指定時、空間統計(σ_total)はチャネルごとに計算して
/// 画素数重み付きでRMS合成する。混合統計ではチャネル間の感度差
/// (センサ欠陥ではない構造)がσ_totalに乗り、σ_FPNが桁違いに過大になるため
/// (EMVA1288もカラーはチャネル別評価を要求している)。
/// </summary>
public static class NoiseAnalysis
{
    /// <summary>
    /// 単一フレームからノイズを測定する(時間ノイズとFPNは分離できない)。
    /// </summary>
    /// <param name="image">対象画像(通常はダークフレーム)。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="region">
    /// 評価領域。nullなら全体。Bayer指定時も2x2境界へ広げず、この領域の画素だけで集計する。
    /// </param>
    /// <param name="pattern">Bayerパターン。None以外でσをチャネル別に計算する。</param>
    /// <param name="saturationCode">飽和信号レベル(raw code)。0以下ならビット深度の最大値。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>測定結果(SigmaTemporal/SigmaFpnはNaN)。</returns>
    public static NoiseMeasurement MeasureSingle(
        RawImage image,
        int frame = 0,
        RegionOfInterest? region = null,
        BayerPattern pattern = BayerPattern.None,
        double saturationCode = 0,
        CancellationToken cancellationToken = default)
    {
        RegionOfInterest roi = (region ?? new RegionOfInterest(0, 0, image.Width, image.Height))
            .Clamp(image.Width, image.Height);
        (long count, double mean, double sigma) = ComputeSpatialStats(
            image, frame, roi, pattern, cancellationToken);
        double saturation = ResolveSaturation(saturationCode, image.Format.BitDepth);
        return new NoiseMeasurement(
            count, mean, sigma, double.NaN, double.NaN, saturation);
    }

    /// <summary>
    /// 同一条件で撮影した2枚から時間ノイズとFPNを分離して測定する。
    /// 差分により固定パターン成分が相殺されるため、
    /// 時間ノイズ σ_temporal = σ(A−B) / √2 となる。
    /// </summary>
    /// <param name="imageA">1枚目(この画像の統計をσ_totalとする)。</param>
    /// <param name="imageB">2枚目(同一サイズ・同一条件)。</param>
    /// <param name="frameA">1枚目のフレーム番号。</param>
    /// <param name="frameB">2枚目のフレーム番号。</param>
    /// <param name="region">
    /// 評価領域。nullなら全体。σ_total と σ_temporal はどちらもこの領域の画素だけで集計する
    /// (Bayer指定時も2x2境界へ広げない)。
    /// </param>
    /// <param name="pattern">Bayerパターン。None以外でσ_totalをチャネル別に計算する。</param>
    /// <param name="saturationCode">飽和信号レベル(raw code)。0以下ならビット深度の最大値。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>測定結果。</returns>
    /// <exception cref="ArgumentException">サイズまたはビット深度が一致しない場合。</exception>
    public static NoiseMeasurement MeasurePair(
        RawImage imageA,
        RawImage imageB,
        int frameA = 0,
        int frameB = 0,
        RegionOfInterest? region = null,
        BayerPattern pattern = BayerPattern.None,
        double saturationCode = 0,
        CancellationToken cancellationToken = default)
    {
        if (imageA.Width != imageB.Width || imageA.Height != imageB.Height)
        {
            throw new ArgumentException(
                $"サイズが一致しません: {imageA.Width}×{imageA.Height} と " +
                $"{imageB.Width}×{imageB.Height}", nameof(imageB));
        }

        // ビット深度が違うと差分が code 値域で桁ごとずれ、σ_temporal・σ_FPN・DR が
        // まとめて誤る(例: 12bit と 16bit で σ_temporal が約11倍、σ_FPN=0、DR が約21dB低下)。
        // 12bit raw から保存した 16bit TIFF を2枚目に指定するだけで成立するため必ず弾く。
        if (imageA.Format.BitDepth != imageB.Format.BitDepth)
        {
            throw new ArgumentException(
                $"ビット深度が一致しません: {imageA.Format.BitDepth}bit と " +
                $"{imageB.Format.BitDepth}bit。同じビット深度の2枚を指定してください。",
                nameof(imageB));
        }

        RegionOfInterest roi = (region ?? new RegionOfInterest(0, 0, imageA.Width, imageA.Height))
            .Clamp(imageA.Width, imageA.Height);
        (long countA, double meanA, double sigmaTotal) = ComputeSpatialStats(
            imageA, frameA, roi, pattern, cancellationToken);

        int shiftA = 16 - imageA.Format.BitDepth;
        int shiftB = 16 - imageB.Format.BitDepth;

        // 差分は ±65535 に収まるので、二乗和は long で 2.1e9 サンプルまで正確。
        // doubleでの逐次加算より丸めも小さい
        object gate = new();
        long sum = 0;
        UInt128 sumSq = UInt128.Zero;
        long count = 0;

        Parallel.For(
            0,
            roi.Height,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => (RowA: new ushort[roi.Width], RowB: new ushort[roi.Width],
                   Sum: 0L, SumSq: UInt128.Zero, Count: 0L),
            (row, _, local) =>
            {
                imageA.CopyRegion(frameA, roi.X, roi.Y + row, roi.Width, 1, local.RowA);
                imageB.CopyRegion(frameB, roi.X, roi.Y + row, roi.Width, 1, local.RowB);
                long rowSum = local.Sum;

                // スレッドローカルも UInt128 で持つ(longでは合算前に桁あふれする)
                UInt128 rowSumSq = local.SumSq;
                long rowCount = local.Count;
                for (int x = 0; x < roi.Width; x++)
                {
                    int diff = (local.RowA[x] >> shiftA) - (local.RowB[x] >> shiftB);
                    rowSum += diff;
                    rowSumSq += (ulong)((long)diff * diff);
                    rowCount++;
                }

                return (local.RowA, local.RowB, rowSum, rowSumSq, rowCount);
            },
            local =>
            {
                lock (gate)
                {
                    sum += local.Sum;
                    sumSq += local.SumSq;
                    count += local.Count;
                }
            });

        cancellationToken.ThrowIfCancellationRequested();

        double sigmaTemporal = 0;
        if (count > 0)
        {
            double meanDiff = (double)sum / count;
            double varianceDiff = Math.Max(
                0, ((double)sumSq / count) - (meanDiff * meanDiff));
            // 独立な2枚の差分は分散が2倍になるため √2 で割る
            sigmaTemporal = Math.Sqrt(varianceDiff / 2.0);
        }

        double sigmaFpn = Math.Sqrt(Math.Max(
            0, sigmaTotal * sigmaTotal - sigmaTemporal * sigmaTemporal));
        double saturation = ResolveSaturation(saturationCode, imageA.Format.BitDepth);

        return new NoiseMeasurement(
            countA, meanA, sigmaTotal, sigmaTemporal, sigmaFpn, saturation);
    }

    /// <summary>
    /// 空間統計(σ_totalの元)を計算する。評価画素は時間ノイズの差分と同じ roi の全画素。
    /// Bayer指定時はチャネル内分散を画素数重みでプールした √(Σ nᵢσᵢ² / Σ nᵢ) を返し、
    /// チャネル間の平均値差が分散に混入しないようにする。
    /// </summary>
    private static (long Count, double Mean, double Sigma) ComputeSpatialStats(
        RawImage image,
        int frame,
        RegionOfInterest roi,
        BayerPattern pattern,
        CancellationToken cancellationToken)
    {
        if (pattern == BayerPattern.None)
        {
            RegionStatistics stats = ImageAnalysis.ComputeStatistics(
                image, frame, roi, cancellationToken);
            return (stats.SampleCount, stats.Mean, stats.Sigma);
        }

        if (roi.PixelCount == 0)
        {
            return (0, 0, 0);
        }

        // ImageAnalysis.ComputeChannelAnalysis は roi を外側の2x2境界へ広げるので使わない。
        // 差分(時間ノイズ)は roi そのものを集計するため、空間統計だけ広げると
        // σ_FPN = √(σ_total² − σ_temporal²) が別々の画素集合の分散の差になる
        // (6×6 RGGB・roi=(1,1,4,4) では16画素のはずが36画素になり、roi外の1画素で
        // 存在しないFPNが数百LSB出ていた)。Bayerチャネルは画素の絶対座標の偶奇で決まるので、
        // 2x2に揃えなくても roi の画素をそのまま振り分けられる。4つの偶奇クラスが
        // R/Gr/Gb/Bに1対1で対応するため、プール分散はパターンの種類に依らない。
        // 測定用途なのでサンプリングせず全画素から取る
        int shift = 16 - image.Format.BitDepth;
        object gate = new();
        var sums = new long[4];
        var sumSqs = new UInt128[4];
        var counts = new long[4];

        Parallel.For(
            0,
            roi.Height,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => new ParityAccumulator(roi.Width),
            (row, _, local) =>
            {
                int y = roi.Y + row;
                image.CopyRegion(frame, roi.X, y, roi.Width, 1, local.Row);
                local.AddRow(y & 1, roi.X & 1, shift);
                return local;
            },
            local =>
            {
                lock (gate)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        sums[c] += local.Sums[c];
                        sumSqs[c] += local.SumSqs[c];
                        counts[c] += local.Counts[c];
                    }
                }
            });

        cancellationToken.ThrowIfCancellationRequested();

        long total = 0;
        long totalSum = 0;
        double pooledVariance = 0;
        for (int c = 0; c < 4; c++)
        {
            long n = counts[c];
            if (n == 0)
            {
                continue;
            }

            double mean = (double)sums[c] / n;
            double variance = (double)sumSqs[c] / n - mean * mean;
            total += n;
            totalSum += sums[c];
            pooledVariance += n * Math.Max(0, variance);
        }

        return total > 0
            ? (total, (double)totalSum / total, Math.Sqrt(pooledVariance / total))
            : (0, 0, 0);
    }

    private static double ResolveSaturation(double saturationCode, int bitDepth)
    {
        double max = (1 << bitDepth) - 1;

        // 前のファイル(より深いビット深度)の飽和コードが残っていても DR を過大評価しない
        // NaN は比較を素通りするので有限性も確かめる(NaNだとDRがNaNになる)
        return double.IsFinite(saturationCode) && saturationCode > 0
            ? Math.Min(saturationCode, max)
            : max;
    }

    /// <summary>
    /// 画素の絶対座標の偶奇(2x2の4クラス = Bayerの4チャネル)ごとの和・二乗和・画素数。
    /// 並列集計のスレッドローカル状態として使う。
    /// </summary>
    private sealed class ParityAccumulator
    {
        internal ParityAccumulator(int width)
        {
            Row = new ushort[width];
        }

        /// <summary>1行ぶんの読み込みバッファ。</summary>
        internal ushort[] Row { get; }

        /// <summary>クラス別の総和(添字 = 行偶奇×2 + 列偶奇)。</summary>
        internal long[] Sums { get; } = new long[4];

        /// <summary>クラス別の二乗和。</summary>
        internal UInt128[] SumSqs { get; } = new UInt128[4];

        /// <summary>クラス別の画素数。</summary>
        internal long[] Counts { get; } = new long[4];

        /// <summary><see cref="Row"/> に読み込んだ1行を集計する。</summary>
        /// <param name="rowParity">行の絶対Y座標の偶奇。</param>
        /// <param name="firstColumnParity">Row[0] の絶対X座標の偶奇。</param>
        /// <param name="shift">16bit正規化値を raw code へ戻す右シフト量。</param>
        internal void AddRow(int rowParity, int firstColumnParity, int shift)
        {
            ushort[] row = Row;
            for (int start = 0; start < 2 && start < row.Length; start++)
            {
                long sum = 0;

                // 1画素あたり最大 65535² ≈ 4.3e9 なので、1行(半分)なら ulong で桁あふれしない
                ulong sumSq = 0;
                long count = 0;
                for (int x = start; x < row.Length; x += 2)
                {
                    int code = row[x] >> shift;
                    sum += code;
                    sumSq += (ulong)((long)code * code);
                    count++;
                }

                int parity = (rowParity << 1) | ((firstColumnParity + start) & 1);
                Sums[parity] += sum;
                SumSqs[parity] += sumSq;
                Counts[parity] += count;
            }
        }
    }
}
