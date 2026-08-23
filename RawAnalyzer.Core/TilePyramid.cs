namespace RawAnalyzer.Core;

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

    /// <summary>
    /// 連鎖縮小のために前段のブロック合計を保持する上限画素数。
    /// </summary>
    /// <remarks>
    /// 合計は画素あたり4バイト。これを超えるレベルでは保持せず、次のレベルは
    /// 元画像から作り直す。高いfactorほどレベルは小さくなるので、
    /// 繰り返しのフル読み込みが問題になる範囲は連鎖でまかなえる。
    /// </remarks>
    private const long MaxChainSumPixels = 32_000_000;

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

        // 前段の「ブロック合計」。平均値ではなく合計から連鎖することで、
        // 端の半端なブロック(画素数が factor 未満)も正しい重みで足し合わせられ、
        // 元画像を直接ブロック平均した値と完全に一致する
        uint[]? previousSums = null;
        for (int factor = 2; factor <= MaxFactor; factor *= 2)
        {
            int width = (image.Width + factor - 1) / factor;
            int height = (image.Height + factor - 1) / factor;
            if ((long)width * height > maxLevelPixels)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 合計の保持は画素数ぶんの uint 配列。大きすぎるレベルでは持たず、
            // 次のレベルは元画像から作り直す(メモリと再読込のつり合い)
            bool keepSums = (long)width * height <= MaxChainSumPixels;
            bool canChain = previous is not null && previousSums is not null
                && previous.Factor * 2 == factor;

            (PyramidLevel level, uint[]? sums) = canChain
                ? DownsampleChained(
                    previous!, previousSums!, image.Width, image.Height, factor, keepSums,
                    cancellationToken)
                : DownsampleDirect(image, frame, factor, keepSums, cancellationToken);

            levels.Add(level);
            previous = level;
            previousSums = sums;
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

    /// <summary>元画像を直接ブロック平均して1レベル作る。</summary>
    /// <param name="image">元画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="factor">縮小率。</param>
    /// <param name="keepSums">ブロック合計も返すか(次レベルの連鎖用)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>生成したレベルと、要求された場合のブロック合計。</returns>
    private static (PyramidLevel Level, uint[]? Sums) DownsampleDirect(
        RawImage image, int frame, int factor, bool keepSums,
        CancellationToken cancellationToken)
    {
        int sourceWidth = image.Width;
        int sourceHeight = image.Height;
        int width = (sourceWidth + factor - 1) / factor;
        int height = (sourceHeight + factor - 1) / factor;
        var pixels = new ushort[(long)width * height];
        uint[]? sums = keepSums ? new uint[(long)width * height] : null;

        Parallel.For(
            0,
            height,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => (Row: new ushort[sourceWidth], Accumulator: new uint[width]),
            (destY, _, buffers) =>
            {
                Array.Clear(buffers.Accumulator);
                int rows = Math.Min(factor, sourceHeight - (destY * factor));
                for (int r = 0; r < rows; r++)
                {
                    image.CopyRegion(
                        frame, 0, (destY * factor) + r, sourceWidth, 1, buffers.Row);

                    // ブロックごとに区切って足す(画素ごとの除算 sx / factor を避ける)
                    for (int destX = 0; destX < width; destX++)
                    {
                        int start = destX * factor;
                        int cols = Math.Min(factor, sourceWidth - start);
                        uint block = 0;
                        for (int i = 0; i < cols; i++)
                        {
                            block += buffers.Row[start + i];
                        }

                        buffers.Accumulator[destX] += block;
                    }
                }

                long rowOffset = (long)destY * width;
                for (int destX = 0; destX < width; destX++)
                {
                    int cols = Math.Min(factor, sourceWidth - (destX * factor));
                    uint sum = buffers.Accumulator[destX];
                    pixels[rowOffset + destX] = (ushort)(sum / (uint)(cols * rows));
                    if (sums is not null)
                    {
                        sums[rowOffset + destX] = sum;
                    }
                }

                return buffers;
            },
            _ => { });

        cancellationToken.ThrowIfCancellationRequested();
        return (new PyramidLevel(factor, width, height, pixels), sums);
    }

    /// <summary>
    /// 前段レベルのブロック合計から1レベル作る(元画像は読まない)。
    /// </summary>
    /// <remarks>
    /// 合計を足し合わせてから実画素数で割るので、端の半端なブロックがあっても
    /// 元画像を直接ブロック平均した値と一致する。
    /// </remarks>
    /// <param name="source">前段レベル。</param>
    /// <param name="sourceSums">前段レベルのブロック合計。</param>
    /// <param name="imageWidth">元画像の幅(端ブロックの実画素数の算出に使う)。</param>
    /// <param name="imageHeight">元画像の高さ。</param>
    /// <param name="factor">生成する縮小率。</param>
    /// <param name="keepSums">ブロック合計も返すか。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>生成したレベルと、要求された場合のブロック合計。</returns>
    private static (PyramidLevel Level, uint[]? Sums) DownsampleChained(
        PyramidLevel source,
        uint[] sourceSums,
        int imageWidth,
        int imageHeight,
        int factor,
        bool keepSums,
        CancellationToken cancellationToken)
    {
        int width = (imageWidth + factor - 1) / factor;
        int height = (imageHeight + factor - 1) / factor;
        int sourceFactor = source.Factor;
        int sourceWidth = source.Width;
        int sourceHeight = source.Height;
        var pixels = new ushort[(long)width * height];
        uint[]? sums = keepSums ? new uint[(long)width * height] : null;

        Parallel.For(
            0,
            height,
            new ParallelOptions { CancellationToken = cancellationToken },
            destY =>
            {
                long rowOffset = (long)destY * width;
                int sy1 = Math.Min(sourceHeight, (destY * 2) + 2);
                for (int destX = 0; destX < width; destX++)
                {
                    int sx1 = Math.Min(sourceWidth, (destX * 2) + 2);
                    uint sum = 0;
                    long count = 0;
                    for (int sy = destY * 2; sy < sy1; sy++)
                    {
                        int rows = Math.Min(sourceFactor, imageHeight - (sy * sourceFactor));
                        for (int sx = destX * 2; sx < sx1; sx++)
                        {
                            int cols = Math.Min(sourceFactor, imageWidth - (sx * sourceFactor));
                            sum += sourceSums[((long)sy * sourceWidth) + sx];
                            count += (long)cols * rows;
                        }
                    }

                    pixels[rowOffset + destX] = (ushort)(sum / (uint)count);
                    if (sums is not null)
                    {
                        sums[rowOffset + destX] = sum;
                    }
                }
            });

        cancellationToken.ThrowIfCancellationRequested();
        return (new PyramidLevel(factor, width, height, pixels), sums);
    }
}
