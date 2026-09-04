namespace RawAnalyzer.Core;

/// <summary>空間フィルタの種類。</summary>
public enum ImageFilterKind
{
    /// <summary>ボックス平均。</summary>
    Mean,
    /// <summary>ガウシアン平滑化。</summary>
    Gaussian,
    /// <summary>中央値(点状ノイズ除去)。</summary>
    Median,
    /// <summary>ガウシアンを使ったアンシャープマスク。</summary>
    UnsharpMask,
    /// <summary>Sobel勾配の大きさ。1/4スケール、0〜65535に制限。</summary>
    Sobel,
    /// <summary>最小値(収縮)。</summary>
    Minimum,
    /// <summary>最大値(膨張)。</summary>
    Maximum,
}

/// <summary>空間フィルタの設定。同じCFA位置または同じRGB成分だけを処理する。</summary>
/// <param name="Kind">種類。</param>
/// <param name="Radius">半径1〜4。窓は(2r+1)×(2r+1)。Sobelは常に半径1。</param>
/// <param name="Sigma">ガウシアンの標準偏差(0.1〜10、同色格子の画素単位)。</param>
/// <param name="Amount">アンシャープ強度(0〜5)。</param>
public sealed record ImageFilterOptions(
    ImageFilterKind Kind, int Radius = 1, double Sigma = 1.0, double Amount = 1.0)
{
    /// <summary>未定義の種類・非有限値・過大な窓を拒否する。</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        if (Radius is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(Radius), "半径は1〜4です。");
        }

        if (!double.IsFinite(Sigma) || Sigma is < 0.1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(Sigma), "σは0.1〜10の有限値です。");
        }

        if (!double.IsFinite(Amount) || Amount is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(Amount), "強度は0〜5の有限値です。");
        }
    }
}

/// <summary>
/// 16bit空間フィルタ。端は同じチャネルの端画素を複製する。
/// Bayerは2画素刻みでR/Gr/Gb/Bを分離し、RGBは各成分を独立に処理する。
/// 入力は変更せず、16bit・単一フレームの結果を返す。
/// </summary>
public static class ImageFilters
{
    /// <summary>指定したRAWフレームを処理する。</summary>
    /// <param name="source">入力画像。HDRの露光分割は呼び出し側で行う。</param>
    /// <param name="options">設定。</param>
    /// <param name="frame">対象フレーム。</param>
    /// <param name="pattern">省略時は入力のCFA。画面で変更したCFAも指定可能。</param>
    /// <param name="progress">進捗(0〜1)。</param>
    /// <param name="cancellationToken">キャンセル。</param>
    /// <returns>16bitの処理結果。</returns>
    public static RawImage Apply(
        RawImage source, ImageFilterOptions options, int frame = 0, BayerPattern? pattern = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((uint)frame >= (uint)source.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        BayerPattern cfa = pattern ?? source.Format.Bayer;
        PixelProcessing.ValidatePattern(cfa);
        ushort[] pixels = Process(source.Width, source.Height, 1,
            cfa == BayerPattern.None ? 1 : 2, options,
            (y, row) => source.CopyRegion(frame, 0, y, source.Width, 1, row),
            progress, cancellationToken);
        return RawImage.FromPixels(
            PixelProcessing.OutputFormat(source, source.Width, source.Height, cfa), pixels);
    }

    /// <summary>RGBの各成分を独立に処理する。</summary>
    /// <param name="source">入力RGB画像。</param>
    /// <param name="options">設定。</param>
    /// <param name="progress">進捗(0〜1)。</param>
    /// <param name="cancellationToken">キャンセル。</param>
    /// <returns>16bit RGBの処理結果。</returns>
    public static ColorImage Apply(
        ColorImage source, ImageFilterOptions options, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ushort[] pixels = Process(source.Width, source.Height, 3, 1, options,
            (y, row) => source.CopyRow(y, 0, source.Width, row), progress, cancellationToken);
        return ColorImage.FromInterleaved(source.Width, source.Height, 16, pixels);
    }

    private static ushort[] Process(
        int width, int height, int channels, int step, ImageFilterOptions options,
        Action<int, ushort[]> readRow, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ct.ThrowIfCancellationRequested();
        int radius = options.Kind == ImageFilterKind.Sobel ? 1 : options.Radius;
        int size = radius * 2 + 1;
        bool separable = options.Kind is not (ImageFilterKind.Median or ImageFilterKind.Sobel);
        long scratch = (long)width * channels * size * (separable ? 10 : 2) + size * size * 2L;
        int workers = PixelProcessing.WorkerCount(width, height, channels, scratch);
        var output = new ushort[checked(width * height * channels)];
        double[] weights = CreateWeights(radius, options);
        var rowProgress = new PixelProcessing.RowProgress(height, progress);
        Parallel.For(0, height,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = workers },
            () => new RowCache(width, height, channels, step, radius, separable, readRow),
            (index, _, cache) =>
            {
                // Bayerは偶数行→奇数行の順で走査し、同色の行キャッシュを再利用する。
                int evenRows = (height + 1) / 2;
                int y = step == 1 ? index
                    : index < evenRows ? index * 2 : (index - evenRows) * 2 + 1;
                cache.Prepare(y, weights, options.Kind, ct);
                int offset = y * width * channels;
                for (int x = 0; x < width; x++)
                {
                    if ((x & 1023) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    for (int c = 0; c < channels; c++)
                    {
                        int position = x * channels + c;
                        double value;
                        if (separable)
                        {
                            value = options.Kind == ImageFilterKind.Minimum ? ushort.MaxValue : 0;
                            for (int k = 0; k < size; k++)
                            {
                                double v = cache.HorizontalWindow[k][position];
                                value = options.Kind switch
                                {
                                    ImageFilterKind.Minimum => Math.Min(value, v),
                                    ImageFilterKind.Maximum => Math.Max(value, v),
                                    _ => value + weights[k] * v,
                                };
                            }

                            if (options.Kind == ImageFilterKind.UnsharpMask)
                            {
                                double original = cache.Window[radius][position];
                                value = original + options.Amount * (original - value);
                            }
                        }
                        else if (options.Kind == ImageFilterKind.Median)
                        {
                            int n = 0;
                            for (int ky = 0; ky < size; ky++)
                            {
                                for (int dx = -radius; dx <= radius; dx++)
                                {
                                    int sx = Neighbor(x, dx, width, step);
                                    cache.Samples[n++] = cache.Window[ky][sx * channels + c];
                                }
                            }

                            cache.Samples.AsSpan().Sort();
                            value = cache.Samples[n / 2];
                        }
                        else
                        {
                            int left = Neighbor(x, -1, width, step) * channels + c;
                            int right = Neighbor(x, 1, width, step) * channels + c;
                            ushort[] top = cache.Window[0];
                            ushort[] middle = cache.Window[1];
                            ushort[] bottom = cache.Window[2];
                            double gx = top[right] + 2.0 * middle[right] + bottom[right]
                                - top[left] - 2.0 * middle[left] - bottom[left];
                            double gy = bottom[left] + 2.0 * bottom[position] + bottom[right]
                                - top[left] - 2.0 * top[position] - top[right];
                            value = Math.Sqrt(gx * gx + gy * gy) / 4;
                        }

                        output[offset + position] = PixelProcessing.Clamp(value);
                    }
                }

                rowProgress.CompleteRow();
                return cache;
            }, _ => { });
        ct.ThrowIfCancellationRequested();
        progress?.Report(1);
        return output;
    }

    // 物理座標をクランプするのではなく、同じCFA位相の格子内でクランプする。
    private static int Neighbor(int position, int delta, int length, int step)
    {
        int phase = position % step;
        int last = (length - 1 - phase) / step;
        return phase + Math.Clamp(position / step + delta, 0, last) * step;
    }

    private static double[] CreateWeights(int radius, ImageFilterOptions options)
    {
        var weights = new double[radius * 2 + 1];
        double sum = 0;
        for (int dx = -radius; dx <= radius; dx++)
        {
            double weight = options.Kind is ImageFilterKind.Gaussian or ImageFilterKind.UnsharpMask
                ? Math.Exp(-dx * dx / (2 * options.Sigma * options.Sigma)) : 1;
            weights[dx + radius] = weight;
            sum += weight;
        }

        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] /= sum;
        }

