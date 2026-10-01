namespace RawAnalyzer.Core;

/// <summary>
/// 1つのBayerチャネルだけから成る格子領域(<see cref="ChannelRegion"/>)の解析。
/// チャネル分割表示で選んだROIを、表示されている画素だけで集計するために使う。
/// </summary>
/// <remarks>
/// 値はすべてraw code値域で返す。統計・射影・ノイズの定義は矩形版
/// (<see cref="ImageAnalysis"/> / <see cref="NoiseAnalysis"/>)と揃えてあり、
/// 格子の画素だけを並べた画像を矩形版で解析した結果と一致する。
/// ノイズは、集計した画素から測定値を求める部分を矩形版と共有している(違うのは画素の走査だけ)。
/// 格子は1チャネルなので、Bayerのチャネル別プールは不要(σはそのチャネルのσ)。
/// </remarks>
public static class ChannelRegionAnalysis
{
    /// <summary>
    /// 領域のヒストグラムと統計を計算する。領域画素数が <paramref name="maxSamples"/> を
    /// 超える場合は格子の行・列を等間隔に間引き、IsSampled=true を返す
    /// (<see cref="ImageAnalysis.ComputeHistogram"/> と同じ基準)。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="region">対象領域(画像範囲内であること)。</param>
    /// <param name="maxSamples">間引きへ切り替える画素数閾値。0以下なら常に全画素。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>raw code値域のヒストグラム。</returns>
    /// <exception cref="ArgumentOutOfRangeException">領域が画像の範囲外の場合。</exception>
    public static HistogramResult ComputeHistogram(
        RawImage image,
        int frame,
        ChannelRegion region,
        long maxSamples = ImageAnalysis.DefaultMaxHistogramSamples,
        CancellationToken cancellationToken = default)
    {
        ThrowIfOutside(image, region);
        int bitDepth = image.Format.BitDepth;
        int shift = 16 - bitDepth;
        var bins = new long[1 << bitDepth];
        if (region.PixelCount == 0)
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

        // 格子は単一チャネルなので、刻みが偶数でも特定チャネルへ偏ることはない。
        // ただし格子の行 j は元画像の行 2j なので、刻みがHDR行交互の周期と公約数を持つと特定の段(露光)の
        // 行しか読まない(2段・ライン単位2では格子の行が交互に長秒・短秒になり、刻み2なら長秒だけ)。周期と互いに素にする
        int stride = maxSamples > 0
            ? ImageAnalysis.CoprimeStride(
                (int)Math.Ceiling(Math.Sqrt((double)region.PixelCount / maxSamples)),
                ImageAnalysis.HdrRowPeriod(image.Format))
            : 1;
        var row = new ushort[SourceSpan(region)];
        long sum = 0;
        UInt128 sumSq = UInt128.Zero;
        int min = int.MaxValue;
        int max = int.MinValue;
        long count = 0;
        for (int j = 0; j < region.Height; j += stride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image.CopyRegion(frame, region.X, region.Y + 2 * j, row.Length, 1, row);
            for (int i = 0; i < region.Width; i += stride)
            {
                int code = row[2 * i] >> shift;
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

        double mean = (double)sum / count;
        double variance = (double)sumSq / count - mean * mean;
        return new HistogramResult
        {
            Bins = bins,
            BitDepth = bitDepth,
            IsSampled = stride > 1,
            SampleCount = count,
            Statistics = new RegionStatistics(
                mean, Math.Sqrt(Math.Max(0, variance)), min, max, count),
        };
    }

    /// <summary>
    /// 領域の水平射影(格子の各列について縦方向の平均)と垂直射影(各行について横方向の平均)を
    /// 1回の走査で求める。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="region">対象領域(画像範囲内であること)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>水平射影(領域幅ぶん)と垂直射影(領域高さぶん)のraw code平均。</returns>
    /// <exception cref="ArgumentOutOfRangeException">領域が画像の範囲外の場合。</exception>
    public static (double[] Horizontal, double[] Vertical) ComputeProjections(
        RawImage image, int frame, ChannelRegion region,
        CancellationToken cancellationToken = default)
    {
        ThrowIfOutside(image, region);
        if (region.PixelCount == 0)
        {
            return (Array.Empty<double>(), Array.Empty<double>());
        }

        int shift = 16 - image.Format.BitDepth;
        var columnSums = new double[region.Width];
        var rowMeans = new double[region.Height];
        var row = new ushort[SourceSpan(region)];
        for (int j = 0; j < region.Height; j++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image.CopyRegion(frame, region.X, region.Y + 2 * j, row.Length, 1, row);
            double rowSum = 0;
            for (int i = 0; i < region.Width; i++)
            {
                int code = row[2 * i] >> shift;
                columnSums[i] += code;
                rowSum += code;
            }

            rowMeans[j] = rowSum / region.Width;
        }

        for (int i = 0; i < columnSums.Length; i++)
        {
            columnSums[i] /= region.Height;
        }

        return (columnSums, rowMeans);
    }

    /// <summary>
    /// 単一フレームの領域からノイズを測定する(時間ノイズとFPNは分離できない)。
    /// 定義は <see cref="NoiseAnalysis.MeasureSingle"/> と同じ。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="region">評価領域(画像範囲内であること)。</param>
    /// <param name="saturationCode">飽和信号レベル(raw code)。0以下ならビット深度の最大値。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>測定結果(SigmaTemporal/SigmaFpnはNaN)。</returns>
    /// <exception cref="ArgumentOutOfRangeException">領域が画像の範囲外の場合。</exception>
    public static NoiseMeasurement MeasureSingle(
        RawImage image,
        int frame,
        ChannelRegion region,
        double saturationCode = 0,
        CancellationToken cancellationToken = default)
    {
        ThrowIfOutside(image, region);
        (NoiseMoments values, _) = Accumulate(image, frame, null, 0, region, cancellationToken);

        // 格子は1チャネルなので、空間統計は1つの集合(矩形版のBayerなしと同じ扱い)
        return NoiseDefinition.Evaluate(
            new[] { values }, difference: null, saturationCode, image.Format.BitDepth);
    }

    /// <summary>
    /// 同一条件の2枚から、領域内の時間ノイズ(σ(A−B)/√2)とFPNを分離して測定する。
    /// 空間統計と差分は同じ画素集合で集計する。定義は <see cref="NoiseAnalysis.MeasurePair"/> と同じ。
    /// </summary>
    /// <param name="imageA">1枚目(この画像の統計をσ_totalとする)。</param>
    /// <param name="imageB">2枚目(同一サイズ・同一ビット深度)。</param>
    /// <param name="frameA">1枚目のフレーム番号。</param>
    /// <param name="frameB">2枚目のフレーム番号。</param>
    /// <param name="region">評価領域(画像範囲内であること)。</param>
    /// <param name="saturationCode">飽和信号レベル(raw code)。0以下ならビット深度の最大値。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>測定結果。</returns>
    /// <exception cref="ArgumentException">サイズまたはビット深度が一致しない場合。</exception>
    /// <exception cref="ArgumentOutOfRangeException">領域が画像の範囲外の場合。</exception>
    public static NoiseMeasurement MeasurePair(
        RawImage imageA,
        RawImage imageB,
        int frameA,
        int frameB,
        ChannelRegion region,
        double saturationCode = 0,
        CancellationToken cancellationToken = default)
    {
        if (imageA.Width != imageB.Width || imageA.Height != imageB.Height)
        {
            throw new ArgumentException(
                $"サイズが一致しません: {imageA.Width}×{imageA.Height} と " +
                $"{imageB.Width}×{imageB.Height}", nameof(imageB));
        }

        if (imageA.Format.BitDepth != imageB.Format.BitDepth)
        {
            throw new ArgumentException(
                $"ビット深度が一致しません: {imageA.Format.BitDepth}bit と " +
                $"{imageB.Format.BitDepth}bit。同じビット深度の2枚を指定してください。",
                nameof(imageB));
        }

        ThrowIfOutside(imageA, region);
        (NoiseMoments values, NoiseMoments difference) =
            Accumulate(imageA, frameA, imageB, frameB, region, cancellationToken);
        return NoiseDefinition.Evaluate(
            new[] { values }, difference, saturationCode, imageA.Format.BitDepth);
    }

    /// <summary>格子1行ぶんを元画像から読むときの幅(両端の格子点を含む)。</summary>
    private static int SourceSpan(ChannelRegion region) => 2 * region.Width - 1;

    private static void ThrowIfOutside(RawImage image, ChannelRegion region)
    {
        if (!region.IsWithin(image.Width, image.Height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(region), region,
                $"領域が画像({image.Width}×{image.Height})の範囲外です。");
        }
    }

    /// <summary>
    /// 領域内のAのモーメントと、Bがあれば差分A−Bのモーメントを1回の走査で集める
    /// (測定値への換算は <see cref="NoiseDefinition"/> が矩形版と共通の定義で行う)。
    /// </summary>
    private static (NoiseMoments Values, NoiseMoments Difference) Accumulate(
        RawImage imageA,
        int frameA,
        RawImage? imageB,
        int frameB,
        ChannelRegion region,
        CancellationToken cancellationToken)
    {
        if (region.PixelCount == 0)
        {
            return default;
        }

        int span = SourceSpan(region);
        int shiftA = 16 - imageA.Format.BitDepth;
        int shiftB = imageB is null ? 0 : 16 - imageB.Format.BitDepth;
        object gate = new();
        var total = new Accumulator(0);

        Parallel.For(
            0,
            region.Height,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => new Accumulator(span),
            (j, _, local) =>
            {
                int y = region.Y + 2 * j;
                imageA.CopyRegion(frameA, region.X, y, span, 1, local.RowA);
                imageB?.CopyRegion(frameB, region.X, y, span, 1, local.RowB);
                for (int i = 0; i < region.Width; i++)
                {
                    int a = local.RowA[2 * i] >> shiftA;
                    local.Values.Add(a);
                    if (imageB is not null)
                    {
                        local.Difference.Add(a - (local.RowB[2 * i] >> shiftB));
                    }
                }

                return local;
            },
            local =>
            {
                lock (gate)
                {
                    total.Values.Add(local.Values);
                    total.Difference.Add(local.Difference);
                }
            });

        cancellationToken.ThrowIfCancellationRequested();
        return (total.Values, total.Difference);
    }

    /// <summary>並列走査のスレッドローカル集計。</summary>
    private sealed class Accumulator(int span)
    {
        public readonly ushort[] RowA = new ushort[span];
        public readonly ushort[] RowB = new ushort[span];
        public NoiseMoments Values;
        public NoiseMoments Difference;
    }
}
