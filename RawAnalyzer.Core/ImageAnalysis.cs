namespace RawAnalyzer.Core;

/// <summary>画像上の矩形領域(画素座標)。</summary>
/// <param name="X">左端X座標。</param>
/// <param name="Y">上端Y座標。</param>
/// <param name="Width">幅。</param>
/// <param name="Height">高さ。</param>
public readonly record struct RegionOfInterest(int X, int Y, int Width, int Height)
{
    /// <summary>領域の画素数。</summary>
    public long PixelCount => (long)Width * Height;

    /// <summary>
    /// 画像範囲内に収まるよう領域をクランプする。
    /// </summary>
    /// <param name="imageWidth">画像の幅。</param>
    /// <param name="imageHeight">画像の高さ。</param>
    /// <returns>クランプ済み領域。交差がない場合は幅/高さ0。</returns>
    public RegionOfInterest Clamp(int imageWidth, int imageHeight)
    {
        int x0 = Math.Clamp(X, 0, imageWidth);
        int y0 = Math.Clamp(Y, 0, imageHeight);
        int x1 = Math.Clamp(X + Width, 0, imageWidth);
        int y1 = Math.Clamp(Y + Height, 0, imageHeight);
        return new RegionOfInterest(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }
}

/// <summary>領域統計値(raw code値域)。</summary>
/// <param name="Mean">平均。</param>
/// <param name="Sigma">標準偏差。</param>
/// <param name="Min">最小値。</param>
/// <param name="Max">最大値。</param>
/// <param name="SampleCount">サンプル数。</param>
public readonly record struct RegionStatistics(
    double Mean, double Sigma, int Min, int Max, long SampleCount);

/// <summary>
/// ヒストグラム計算結果。ビンはraw code値域(実ビット深度)で構成される。
/// </summary>
public sealed class HistogramResult
{
    /// <summary>ビン配列。長さは 2^BitDepth。</summary>
    public required long[] Bins { get; init; }

    /// <summary>ビット深度。</summary>
    public required int BitDepth { get; init; }

    /// <summary>サンプリング計算されたかどうか(全画素走査でない)。</summary>
    public required bool IsSampled { get; init; }

    /// <summary>実際に集計されたサンプル数。</summary>
    public required long SampleCount { get; init; }

    /// <summary>サンプルに基づく統計値。</summary>
    public required RegionStatistics Statistics { get; init; }
}

/// <summary>プロファイル(ライン/射影)の統計値。</summary>
/// <param name="Count">サンプル数。</param>
/// <param name="Mean">平均。</param>
/// <param name="Min">最小値。</param>
/// <param name="Max">最大値。</param>
/// <param name="Median">中央値。</param>
/// <param name="Sigma">標準偏差。</param>
public readonly record struct ProfileStatistics(
    int Count, double Mean, double Min, double Max, double Median, double Sigma);

/// <summary>
/// ヒストグラムから導出される解析指標。値はすべてraw code値域。
/// </summary>
/// <param name="SampleCount">総サンプル数。</param>
/// <param name="Mean">平均。</param>
/// <param name="Sigma">標準偏差。</param>
/// <param name="Min">最小値。</param>
/// <param name="Max">最大値。</param>
/// <param name="Median">中央値。</param>
/// <param name="Mode">最頻値。</param>
/// <param name="P1">1パーセンタイル。</param>
/// <param name="P99">99パーセンタイル。</param>
/// <param name="SaturatedPercent">飽和(最大code)画素の割合[%]。</param>
/// <param name="ZeroPercent">黒つぶれ(code=0)画素の割合[%]。</param>
/// <param name="DynamicRangeDb">最大/σ から求めた簡易ダイナミックレンジ[dB]。</param>
public readonly record struct HistogramMetrics(
    long SampleCount,
    double Mean,
    double Sigma,
    int Min,
    int Max,
    int Median,
    int Mode,
    int P1,
    int P99,
    double SaturatedPercent,
    double ZeroPercent,
    double DynamicRangeDb);

/// <summary>Bayerチャネル1つぶんのヒストグラムと統計。</summary>
public sealed class ChannelHistogram
{
    /// <summary>チャネル。</summary>
    public required BayerChannel Channel { get; init; }

    /// <summary>ビン配列(raw code値域、長さ2^BitDepth)。</summary>
    public required long[] Bins { get; init; }

    /// <summary>このチャネルの統計。</summary>
    public required RegionStatistics Statistics { get; init; }
}

/// <summary>
/// Bayerチャネル別解析の結果(全体+R/Gr/Gb/B)。
/// </summary>
public sealed class ChannelAnalysisResult
{
    /// <summary>全画素のヒストグラム。</summary>
    public required HistogramResult Total { get; init; }

    /// <summary>チャネル別ヒストグラム(R, Gr, Gb, Bの順)。パターンNoneでは空。</summary>
    public required IReadOnlyList<ChannelHistogram> Channels { get; init; }
}

/// <summary>
/// ヒストグラム・領域統計・ラインプロファイルの計算。値はすべてraw code値域で返す。
/// </summary>
public static class ImageAnalysis
{
    /// <summary>この画素数を超える領域はヒストグラムをサンプリング計算する既定閾値。</summary>
    public const long DefaultMaxHistogramSamples = 10_000_000;

    /// <summary>
    /// 領域の統計値(mean/σ/min/max)を全画素走査で正確に計算する。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="roi">対象領域(画像範囲内であること)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>raw code値域の統計値。</returns>
    public static RegionStatistics ComputeStatistics(
        RawImage image, int frame, RegionOfInterest roi, CancellationToken cancellationToken = default)
    {
        return ComputeStatistics(image, frame, roi, maxSamples: 0, cancellationToken);
    }

    /// <summary>
    /// 領域の統計値(mean/σ/min/max)を計算する。
    /// 領域画素数が <paramref name="maxSamples"/> を超える場合は行・列の等間隔
    /// サンプリングで計算する(ヒストグラムと同じ基準)。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="roi">対象領域(画像範囲内であること)。</param>
    /// <param name="maxSamples">サンプリングへ切り替える画素数閾値。0以下なら常に全画素走査。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>raw code値域の統計値。SampleCountは実際に集計した画素数。</returns>
    public static RegionStatistics ComputeStatistics(
        RawImage image,
        int frame,
        RegionOfInterest roi,
        long maxSamples,
        CancellationToken cancellationToken = default)
    {
        roi = roi.Clamp(image.Width, image.Height);
        if (roi.PixelCount == 0)
        {
            return new RegionStatistics(0, 0, 0, 0, 0);
        }

        int stride = 1;
        if (maxSamples > 0 && roi.PixelCount > maxSamples)
        {
            stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)roi.PixelCount / maxSamples)));

            // ヒストグラムと同じ理由でstrideは奇数にする。
            // 偶数だと走査位置のx/y偶奇が固定され、BayerではRだけの統計になる
            if ((stride & 1) == 0)
            {
                stride++;
            }
        }

        int shift = 16 - image.Format.BitDepth;
        object gate = new();
        long totalSum = 0;

        // 16bitでは code^2 が最大約4.3e9。longだと約2.1e9サンプルで桁あふれし、
        // varianceが負→σ=0に握りつぶされて無警告で壊れる(ギガピクセルは対応範囲)
        UInt128 totalSumSq = UInt128.Zero;
        long totalCount = 0;
        int totalMin = int.MaxValue;
        int totalMax = int.MinValue;
        int rowCount = (roi.Height + stride - 1) / stride;
        int sampleStride = stride;

        Parallel.For(
            0,
            rowCount,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => (Buffer: new ushort[roi.Width], Sum: 0L, SumSq: 0L, Count: 0L,
                Min: int.MaxValue, Max: int.MinValue),
            (rowIndex, _, local) =>
            {
                image.CopyRegion(
                    frame, roi.X, roi.Y + rowIndex * sampleStride, roi.Width, 1, local.Buffer);
                long sum = local.Sum;
                long sumSq = local.SumSq;
                long count = local.Count;
                int min = local.Min;
                int max = local.Max;
                for (int x = 0; x < roi.Width; x += sampleStride)
                {
                    int code = local.Buffer[x] >> shift;
                    sum += code;
                    sumSq += (long)code * code;
                    count++;
                    if (code < min)
                    {
                        min = code;
                    }

                    if (code > max)
                    {
                        max = code;
                    }
                }

                return (local.Buffer, sum, sumSq, count, min, max);
            },
            local =>
            {
                lock (gate)
                {
                    totalSum += local.Sum;
                    totalSumSq += (ulong)local.SumSq;
                    totalCount += local.Count;
                    totalMin = Math.Min(totalMin, local.Min);
                    totalMax = Math.Max(totalMax, local.Max);
                }
            });

        cancellationToken.ThrowIfCancellationRequested();
        if (totalCount == 0)
        {
            return new RegionStatistics(0, 0, 0, 0, 0);
        }

        double mean = (double)totalSum / totalCount;
        double variance = (double)totalSumSq / totalCount - mean * mean;
        return new RegionStatistics(
            mean, Math.Sqrt(Math.Max(0, variance)), totalMin, totalMax, totalCount);
    }

    /// <summary>
    /// 領域のヒストグラムを計算する。領域画素数がmaxSamplesを超える場合は
    /// 行・列の等間隔サンプリングで計算し、IsSampled=true を返す。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="region">対象領域。nullなら全体。</param>
    /// <param name="maxSamples">サンプリングに切り替える画素数閾値。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>raw code値域のヒストグラム。</returns>
    public static HistogramResult ComputeHistogram(
        RawImage image,
        int frame,
        RegionOfInterest? region = null,
        long maxSamples = DefaultMaxHistogramSamples,
        CancellationToken cancellationToken = default)
    {
        RegionOfInterest roi = (region ?? new RegionOfInterest(0, 0, image.Width, image.Height))
            .Clamp(image.Width, image.Height);
        int bitDepth = image.Format.BitDepth;
        int shift = 16 - bitDepth;
        var bins = new long[1 << bitDepth];
        if (roi.PixelCount == 0)
        {
            return new HistogramResult
            {
                Bins = bins,
                BitDepth = bitDepth,
                IsSampled = false,
                SampleCount = 0,
                Statistics = new RegionStatistics(0, 0, 0, 0, 0),
            };
        }

        int stride = (int)Math.Ceiling(Math.Sqrt((double)roi.PixelCount / maxSamples));
        stride = Math.Max(1, stride);

        // strideが偶数だと走査位置のx/y偶奇が固定され、Bayer画像では
        // 4チャネルのうち1つ(例: Rのみ)しかサンプリングされず統計値が別物になる。
        // 奇数にすると行・列とも偶奇が交互に進み、4チャネルが均等に含まれる。
        if ((stride & 1) == 0)
        {
            stride++;
        }

        bool sampled = stride > 1;

        var buffer = new ushort[roi.Width];
        long sum = 0;
        UInt128 sumSq = UInt128.Zero;
        int min = int.MaxValue;
        int max = int.MinValue;
        long count = 0;

        for (int row = 0; row < roi.Height; row += stride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image.CopyRegion(frame, roi.X, roi.Y + row, roi.Width, 1, buffer);
            for (int x = 0; x < roi.Width; x += stride)
            {
                int code = buffer[x] >> shift;
                bins[code]++;
                sum += code;
                sumSq += (ulong)((long)code * code);
                if (code < min)
                {
                    min = code;
                }

                if (code > max)
                {
                    max = code;
                }

                count++;
            }
        }

        double mean = count > 0 ? (double)sum / count : 0;
        double variance = count > 0 ? (double)sumSq / count - mean * mean : 0;
        return new HistogramResult
        {
            Bins = bins,
            BitDepth = bitDepth,
            IsSampled = sampled,
            SampleCount = count,
            Statistics = new RegionStatistics(
                mean, Math.Sqrt(Math.Max(0, variance)), min, max, count),
        };
    }

    /// <summary>
    /// Bayerチャネル別のヒストグラムと統計を計算する(全体分も同時に返す)。
    /// 領域は2x2ブロック単位で走査し、サンプリング時も全チャネルを均等に含める。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="pattern">Bayerパターン(Noneの場合チャネル別は空)。</param>
    /// <param name="region">対象領域。nullなら全体。</param>
    /// <param name="maxSamples">サンプリングに切り替える画素数閾値。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>全体+チャネル別の解析結果。</returns>
    public static ChannelAnalysisResult ComputeChannelAnalysis(
        RawImage image,
        int frame,
        BayerPattern pattern,
        RegionOfInterest? region = null,
        long maxSamples = DefaultMaxHistogramSamples,
        CancellationToken cancellationToken = default)
    {
        RegionOfInterest roi = (region ?? new RegionOfInterest(0, 0, image.Width, image.Height))
            .Clamp(image.Width, image.Height);
        int bitDepth = image.Format.BitDepth;
        int shift = 16 - bitDepth;
        int binCount = 1 << bitDepth;

        // 2x2ブロック整列。内側へ切り詰めるとROIが小さいとき大半の画素が捨てられ
        // (例: roi=(1,1,4,4) で16画素中4画素、各チャネル1サンプル→σ=0)、
        // 統計が意味をなさなくなる。外側へスナップして必ずROI全体を含める
        // (各辺で最大1画素はみ出す)。
        int x0 = roi.X & ~1;
        int y0 = roi.Y & ~1;
        int x1 = Math.Min(image.Width, (roi.X + roi.Width + 1) & ~1);
        int y1 = Math.Min(image.Height, (roi.Y + roi.Height + 1) & ~1);
        int blocksX = Math.Max(0, (x1 - x0) / 2);
        int blocksY = Math.Max(0, (y1 - y0) / 2);

        var channelBins = new long[4][];
        var sums = new long[4];
        var sumSqs = new UInt128[4];
        var mins = new int[4];
        var maxs = new int[4];
        var counts = new long[4];
        for (int i = 0; i < 4; i++)
        {
            channelBins[i] = new long[binCount];
            mins[i] = int.MaxValue;
            maxs[i] = int.MinValue;
        }

        var totalBins = new long[binCount];

        long totalPixels = 4L * blocksX * blocksY;
        int strideBlocks = totalPixels > maxSamples
            ? (int)Math.Ceiling(Math.Sqrt((double)totalPixels / maxSamples))
            : 1;
        bool sampled = strideBlocks > 1;

        // チャネルインデックス: 0=R, 1=Gr, 2=Gb, 3=B
        var parityToChannel = new int[4];
        for (int py = 0; py < 2; py++)
        {
            for (int px = 0; px < 2; px++)
            {
                parityToChannel[py * 2 + px] = BayerHelper.GetChannel(pattern, px, py) switch
                {
                    BayerChannel.R => 0,
                    BayerChannel.Gr => 1,
                    BayerChannel.Gb => 2,
                    BayerChannel.B => 3,
                    _ => 1,
                };
            }
        }

        if (blocksX > 0 && blocksY > 0)
        {
            int rowWidth = blocksX * 2;
            int rowSteps = (blocksY + strideBlocks - 1) / strideBlocks;

            // ブロック行単位で並列化する。チャネル別ビンはスレッドごとに要るので
            // (16bitでは1区画あたり数MB)、区画数をコア数で頭打ちにして確保を抑える
            int partitions = Math.Clamp(Environment.ProcessorCount, 1, rowSteps);
            var partials = new ChannelAccumulator[partitions];
            Parallel.For(
                0,
                partitions,
                new ParallelOptions { CancellationToken = cancellationToken },
                partition =>
                {
                    var local = new ChannelAccumulator(binCount);
                    var rowTop = new ushort[rowWidth];
                    var rowBottom = new ushort[rowWidth];
                    for (int step = partition; step < rowSteps; step += partitions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int y = y0 + (step * strideBlocks * 2);
                        image.CopyRegion(frame, x0, y, rowWidth, 1, rowTop);
                        image.CopyRegion(frame, x0, y + 1, rowWidth, 1, rowBottom);
                        for (int bx = 0; bx < blocksX; bx += strideBlocks)
                        {
                            int xi = bx * 2;
                            local.Add(
                                rowTop[xi] >> shift,
                                parityToChannel[((y & 1) * 2) + ((x0 + xi) & 1)]);
                            local.Add(
                                rowTop[xi + 1] >> shift,
                                parityToChannel[((y & 1) * 2) + ((x0 + xi + 1) & 1)]);
                            local.Add(
                                rowBottom[xi] >> shift,
                                parityToChannel[(((y + 1) & 1) * 2) + ((x0 + xi) & 1)]);
                            local.Add(
                                rowBottom[xi + 1] >> shift,
                                parityToChannel[(((y + 1) & 1) * 2) + ((x0 + xi + 1) & 1)]);
                        }
                    }

                    partials[partition] = local;
                });

            foreach (ChannelAccumulator local in partials)
            {
                local.MergeInto(totalBins, channelBins, sums, sumSqs, mins, maxs, counts);
            }
        }

        // 画像サイズが奇数だと画像境界へのクランプで(x1-x0)や(y1-y0)が奇数になり、
        // 2x2ブロックに入らない最終列/行が黙って統計から抜けるため、別途走査する
        int oddColumnX = ((x1 - x0) & 1) == 1 ? x1 - 1 : -1;
        int oddRowY = ((y1 - y0) & 1) == 1 ? y1 - 1 : -1;
        if (oddColumnX >= 0 && blocksY > 0)
        {
            var pair = new ushort[2];
            for (int by = 0; by < blocksY; by += strideBlocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int y = y0 + by * 2;
                image.CopyRegion(frame, oddColumnX, y, 1, 2, pair);
                Accumulate(pair[0], parityToChannel[(y & 1) * 2 + (oddColumnX & 1)]);
                Accumulate(pair[1], parityToChannel[((y + 1) & 1) * 2 + (oddColumnX & 1)]);
            }
        }

        if (oddRowY >= 0 && blocksX > 0)
        {
            var lastRow = new ushort[blocksX * 2];
            image.CopyRegion(frame, x0, oddRowY, lastRow.Length, 1, lastRow);
            for (int bx = 0; bx < blocksX; bx += strideBlocks)
            {
                int xi = bx * 2;
                Accumulate(lastRow[xi], parityToChannel[(oddRowY & 1) * 2 + ((x0 + xi) & 1)]);
                Accumulate(
                    lastRow[xi + 1], parityToChannel[(oddRowY & 1) * 2 + ((x0 + xi + 1) & 1)]);
            }
        }

        if (oddColumnX >= 0 && oddRowY >= 0)
        {
            var corner = new ushort[1];
            image.CopyRegion(frame, oddColumnX, oddRowY, 1, 1, corner);
            Accumulate(corner[0], parityToChannel[(oddRowY & 1) * 2 + (oddColumnX & 1)]);
        }

        void Accumulate(ushort value, int channel)
        {
            int code = value >> shift;
            totalBins[code]++;
            channelBins[channel][code]++;
            sums[channel] += code;
            sumSqs[channel] += (ulong)((long)code * code);
            if (code < mins[channel])
            {
                mins[channel] = code;
            }

            if (code > maxs[channel])
            {
                maxs[channel] = code;
            }

            counts[channel]++;
        }

        // 全体統計をチャネル合算から求める
        long totalCount = counts.Sum();
        long totalSum = sums.Sum();
        UInt128 totalSumSq = UInt128.Zero;
        foreach (UInt128 channelSumSq in sumSqs)
        {
            totalSumSq += channelSumSq;
        }

        double totalMean = totalCount > 0 ? (double)totalSum / totalCount : 0;
        double totalVar = totalCount > 0 ? (double)totalSumSq / totalCount - totalMean * totalMean : 0;
        var total = new HistogramResult
        {
            Bins = totalBins,
            BitDepth = bitDepth,
            IsSampled = sampled,
            SampleCount = totalCount,
            Statistics = new RegionStatistics(
                totalMean, Math.Sqrt(Math.Max(0, totalVar)),
                totalCount > 0 ? mins.Where((_, i) => counts[i] > 0).Min() : 0,
                totalCount > 0 ? maxs.Where((_, i) => counts[i] > 0).Max() : 0,
                totalCount),
        };

        if (pattern == BayerPattern.None)
        {
            return new ChannelAnalysisResult
            {
                Total = total,
                Channels = Array.Empty<ChannelHistogram>(),
            };
        }

        var channelOrder = new[]
        {
            BayerChannel.R, BayerChannel.Gr, BayerChannel.Gb, BayerChannel.B,
        };
        var channels = new ChannelHistogram[4];
        for (int i = 0; i < 4; i++)
        {
            double mean = counts[i] > 0 ? (double)sums[i] / counts[i] : 0;
            double variance = counts[i] > 0
                ? (double)sumSqs[i] / counts[i] - mean * mean
                : 0;
            channels[i] = new ChannelHistogram
            {
                Channel = channelOrder[i],
                Bins = channelBins[i],
                Statistics = new RegionStatistics(
                    mean, Math.Sqrt(Math.Max(0, variance)),
                    counts[i] > 0 ? mins[i] : 0,
                    counts[i] > 0 ? maxs[i] : 0,
                    counts[i]),
            };
        }

        return new ChannelAnalysisResult { Total = total, Channels = channels };
    }

    /// <summary>
    /// プロファイル配列の統計値(平均/最小/最大/中央値/標準偏差)を計算する。
    /// </summary>
    /// <param name="values">プロファイル値。</param>
    /// <returns>統計値。空配列なら全て0。</returns>
    public static ProfileStatistics ComputeProfileStatistics(ReadOnlySpan<double> values)
    {
        if (values.Length == 0)
        {
            return new ProfileStatistics(0, 0, 0, 0, 0, 0);
        }

        double sum = 0;
        double sumSq = 0;
        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (double v in values)
        {
            sum += v;
            sumSq += v * v;
            if (v < min)
            {
                min = v;
            }

            if (v > max)
            {
                max = v;
            }
        }

        double mean = sum / values.Length;
        double variance = sumSq / values.Length - mean * mean;

        var sorted = values.ToArray();
        Array.Sort(sorted);
        double median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;

        return new ProfileStatistics(
            values.Length, mean, min, max, median, Math.Sqrt(Math.Max(0, variance)));
    }

    /// <summary>
    /// ヒストグラムから解析指標(中央値・最頻値・パーセンタイル・飽和率など)を求める。
    /// </summary>
    /// <param name="histogram">ヒストグラム結果。</param>
    /// <returns>解析指標。</returns>
    public static HistogramMetrics ComputeHistogramMetrics(HistogramResult histogram)
    {
        long[] bins = histogram.Bins;
        long total = histogram.SampleCount;
        if (total <= 0)
        {
            return new HistogramMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        int median = 0;
        int p1 = 0;
        int p99 = bins.Length - 1;
        int mode = 0;
        long modeCount = 0;
        long accumulated = 0;
        bool medianFound = false;
        bool p1Found = false;
        bool p99Found = false;
        long medianTarget = total / 2;
        long p1Target = (long)(total * 0.01);

        // total==1 だと (long)(1*0.99)==0 となり、P1/Medianと違って
        // 最初のビンで即成立して P99 < P1 という矛盾表示になる
        long p99Target = Math.Max(1, (long)(total * 0.99));

        for (int i = 0; i < bins.Length; i++)
        {
            long count = bins[i];
            if (count > modeCount)
            {
                modeCount = count;
                mode = i;
            }

            accumulated += count;
            if (!p1Found && accumulated > p1Target)
            {
                p1 = i;
                p1Found = true;
            }

            if (!medianFound && accumulated > medianTarget)
            {
                median = i;
                medianFound = true;
            }

            if (!p99Found && accumulated >= p99Target)
            {
                p99 = i;
                p99Found = true;
            }
        }

        int maxCode = bins.Length - 1;
        double saturated = 100.0 * bins[maxCode] / total;
        double zero = 100.0 * bins[0] / total;
        RegionStatistics stats = histogram.Statistics;
        double dynamicRange = stats.Sigma > 0
            ? 20 * Math.Log10(maxCode / stats.Sigma)
            : 0;

        return new HistogramMetrics(
            total, stats.Mean, stats.Sigma, stats.Min, stats.Max,
            median, mode, p1, p99, saturated, zero, dynamicRange);
    }

    /// <summary>
    /// 領域の水平射影(各X座標について領域内の行方向平均)を求める。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="roi">対象領域。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>領域幅ぶんの平均raw code配列。</returns>
    public static double[] ComputeHorizontalProjection(
        RawImage image, int frame, RegionOfInterest roi,
        CancellationToken cancellationToken = default)
    {
        roi = roi.Clamp(image.Width, image.Height);
        if (roi.PixelCount == 0)
        {
            return Array.Empty<double>();
        }

        int shift = 16 - image.Format.BitDepth;
        var sums = new double[roi.Width];
        var buffer = new ushort[roi.Width];
        for (int row = 0; row < roi.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image.CopyRegion(frame, roi.X, roi.Y + row, roi.Width, 1, buffer);
            for (int x = 0; x < roi.Width; x++)
            {
                sums[x] += buffer[x] >> shift;
            }
        }

        for (int x = 0; x < sums.Length; x++)
        {
            sums[x] /= roi.Height;
        }

        return sums;
    }

    /// <summary>
    /// 領域の垂直射影(各Y座標について領域内の列方向平均)を求める。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="roi">対象領域。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>領域高さぶんの平均raw code配列。</returns>
    public static double[] ComputeVerticalProjection(
        RawImage image, int frame, RegionOfInterest roi,
        CancellationToken cancellationToken = default)
    {
        roi = roi.Clamp(image.Width, image.Height);
        if (roi.PixelCount == 0)
        {
            return Array.Empty<double>();
        }

        int shift = 16 - image.Format.BitDepth;
        var means = new double[roi.Height];
        var buffer = new ushort[roi.Width];
        for (int row = 0; row < roi.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image.CopyRegion(frame, roi.X, roi.Y + row, roi.Width, 1, buffer);
            double sum = 0;
            for (int x = 0; x < roi.Width; x++)
            {
                sum += buffer[x] >> shift;
            }

            means[row] = sum / roi.Width;
        }

        return means;
    }

    /// <summary>
    /// 領域の水平射影と垂直射影を1回の走査で同時に求める。
    /// </summary>
    /// <remarks>
    /// 別々に呼ぶと同じ領域を2回読むことになる。全面ROIの10億画素では
    /// 2GBを2度読み直すため、まとめて1パスにする。
    /// </remarks>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="roi">対象領域。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>水平射影(領域幅ぶん)と垂直射影(領域高さぶん)。</returns>
    public static (double[] Horizontal, double[] Vertical) ComputeProjections(
        RawImage image, int frame, RegionOfInterest roi,
        CancellationToken cancellationToken = default)
    {
        roi = roi.Clamp(image.Width, image.Height);
        if (roi.PixelCount == 0)
        {
            return (Array.Empty<double>(), Array.Empty<double>());
        }

        int shift = 16 - image.Format.BitDepth;
        var columnSums = new double[roi.Width];
        var rowMeans = new double[roi.Height];
        var buffer = new ushort[roi.Width];
        for (int row = 0; row < roi.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image.CopyRegion(frame, roi.X, roi.Y + row, roi.Width, 1, buffer);
            double rowSum = 0;
            for (int x = 0; x < roi.Width; x++)
            {
                int code = buffer[x] >> shift;
                columnSums[x] += code;
                rowSum += code;
            }

            rowMeans[row] = rowSum / roi.Width;
        }

        for (int x = 0; x < columnSums.Length; x++)
        {
            columnSums[x] /= roi.Height;
        }

        return (columnSums, rowMeans);
    }

    /// <summary>
    /// 水平ラインプロファイル(指定行のraw code列)を抽出する。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="y">行番号。</param>
    /// <returns>幅ぶんのraw code配列。</returns>
    public static ushort[] ExtractRowProfile(RawImage image, int frame, int y)
    {
        int shift = 16 - image.Format.BitDepth;
        var values = new ushort[image.Width];
        image.CopyRegion(frame, 0, y, image.Width, 1, values);
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (ushort)(values[i] >> shift);
        }

        return values;
    }

    /// <summary>
    /// チャネル別集計のスレッドローカル蓄積。
    /// </summary>
    /// <remarks>
    /// ロックなしで加算し、走査後に一度だけ合算する。ビン配列はチャネル分
    /// (16bitでは4×64Ki要素)あるため、区画ごとに1個だけ作る。
    /// </remarks>
    private sealed class ChannelAccumulator
    {
        internal readonly long[] TotalBins;
        internal readonly long[][] ChannelBins = new long[4][];
        internal readonly long[] Sums = new long[4];
        internal readonly UInt128[] SumSqs = new UInt128[4];
        internal readonly int[] Mins = new int[4];
        internal readonly int[] Maxs = new int[4];
        internal readonly long[] Counts = new long[4];

        internal ChannelAccumulator(int binCount)
        {
            TotalBins = new long[binCount];
            for (int i = 0; i < 4; i++)
            {
                ChannelBins[i] = new long[binCount];
                Mins[i] = int.MaxValue;
                Maxs[i] = int.MinValue;
            }
        }

        /// <summary>1画素を集計する。</summary>
        /// <param name="code">raw code値(ビット深度に合わせてシフト済み)。</param>
        /// <param name="channel">チャネル番号(0=R, 1=Gr, 2=Gb, 3=B)。</param>
        internal void Add(int code, int channel)
        {
            TotalBins[code]++;
            ChannelBins[channel][code]++;
            Sums[channel] += code;
            SumSqs[channel] += (ulong)((long)code * code);
            if (code < Mins[channel])
            {
                Mins[channel] = code;
            }

            if (code > Maxs[channel])
            {
                Maxs[channel] = code;
            }

            Counts[channel]++;
        }

        /// <summary>集計結果を全体の配列へ合算する。</summary>
        /// <param name="totalBins">全体ヒストグラム。</param>
        /// <param name="channelBins">チャネル別ヒストグラム。</param>
        /// <param name="sums">チャネル別の総和。</param>
        /// <param name="sumSqs">チャネル別の二乗和。</param>
        /// <param name="mins">チャネル別の最小値。</param>
        /// <param name="maxs">チャネル別の最大値。</param>
        /// <param name="counts">チャネル別のサンプル数。</param>
        internal void MergeInto(
            long[] totalBins, long[][] channelBins, long[] sums, UInt128[] sumSqs,
            int[] mins, int[] maxs, long[] counts)
        {
            for (int i = 0; i < TotalBins.Length; i++)
            {
                totalBins[i] += TotalBins[i];
            }

            for (int c = 0; c < 4; c++)
            {
                long[] source = ChannelBins[c];
                long[] destination = channelBins[c];
                for (int i = 0; i < source.Length; i++)
                {
                    destination[i] += source[i];
                }

                sums[c] += Sums[c];
                sumSqs[c] += SumSqs[c];
                counts[c] += Counts[c];
                mins[c] = Math.Min(mins[c], Mins[c]);
                maxs[c] = Math.Max(maxs[c], Maxs[c]);
            }
        }
    }

    /// <summary>
    /// 垂直ラインプロファイル(指定列のraw code列)を抽出する。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="x">列番号。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>高さぶんのraw code配列。</returns>
    public static ushort[] ExtractColumnProfile(
        RawImage image, int frame, int x, CancellationToken cancellationToken = default)
    {
        int shift = 16 - image.Format.BitDepth;
        var values = new ushort[image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            if ((y & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            values[y] = (ushort)(image.GetPixel(x, y, frame) >> shift);
        }

        return values;
    }
}
