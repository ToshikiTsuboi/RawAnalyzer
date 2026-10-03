namespace RawAnalyzer.Core;

/// <summary>
/// 縮小ピラミッドの1レベル。元画像を1/Factorに平均縮小した画素を保持する。
/// </summary>
public sealed class PyramidLevel
{
    private readonly ushort[] _pixels;

    internal PyramidLevel(
        int factor, int width, int height, ushort[] pixels, int segmentWidth = 0, int levelSegmentWidth = 0)
    {
        Factor = factor;
        Width = width;
        Height = height;
        _pixels = pixels;
        SegmentWidth = segmentWidth;
        LevelSegmentWidth = levelSegmentWidth;
    }

    /// <summary>縮小率(2, 4, 8, 16, 32, 64)。</summary>
    public int Factor { get; }

    /// <summary>このレベルの幅(画素数)。</summary>
    public int Width { get; }

    /// <summary>このレベルの高さ(画素数)。</summary>
    public int Height { get; }

    /// <summary>
    /// 区画ごとに縮小したレベルの、元画像の区画の幅(<see cref="RawImage.SegmentWidth"/>)。
    /// 区画に分けずに縮小したレベルでは0。
    /// </summary>
    /// <remarks>
    /// 区画ごとに縮小したレベルでは、元画像の区画 s の縮小画素がレベルのX座標 s×<see cref="LevelSegmentWidth"/> から
    /// 並ぶ(区画の右端の端数のブロックも1列。最後の区画は残りの幅のぶん)。元画像の列 x はレベルの列
    /// (x ÷ SegmentWidth)×LevelSegmentWidth + (x mod SegmentWidth) ÷ Factor(いずれも整数の割り算)に写る。
    /// </remarks>
    public int SegmentWidth { get; }

    /// <summary>
    /// 区画1つぶんのレベルの幅(<see cref="SegmentWidth"/> を縮小率で割って切り上げ)。区画に分けないレベルでは0。
    /// </summary>
    public int LevelSegmentWidth { get; }

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
/// <remarks>
/// 並置画像(<see cref="RawImage.SegmentWidth"/>。HDR分割ビューの各露光の段など)は区画ごとに縮小し、ブロックは
/// 区画の境目をまたがない(<see cref="PyramidLevel.SegmentWidth"/>)。並置画像全体を一様に縮小すると、区画の幅が
/// 縮小率の倍数でないとき境目のブロックが両方の区画(露光の違う段)の画素を平均する。
/// </remarks>
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
        LevelColumns? previousColumns = null;

        // 並置画像は区画ごとに縮小する(区画が画像全体なら区画に分けない)
        int segmentWidth = image.SegmentWidth > 0 && image.SegmentWidth < image.Width ? image.SegmentWidth : 0;

        // 前段の「ブロック合計」。平均値ではなく合計から連鎖することで、
        // 端の半端なブロック(画素数が factor 未満)も正しい重みで足し合わせられ、
        // 元画像を直接ブロック平均した値と完全に一致する
        uint[]? previousSums = null;
        for (int factor = 2; factor <= MaxFactor; factor *= 2)
        {
            LevelColumns columns = LevelColumns.Create(image.Width, segmentWidth, factor);
            int width = columns.Count;
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
                    previous!, previousSums!, previousColumns!, columns, image.Height, factor, keepSums,
                    cancellationToken)
                : DownsampleDirect(image, frame, factor, columns, keepSums, cancellationToken);

