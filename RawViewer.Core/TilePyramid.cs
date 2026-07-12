namespace RawViewer.Core;

/// <summary>
/// 縮小ピラミッドの1レベル。元画像を1/Factorに平均縮小した画素を保持する。
/// </summary>
public sealed class PyramidLevel
{
    private readonly ushort[] _pixels;

    internal PyramidLevel(int factor, int width, int height, ushort[] pixels)
    {
        Factor = factor;
        Width = width;
        Height = height;
        _pixels = pixels;
    }

    /// <summary>縮小率(2, 4, 8, 16, 32, 64)。</summary>
    public int Factor { get; }

    /// <summary>このレベルの幅(画素数)。</summary>
    public int Width { get; }

    /// <summary>このレベルの高さ(画素数)。</summary>
    public int Height { get; }

    /// <summary>
    /// 指定座標の画素値を取得する。
    /// </summary>
    /// <param name="x">X座標。</param>
    /// <param name="y">Y座標。</param>
    /// <returns>16bitフルスケールの平均縮小画素値。</returns>
    public ushort GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        return _pixels[(long)y * Width + x];
    }

    /// <summary>
    /// 指定行の画素列を取得する(ゼロコピー)。
    /// </summary>
    /// <param name="y">行番号。</param>
    /// <returns>行の画素スパン。</returns>
    public ReadOnlySpan<ushort> GetRow(int y)
    {
        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        return _pixels.AsSpan((int)((long)y * Width), Width);
    }

    /// <summary>
    /// 指定矩形領域の画素値をバッファへコピーする(行優先)。
    /// </summary>
    /// <param name="x">領域左端のX座標。</param>
    /// <param name="y">領域上端のY座標。</param>
    /// <param name="width">領域の幅。</param>
    /// <param name="height">領域の高さ。</param>
    /// <param name="destination">width × height 以上の長さの出力バッファ。</param>
    public void CopyRegion(int x, int y, int width, int height, Span<ushort> destination)
    {
        if (x < 0 || width <= 0 || x + width > Width)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (y < 0 || height <= 0 || y + height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (destination.Length < width * height)
        {
            throw new ArgumentException("出力バッファが領域の画素数より短いです。", nameof(destination));
        }

        for (int row = 0; row < height; row++)
        {
            _pixels.AsSpan((int)(((long)y + row) * Width + x), width)
                .CopyTo(destination.Slice(row * width, width));
        }
    }
}

/// <summary>
/// 1/2〜1/64の平均縮小ピラミッド。ズームアウト表示時に適切なレベルを参照することで
/// 全画素描画を回避する。生成はParallel.Forで並列化される。
/// </summary>
public sealed class TilePyramid
{
    /// <summary>最大縮小率。</summary>
    public const int MaxFactor = 64;

    /// <summary>1レベルあたりの画素数上限の既定値。超過するレベルは生成しない。</summary>
    public const long DefaultMaxLevelPixels = 100_000_000;

    private readonly PyramidLevel[] _levels;

