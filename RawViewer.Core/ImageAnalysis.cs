namespace RawViewer.Core;

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
    public required uint[] Bins { get; init; }

    /// <summary>ビット深度。</summary>
    public required int BitDepth { get; init; }

    /// <summary>サンプリング計算されたかどうか(全画素走査でない)。</summary>
    public required bool IsSampled { get; init; }

    /// <summary>実際に集計されたサンプル数。</summary>
    public required long SampleCount { get; init; }

    /// <summary>サンプルに基づく統計値。</summary>
    public required RegionStatistics Statistics { get; init; }
}

/// <summary>Bayerチャネル1つぶんのヒストグラムと統計。</summary>
public sealed class ChannelHistogram
{
    /// <summary>チャネル。</summary>
    public required BayerChannel Channel { get; init; }

    /// <summary>ビン配列(raw code値域、長さ2^BitDepth)。</summary>
    public required uint[] Bins { get; init; }

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
        roi = roi.Clamp(image.Width, image.Height);
        if (roi.PixelCount == 0)
        {
            return new RegionStatistics(0, 0, 0, 0, 0);
        }

        int shift = 16 - image.Format.BitDepth;
        object gate = new();
        long totalSum = 0;
        long totalSumSq = 0;
        int totalMin = int.MaxValue;
        int totalMax = int.MinValue;

        Parallel.For(
            0,
            roi.Height,
            () => (Buffer: new ushort[roi.Width], Sum: 0L, SumSq: 0L, Min: int.MaxValue, Max: int.MinValue),
            (row, _, local) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                image.CopyRegion(frame, roi.X, roi.Y + row, roi.Width, 1, local.Buffer);
                long sum = local.Sum;
                long sumSq = local.SumSq;
                int min = local.Min;
                int max = local.Max;
                foreach (ushort v in local.Buffer)
                {
                    int code = v >> shift;
                    sum += code;
                    sumSq += (long)code * code;
                    if (code < min)
                    {
                        min = code;
                    }

                    if (code > max)
                    {
                        max = code;
                    }
                }

                return (local.Buffer, sum, sumSq, min, max);
            },
            local =>
            {
                lock (gate)
                {
                    totalSum += local.Sum;
                    totalSumSq += local.SumSq;
                    totalMin = Math.Min(totalMin, local.Min);
                    totalMax = Math.Max(totalMax, local.Max);
                }
            });

        cancellationToken.ThrowIfCancellationRequested();
        long count = roi.PixelCount;
        double mean = (double)totalSum / count;
        double variance = (double)totalSumSq / count - mean * mean;
        return new RegionStatistics(
            mean, Math.Sqrt(Math.Max(0, variance)), totalMin, totalMax, count);
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
        var bins = new uint[1 << bitDepth];
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
        bool sampled = stride > 1;

        var buffer = new ushort[roi.Width];
        long sum = 0;
        long sumSq = 0;
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
                sumSq += (long)code * code;
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

        // 2x2ブロック整列(偶数座標開始・偶数サイズ)
        int x0 = roi.X + (roi.X & 1);
        int y0 = roi.Y + (roi.Y & 1);
        int blocksX = Math.Max(0, (roi.X + roi.Width - x0) / 2);
        int blocksY = Math.Max(0, (roi.Y + roi.Height - y0) / 2);

        var channelBins = new uint[4][];
        var sums = new long[4];
        var sumSqs = new long[4];
        var mins = new int[4];
        var maxs = new int[4];
        var counts = new long[4];
        for (int i = 0; i < 4; i++)
        {
            channelBins[i] = new uint[binCount];
            mins[i] = int.MaxValue;
            maxs[i] = int.MinValue;
        }

        var totalBins = new uint[binCount];

        long totalPixels = 4L * blocksX * blocksY;
        int strideBlocks = totalPixels > maxSamples
            ? (int)Math.Ceiling(Math.Sqrt((double)totalPixels / maxSamples))
            : 1;
        bool sampled = strideBlocks > 1;

        // チャネルインデックス: 0=R, 1=Gr, 2=Gb, 3=B
        Span<int> parityToChannel = stackalloc int[4];
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
            var rowTop = new ushort[rowWidth];
            var rowBottom = new ushort[rowWidth];
            for (int by = 0; by < blocksY; by += strideBlocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int y = y0 + by * 2;
                image.CopyRegion(frame, x0, y, rowWidth, 1, rowTop);
                image.CopyRegion(frame, x0, y + 1, rowWidth, 1, rowBottom);
                for (int bx = 0; bx < blocksX; bx += strideBlocks)
                {
                    int xi = bx * 2;
                    Accumulate(rowTop[xi], parityToChannel[(y & 1) * 2 + ((x0 + xi) & 1)]);
                    Accumulate(rowTop[xi + 1], parityToChannel[(y & 1) * 2 + ((x0 + xi + 1) & 1)]);
                    Accumulate(rowBottom[xi], parityToChannel[((y + 1) & 1) * 2 + ((x0 + xi) & 1)]);
                    Accumulate(rowBottom[xi + 1], parityToChannel[((y + 1) & 1) * 2 + ((x0 + xi + 1) & 1)]);
                }
            }
        }

        void Accumulate(ushort value, int channel)
        {
            int code = value >> shift;
            totalBins[code]++;
            channelBins[channel][code]++;
            sums[channel] += code;
            sumSqs[channel] += (long)code * code;
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
        long totalSumSq = sumSqs.Sum();
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