        return weights;
    }

    /// <summary>ワーカーごとのリングキャッシュ。画像全体の中間配列は確保しない。</summary>
    private sealed class RowCache
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _channels;
        private readonly int _step;
        private readonly int _radius;
        private readonly bool _separable;
        private readonly Action<int, ushort[]> _readRow;
        private readonly int[] _indices;
        private readonly ushort[][] _rows;
        private readonly double[][] _horizontal;

        internal RowCache(int width, int height, int channels, int step, int radius,
            bool separable, Action<int, ushort[]> readRow)
        {
            _width = width;
            _height = height;
            _channels = channels;
            _step = step;
            _radius = radius;
            _separable = separable;
            _readRow = readRow;
            int size = radius * 2 + 1;
            _indices = Enumerable.Repeat(-1, size).ToArray();
            _rows = new ushort[size][];
            _horizontal = new double[size][];
            Window = new ushort[size][];
            HorizontalWindow = new double[size][];
            Samples = new ushort[size * size];
            for (int i = 0; i < size; i++)
            {
                _rows[i] = new ushort[width * channels];
                _horizontal[i] = separable ? new double[width * channels] : Array.Empty<double>();
            }
        }

        internal ushort[][] Window { get; }
        internal double[][] HorizontalWindow { get; }
        internal ushort[] Samples { get; }

        internal void Prepare(int y, double[] weights, ImageFilterKind kind, CancellationToken ct)
        {
            for (int dy = -_radius; dy <= _radius; dy++)
            {
                ct.ThrowIfCancellationRequested();
                int sy = Neighbor(y, dy, _height, _step);
                int slot = sy / _step % _rows.Length;
                if (_indices[slot] != sy)
                {
                    _readRow(sy, _rows[slot]);
                    _indices[slot] = sy;
                    if (_separable)
                    {
                        FilterHorizontal(_rows[slot], _horizontal[slot], weights, kind, ct);
                    }
                }

                Window[dy + _radius] = _rows[slot];
                HorizontalWindow[dy + _radius] = _horizontal[slot];
            }
        }

        private void FilterHorizontal(ushort[] row, double[] output, double[] weights,
            ImageFilterKind kind, CancellationToken ct)
        {
            for (int x = 0; x < _width; x++)
            {
                if ((x & 1023) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                for (int c = 0; c < _channels; c++)
                {
                    double value = kind == ImageFilterKind.Minimum ? ushort.MaxValue : 0;
                    for (int dx = -_radius; dx <= _radius; dx++)
                    {
                        int sx = Neighbor(x, dx, _width, _step);
                        ushort v = row[sx * _channels + c];
                        value = kind switch
                        {
                            ImageFilterKind.Minimum => Math.Min(value, v),
                            ImageFilterKind.Maximum => Math.Max(value, v),
                            _ => value + weights[dx + _radius] * v,
                        };
                    }

                    output[x * _channels + c] = value;
                }
            }
        }
    }
}