    private TilePyramid(int sourceWidth, int sourceHeight, PyramidLevel[] levels)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        _levels = levels;
    }

    /// <summary>元画像の幅(画素数)。</summary>
    public int SourceWidth { get; }

    /// <summary>元画像の高さ(画素数)。</summary>
    public int SourceHeight { get; }

    /// <summary>生成されたレベルの一覧(縮小率の小さい順)。</summary>
    public IReadOnlyList<PyramidLevel> Levels => _levels;

    /// <summary>
    /// ピラミッドをバックグラウンドで生成する。
    /// </summary>
    /// <param name="image">元画像。</param>
    /// <param name="frame">対象フレーム番号。</param>
    /// <param name="maxLevelPixels">1レベルあたりの画素数上限。超過するレベルはスキップされる。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>生成されたピラミッド。</returns>
    public static Task<TilePyramid> CreateAsync(
        RawImage image,
        int frame = 0,
        long maxLevelPixels = DefaultMaxLevelPixels,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Create(image, frame, maxLevelPixels, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// ピラミッドを同期生成する。
    /// </summary>
    /// <param name="image">元画像。</param>
    /// <param name="frame">対象フレーム番号。</param>
    /// <param name="maxLevelPixels">1レベルあたりの画素数上限。超過するレベルはスキップされる。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>生成されたピラミッド。</returns>
    /// <exception cref="OperationCanceledException">キャンセルされた場合。</exception>
    public static TilePyramid Create(
        RawImage image,
        int frame = 0,
        long maxLevelPixels = DefaultMaxLevelPixels,
        CancellationToken cancellationToken = default)
    {
        var levels = new List<PyramidLevel>();
        PyramidLevel? previous = null;
        for (int factor = 2; factor <= MaxFactor; factor *= 2)
        {
            int width = (image.Width + factor - 1) / factor;
            int height = (image.Height + factor - 1) / factor;
            if ((long)width * height > maxLevelPixels)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            PyramidLevel level;
            if (previous is not null && previous.Factor * 2 == factor)
            {
                PyramidLevel source = previous;
                level = Downsample(
                    source.Width, source.Height, 2, factor,
                    (y, buffer) => source.CopyRegion(0, y, source.Width, 1, buffer),
                    cancellationToken);
            }
            else
            {
                level = Downsample(
                    image.Width, image.Height, factor, factor,
                    (y, buffer) => image.CopyRegion(frame, 0, y, image.Width, 1, buffer),
                    cancellationToken);
            }

            levels.Add(level);
            previous = level;
        }

        return new TilePyramid(image.Width, image.Height, levels.ToArray());
    }

    /// <summary>
    /// ズーム率(表示px / 元画像px)に最適な縮小率を返す。
    /// 生成済みレベルのうち 1/zoom を超えない最大の縮小率を選ぶ。等倍以上は常に1。
    /// </summary>
    /// <param name="zoom">ズーム率。</param>
    /// <returns>縮小率(1は元画像を意味する)。</returns>
    public int SelectFactor(double zoom)
    {
        if (zoom >= 1.0 || zoom <= 0)
        {
            return 1;
        }

        double inverse = 1.0 / zoom;
        int best = 1;
        foreach (PyramidLevel level in _levels)
        {
            if (level.Factor <= inverse)
            {
                best = level.Factor;
            }
        }

        return best;
    }

    /// <summary>
    /// 指定縮小率のレベルを取得する。
    /// </summary>
    /// <param name="factor">縮小率。</param>
    /// <returns>該当レベル。存在しない場合はnull。</returns>
    public PyramidLevel? GetLevel(int factor)
    {
        return _levels.FirstOrDefault(l => l.Factor == factor);
    }

    private static PyramidLevel Downsample(
        int sourceWidth,
        int sourceHeight,
        int blockSize,
        int resultFactor,
        Action<int, ushort[]> readRow,
        CancellationToken cancellationToken)
    {
        int width = (sourceWidth + blockSize - 1) / blockSize;
        int height = (sourceHeight + blockSize - 1) / blockSize;
        var pixels = new ushort[(long)width * height];

        Parallel.For(
            0,
            height,
            () => (Row: new ushort[sourceWidth], Accumulator: new uint[width]),
            (destY, _, buffers) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Array.Clear(buffers.Accumulator);
                int rows = Math.Min(blockSize, sourceHeight - destY * blockSize);
                for (int r = 0; r < rows; r++)
                {
                    readRow(destY * blockSize + r, buffers.Row);
                    for (int sx = 0; sx < sourceWidth; sx++)
                    {
                        buffers.Accumulator[sx / blockSize] += buffers.Row[sx];
                    }
                }

                long rowOffset = (long)destY * width;
                for (int destX = 0; destX < width; destX++)
                {
                    int cols = Math.Min(blockSize, sourceWidth - destX * blockSize);
                    pixels[rowOffset + destX] =
                        (ushort)(buffers.Accumulator[destX] / (uint)(cols * rows));
                }

                return buffers;
            },
            _ => { });

        cancellationToken.ThrowIfCancellationRequested();
        return new PyramidLevel(resultFactor, width, height, pixels);
    }
}
