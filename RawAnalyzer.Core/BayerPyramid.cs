namespace RawAnalyzer.Core;

/// <summary>
/// Bayer位相を保ったまま縮小したピラミッド。
/// </summary>
/// <remarks>
/// <see cref="TilePyramid"/> は近傍4画素をまとめて平均するためBayerモザイクが壊れ、
/// カラー系表示(BayerColor / ColorDevelop / ChannelSplit)には使えない。
/// こちらは同色画素どうしだけを平均するので、各レベルが元と同じBayerパターンを持つ
/// 小さなRaw画像になり、カラー描画経路をそのまま流用できる。
/// </remarks>
public sealed class BayerPyramid : IDisposable
{
    /// <summary>最大縮小率。</summary>
    public const int MaxFactor = 32;

    /// <summary>1レベルあたりの画素数上限の既定値。超過するレベルは生成しない。</summary>
    public const long DefaultMaxLevelPixels = 40_000_000;

    private readonly (int Factor, RawImage Image)[] _levels;
    private bool _disposed;

    private BayerPyramid(int sourceWidth, int sourceHeight, (int, RawImage)[] levels)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        _levels = levels;
    }

    /// <summary>元画像の幅(画素数)。</summary>
    public int SourceWidth { get; }

    /// <summary>元画像の高さ(画素数)。</summary>
    public int SourceHeight { get; }

    /// <summary>生成されたレベル数。</summary>
    public int LevelCount => _levels.Length;

    /// <summary>
    /// ピラミッドをバックグラウンドで生成する。
    /// </summary>
    /// <param name="image">元画像。</param>
    /// <param name="format">Bayerパターンを含むフォーマット記述子。</param>
    /// <param name="frame">対象フレーム番号。</param>
    /// <param name="maxLevelPixels">1レベルあたりの画素数上限。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>生成されたピラミッド。Bayerでない場合はレベル0個。</returns>
    public static Task<BayerPyramid> CreateAsync(
        RawImage image,
        RawFormat format,
        int frame = 0,
        long maxLevelPixels = DefaultMaxLevelPixels,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Create(image, format, frame, maxLevelPixels, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// ピラミッドを同期生成する。
    /// </summary>
    /// <param name="image">元画像。</param>
    /// <param name="format">Bayerパターンを含むフォーマット記述子。</param>
    /// <param name="frame">対象フレーム番号。</param>
    /// <param name="maxLevelPixels">1レベルあたりの画素数上限。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>生成されたピラミッド。Bayerでない場合はレベル0個。</returns>
    /// <exception cref="OperationCanceledException">キャンセルされた場合。</exception>
    public static BayerPyramid Create(
        RawImage image,
        RawFormat format,
        int frame = 0,
        long maxLevelPixels = DefaultMaxLevelPixels,
        CancellationToken cancellationToken = default)
    {
        if (format.Bayer == BayerPattern.None)
        {
            return new BayerPyramid(image.Width, image.Height, Array.Empty<(int, RawImage)>());
        }

        var levels = new List<(int Factor, RawImage Image)>();
        RawFormat levelFormat = format with { FrameCount = 1, Hdr = HdrMode.None };
        RawImage? previous = null;
        int previousFactor = 0;

        for (int factor = 2; factor <= MaxFactor; factor *= 2)
        {
            // 出力は2x2ブロック単位。ブロック数が0になったら打ち切る
            int width = image.Width / (2 * factor) * 2;
            int height = image.Height / (2 * factor) * 2;
            if (width < 2 || height < 2)
            {
                break;
            }

            if ((long)width * height > maxLevelPixels)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 直前のレベルがあれば1/2ずつ、無ければ元画像から一気に縮小する
            bool fromPrevious = previous is not null && previousFactor * 2 == factor;
            RawImage sourceImage = fromPrevious ? previous! : image;
            int sourceFrame = fromPrevious ? 0 : frame;
            int reduction = fromPrevious ? 2 : factor;

            ushort[] pixels = ReduceSamePhase(
                sourceImage, sourceFrame, reduction, width, height, cancellationToken);
            RawImage level = RawImage.FromPixels(
                levelFormat with { Width = width, Height = height }, pixels);
            levels.Add((factor, level));
            previous = level;
            previousFactor = factor;
        }

        return new BayerPyramid(image.Width, image.Height, levels.ToArray());
    }

    /// <summary>
    /// ズーム率に最適な縮小率を返す。生成済みレベルのうち 1/zoom を超えない最大の縮小率。
    /// </summary>
    /// <param name="zoom">ズーム率(表示px / 元画像px)。</param>
    /// <returns>縮小率(1は元画像を意味する)。</returns>
    public int SelectFactor(double zoom)
    {
        if (zoom >= 1.0 || zoom <= 0)
        {
            return 1;
        }

        double inverse = 1.0 / zoom;
        int best = 1;
        foreach ((int factor, _) in _levels)
        {
            if (factor <= inverse)
            {
                best = factor;
            }
        }

        return best;
    }

    /// <summary>
    /// 指定縮小率のレベル画像を取得する。
    /// </summary>
    /// <param name="factor">縮小率。</param>
    /// <returns>該当レベルの画像。存在しない場合はnull。</returns>
    public RawImage? GetLevel(int factor)
    {
        foreach ((int levelFactor, RawImage levelImage) in _levels)
        {
            if (levelFactor == factor)
            {
                return levelImage;
            }
        }

        return null;
    }

    /// <summary>レベル画像を解放する。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach ((_, RawImage level) in _levels)
        {
            level.Dispose();
        }
    }

    /// <summary>
    /// 同色画素だけを平均して 1/reduction に縮小する。
    /// 出力の (2bx+px, 2by+py) は、入力の 2*(bx*r+i)+px / 2*(by*r+j)+py (i,j∈[0,r)) の平均。
    /// </summary>
    private static ushort[] ReduceSamePhase(
        RawImage source,
        int frame,
        int reduction,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var pixels = new ushort[(long)width * height];
        int blockRows = height / 2;
        int blockColumns = width / 2;

        // 使用する元画像の範囲(端数は切り捨て)
        int usableWidth = blockColumns * 2 * reduction;
        int sourceRowsPerBlock = 2 * reduction;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(reduction);
        uint divisor = (uint)reduction * (uint)reduction;

        Parallel.For(
            0,
            blockRows,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => (Row: new ushort[source.Width], Accumulator: new uint[width * 2]),
            (blockY, _, buffers) =>
            {
                Array.Clear(buffers.Accumulator);
                int firstRow = blockY * sourceRowsPerBlock;
                for (int k = 0; k < sourceRowsPerBlock; k++)
                {
                    source.CopyRegion(frame, 0, firstRow + k, source.Width, 1, buffers.Row);

                    // 同じ行位相(偶奇)の出力行へ積む
                    int accBase = (k & 1) * width;
                    for (int sx = 0; sx < usableWidth; sx++)
                    {
                        int destX = (((sx >> 1) >> shift) << 1) | (sx & 1);
                        buffers.Accumulator[accBase + destX] += buffers.Row[sx];
                    }
                }

                for (int py = 0; py < 2; py++)
                {
                    long destOffset = ((long)blockY * 2 + py) * width;
                    int accBase = py * width;
                    for (int destX = 0; destX < width; destX++)
                    {
                        pixels[destOffset + destX] =
                            (ushort)(buffers.Accumulator[accBase + destX] / divisor);
                    }
                }

                return buffers;
            },
            _ => { });

        cancellationToken.ThrowIfCancellationRequested();
        return pixels;
    }
}