            levels.Add(level);
            previous = level;
            previousColumns = columns;
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
    /// <param name="columns">このレベルの列が元画像のどの列を平均するか。</param>
    /// <param name="keepSums">ブロック合計も返すか(次レベルの連鎖用)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>生成したレベルと、要求された場合のブロック合計。</returns>
    private static (PyramidLevel Level, uint[]? Sums) DownsampleDirect(
        RawImage image, int frame, int factor, LevelColumns columns, bool keepSums,
        CancellationToken cancellationToken)
    {
        int sourceWidth = image.Width;
        int sourceHeight = image.Height;
        int width = columns.Count;
        int height = (sourceHeight + factor - 1) / factor;
        int[] columnStart = columns.Start;
        int[] columnCount = columns.SourceCount;
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
                        int start = columnStart[destX];
                        int cols = columnCount[destX];
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
                    int cols = columnCount[destX];
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
        return (columns.CreateLevel(factor, height, pixels), sums);
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
    /// <param name="sourceColumns">前段レベルの列が元画像のどの列を平均したか(端ブロックの実画素数に使う)。</param>
    /// <param name="columns">このレベルの列が元画像のどの列を平均するか。</param>
    /// <param name="imageHeight">元画像の高さ。</param>
    /// <param name="factor">生成する縮小率。</param>
    /// <param name="keepSums">ブロック合計も返すか。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>生成したレベルと、要求された場合のブロック合計。</returns>
    private static (PyramidLevel Level, uint[]? Sums) DownsampleChained(
        PyramidLevel source,
        uint[] sourceSums,
        LevelColumns sourceColumns,
        LevelColumns columns,
        int imageHeight,
        int factor,
        bool keepSums,
        CancellationToken cancellationToken)
    {
        int width = columns.Count;
        int height = (imageHeight + factor - 1) / factor;
        int sourceFactor = source.Factor;
        int sourceWidth = source.Width;
        int sourceHeight = source.Height;
        var pixels = new ushort[(long)width * height];
        uint[]? sums = keepSums ? new uint[(long)width * height] : null;

        // このレベルの列が前段レベルのどの列(連続する1〜2列)を合わせるか。区画ごとに縮小したレベルでは、
        // 区画の右端の端数のブロックは前段の1列だけで、隣の区画の列とは合わせない
        (int[] firstSource, int[] sourceCount) = columns.MapFrom(sourceColumns);
        int[] sourceColumnPixels = sourceColumns.SourceCount;

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
                    int sx0 = firstSource[destX];
                    int sx1 = sx0 + sourceCount[destX];
                    uint sum = 0;
                    long count = 0;
                    for (int sy = destY * 2; sy < sy1; sy++)
                    {
                        int rows = Math.Min(sourceFactor, imageHeight - (sy * sourceFactor));
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            int cols = sourceColumnPixels[sx];
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
        return (columns.CreateLevel(factor, height, pixels), sums);
    }

    /// <summary>
    /// 1レベルの列ごとに、元画像のどの列(連続する列)を平均するか。並置画像は区画ごとに区画の左端から縮小率ずつ
    /// 区切り、区画の右端の端数は残りの列だけのブロックにする(区画の境目をまたがない)。
    /// </summary>
    private sealed class LevelColumns
    {
        private LevelColumns(int[] start, int[] sourceCount, int segmentWidth, int levelSegmentWidth)
        {
            Start = start;
            SourceCount = sourceCount;
            SegmentWidth = segmentWidth;
            LevelSegmentWidth = levelSegmentWidth;
        }

        /// <summary>列ごとのブロックの左端(元画像の列)。</summary>
        internal int[] Start { get; }

        /// <summary>列ごとのブロックの幅(元画像の列数)。</summary>
        internal int[] SourceCount { get; }

        /// <summary>レベルの幅(列数)。</summary>
        internal int Count => Start.Length;

        /// <summary>元画像の区画の幅(区画に分けなければ0)。</summary>
        internal int SegmentWidth { get; }

        /// <summary>区画1つぶんのレベルの幅(区画に分けなければ0)。</summary>
        internal int LevelSegmentWidth { get; }

        /// <summary>レベルの列の区切りを作る。</summary>
        /// <param name="imageWidth">元画像の幅。</param>
        /// <param name="segmentWidth">区画の幅(区画に分けなければ0)。</param>
        /// <param name="factor">縮小率。</param>
        /// <returns>列の区切り。</returns>
        internal static LevelColumns Create(int imageWidth, int segmentWidth, int factor)
        {
            // 区画に分けなければ画像全体を1つの区画として同じ規則で区切る(従来の一様な縮小と同じ列になる)
            int segment = segmentWidth > 0 ? segmentWidth : imageWidth;
            int levelSegment = (segment + factor - 1) / factor;
            int segments = (int)(((long)imageWidth + segment - 1) / segment);
            int lastWidth = imageWidth - ((segments - 1) * segment);
            int count = ((segments - 1) * levelSegment) + ((lastWidth + factor - 1) / factor);
            var start = new int[count];
            var sourceCount = new int[count];
            int column = 0;
            for (int left = 0; left < imageWidth; left += segment)
            {
                int right = Math.Min(imageWidth, left + segment);
                for (int x = left; x < right; x += factor)
                {
                    start[column] = x;
                    sourceCount[column] = Math.Min(factor, right - x);
                    column++;
                }
            }

            return segmentWidth > 0
                ? new LevelColumns(start, sourceCount, segmentWidth, levelSegment)
                : new LevelColumns(start, sourceCount, 0, 0);
        }

        /// <summary>
        /// このレベル(前段の2倍の縮小率)の列ごとに、合わせる前段レベルの最初の列と列数を返す。
        /// </summary>
        /// <param name="source">前段レベルの列の区切り。</param>
        /// <returns>列ごとの前段レベルの最初の列と列数(1か2)。</returns>
        internal (int[] First, int[] Count) MapFrom(LevelColumns source)
        {
            var first = new int[Count];
            var count = new int[Count];
            int sx = 0;
            for (int column = 0; column < Count; column++)
            {
                // 前段のブロックはこのレベルのブロックを2つずつに割ったもの(区画の右端では1つのこともある)
                while (source.Start[sx] < Start[column])
                {
                    sx++;
                }

                int end = Start[column] + SourceCount[column];
                int n = 0;
                while (sx + n < source.Count && source.Start[sx + n] < end)
                {
                    n++;
                }

                first[column] = sx;
                count[column] = n;
            }

            return (first, count);
        }

        /// <summary>この列の区切りで縮小したレベルを作る。</summary>
        /// <param name="factor">縮小率。</param>
        /// <param name="height">レベルの高さ。</param>
        /// <param name="pixels">レベルの画素。</param>
        /// <returns>レベル。</returns>
        internal PyramidLevel CreateLevel(int factor, int height, ushort[] pixels)
        {
            return new PyramidLevel(factor, Count, height, pixels, SegmentWidth, LevelSegmentWidth);
        }
    }
}
