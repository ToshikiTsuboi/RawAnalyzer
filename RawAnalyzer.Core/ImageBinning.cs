namespace RawAnalyzer.Core;

/// <summary>デジタルビニングの集約方法。</summary>
public enum BinningMode
{
    /// <summary>平均。明るさを維持する。</summary>
    Average,

    /// <summary>加算。65535を超える値は飽和する。</summary>
    Sum,
}

/// <summary>
/// 空間ビニング。BayerはR/Gr/Gb/Bの位置を個別に集約し、元のCFA配列を維持する。
/// 結果は丸め誤差を抑えるため正規化16bit・単一フレームで保持する。
/// </summary>
public static class ImageBinning
{
    /// <summary>
    /// 出力寸法と右端・下端の切り捨て量を求める。
    /// Bayerでは2×factorの入力ブロックから2×2の出力CFAを作る。
    /// </summary>
    /// <param name="width">入力幅。</param>
    /// <param name="height">入力高さ。</param>
    /// <param name="factor">各方向の集約数(2〜16)。</param>
    /// <param name="pattern">CFA。モノクロ/RGBはNone。</param>
    /// <returns>出力幅、高さ、右端と下端の除外画素数。</returns>
    public static (int Width, int Height, int CroppedRight, int CroppedBottom) GetDimensions(
        int width, int height, int factor, BayerPattern pattern = BayerPattern.None)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (factor is < 2 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), "ビニング係数は2〜16です。");
        }

        PixelProcessing.ValidatePattern(pattern);
        int step = pattern == BayerPattern.None ? 1 : 2;
        int block = step * factor;
        int outWidth = width / block * step;
        int outHeight = height / block * step;
        if (outWidth == 0 || outHeight == 0)
        {
            throw new ArgumentException($"入力は少なくとも{block}×{block}画素必要です。");
        }

        return (outWidth, outHeight, width % block, height % block);
    }

    /// <summary>モノクロ/Bayer RAWの指定フレームをビニングする。入力は変更しない。</summary>
    /// <param name="source">入力画像。HDRの露光分割は呼び出し側で行う。</param>
    /// <param name="factor">各方向の集約数(2〜16)。</param>
    /// <param name="mode">平均/飽和加算。</param>
    /// <param name="frame">対象フレーム。</param>
    /// <param name="pattern">省略時は入力のCFA。画面で変更したCFAも指定可能。</param>
    /// <param name="progress">進捗(0〜1)。</param>
    /// <param name="cancellationToken">キャンセル。</param>
    /// <returns>16bitの処理結果。</returns>
    public static RawImage Apply(
        RawImage source, int factor, BinningMode mode = BinningMode.Average, int frame = 0,
        BayerPattern? pattern = null, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((uint)frame >= (uint)source.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        BayerPattern cfa = pattern ?? source.Format.Bayer;
        var size = GetDimensions(source.Width, source.Height, factor, cfa);
        ushort[] pixels = Process(source.Width, size.Width, size.Height, 1,
            cfa == BayerPattern.None ? 1 : 2, factor, mode,
            (y, row) => source.CopyRegion(frame, 0, y, size.Width * factor, 1, row),
            progress, cancellationToken);
        return RawImage.FromPixels(PixelProcessing.OutputFormat(source, size.Width, size.Height, cfa), pixels);
    }

    /// <summary>RGBの各成分を独立にビニングする。入力は変更しない。</summary>
    /// <param name="source">入力RGB画像。</param>
    /// <param name="factor">各方向の集約数(2〜16)。</param>
    /// <param name="mode">平均/飽和加算。</param>
    /// <param name="progress">進捗(0〜1)。</param>
    /// <param name="cancellationToken">キャンセル。</param>
    /// <returns>16bit RGBの処理結果。</returns>
    public static ColorImage Apply(
        ColorImage source, int factor, BinningMode mode = BinningMode.Average,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var size = GetDimensions(source.Width, source.Height, factor);
        ushort[] pixels = Process(source.Width, size.Width, size.Height, 3, 1, factor, mode,
            (y, row) => source.CopyRow(y, 0, size.Width * factor, row), progress, cancellationToken);
        return ColorImage.FromInterleaved(size.Width, size.Height, 16, pixels);
    }

    private static ushort[] Process(
        int sourceWidth, int width, int height, int channels, int step, int factor, BinningMode mode,
        Action<int, ushort[]> readRow, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        ct.ThrowIfCancellationRequested();
        int workers = PixelProcessing.WorkerCount(width, height, channels,
            (long)sourceWidth * channels * 2 + (long)width * channels * sizeof(long));
        int usedWidth = checked(width * factor);
        var output = new ushort[checked(width * height * channels)];
        var rowProgress = new PixelProcessing.RowProgress(height, progress);
        Parallel.For(0, height,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = workers },
            () => (Row: new ushort[checked(usedWidth * channels)], Sums: new long[width * channels]),
            (y, _, local) =>
            {
                Array.Clear(local.Sums);
                int firstY = y / step * step * factor + y % step;
                for (int dy = 0; dy < factor; dy++)
                {
                    ct.ThrowIfCancellationRequested();
                    readRow(firstY + dy * step, local.Row);
                    for (int x = 0; x < width; x++)
                    {
                        if ((x & 1023) == 0)
                        {
                            ct.ThrowIfCancellationRequested();
                        }

                        int firstX = x / step * step * factor + x % step;
                        for (int dx = 0; dx < factor; dx++)
                        {
                            int src = (firstX + dx * step) * channels;
                            int dest = x * channels;
                            for (int c = 0; c < channels; c++)
                            {
                                local.Sums[dest + c] += local.Row[src + c];
                            }
                        }
                    }
                }

                int offset = y * width * channels;
                double divisor = mode == BinningMode.Average ? factor * factor : 1;
                for (int i = 0; i < local.Sums.Length; i++)
                {
                    output[offset + i] = PixelProcessing.Clamp(local.Sums[i] / divisor);
                }

                rowProgress.CompleteRow();
                return local;
            }, _ => { });
        ct.ThrowIfCancellationRequested();
        progress?.Report(1);
        return output;
    }
}
