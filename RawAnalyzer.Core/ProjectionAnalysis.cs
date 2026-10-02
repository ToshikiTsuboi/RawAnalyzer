using System.Buffers;

namespace RawAnalyzer.Core;

/// <summary>
/// 射影の向き。EMVA 1288 にならい、結果を並べる軸で呼ぶ(平均する方向で呼ぶ流儀(HALCON など)とは逆になる)。
/// </summary>
[Flags]
public enum ProjectionAxes
{
    /// <summary>求めない。</summary>
    None = 0,

    /// <summary>
    /// 水平射影。各列を縦方向に平均した値を x(列)に沿って並べる。縦筋・列ごとのオフセット(列 FPN)を見る
    /// (EMVA 1288 の horizontal profile)。
    /// </summary>
    Horizontal = 1,

    /// <summary>
    /// 垂直射影。各行を横方向に平均した値を y(行)に沿って並べる。横筋・行ごとのオフセット(行 FPN)、
    /// 縦方向のシェーディングを見る(EMVA 1288 の vertical profile)。
    /// </summary>
    Vertical = 2,

    /// <summary>水平射影と垂直射影の両方(1回の走査で求める)。</summary>
    Both = Horizontal | Vertical,
}

/// <summary>
/// 1方向の射影。位置(水平射影なら列、垂直射影なら行)ごとの平均・最小・最大を raw code 値域で持つ。
/// </summary>
/// <remarks>
/// EMVA 1288 の horizontal / vertical profile と同じく、平均に加えて各位置の画素の最小・最大を持つ。
/// 最小・最大は raw code(整数)だが、平均と同じ扱いで描けるよう double で持つ。
/// </remarks>
public sealed class ProjectionProfile
{
    /// <summary>位置ごとの平均。</summary>
    public required double[] Mean { get; init; }

    /// <summary>位置ごとの最小値(raw code)。</summary>
    public required double[] Min { get; init; }

    /// <summary>位置ごとの最大値(raw code)。</summary>
    public required double[] Max { get; init; }

    /// <summary>各位置で平均した画素数(水平射影なら対象の高さ、垂直射影なら対象の幅)。</summary>
    public required long SamplesPerPosition { get; init; }

    /// <summary>位置の数。</summary>
    public int Length => Mean.Length;

    /// <summary>空の射影(対象の画素がない)。</summary>
    /// <returns>長さ0の射影。</returns>
    public static ProjectionProfile Empty() => new()
    {
        Mean = Array.Empty<double>(),
        Min = Array.Empty<double>(),
        Max = Array.Empty<double>(),
        SamplesPerPosition = 0,
    };
}

/// <summary>射影の計算結果。求めなかった向きは null。</summary>
/// <param name="Horizontal">水平射影(各列の縦方向の平均を x に沿って並べたもの)。</param>
/// <param name="Vertical">垂直射影(各行の横方向の平均を y に沿って並べたもの)。</param>
public sealed record ProjectionResult(ProjectionProfile? Horizontal, ProjectionProfile? Vertical);

/// <summary>
/// 画像の矩形(ROI・画像全体)と、1つの Bayer チャネルの格子(<see cref="ChannelRegion"/>)の射影を求める。
/// </summary>
/// <remarks>
/// <para>
/// 列 FPN・行 FPN を見るための値なので間引かず全画素を読む。10億画素(MemoryMappedFile)の画像全体でも
/// 作業メモリが画像の大きさに比例しないよう、対象を列の帯 × 行の帯のタイルに分けて並列に読み、
/// タイルごとの部分和を合算する(部分和を持つ向きの長さはタイルの大きさで抑える)。
/// 取り消しはタイルの中でも数十行ごとに確かめる。
/// </para>
/// <para>
/// 平均は整数の和を画素数で割って求める(和は long で、10億画素の 16bit でも桁あふれしない)。
/// </para>
/// </remarks>
public static class ProjectionAnalysis
{
    /// <summary>部分和を持つ向きのタイルの最大の長さ(列の帯の幅・行の帯の高さ)。</summary>
    internal const int MaxPartialLength = 4096;

    /// <summary>1タイルの目安の画素数(取り消しの間隔と並列の粒度)。</summary>
    internal const long TargetTilePixels = 1 << 20;

    /// <summary>
    /// 矩形領域の射影を求める。領域は画像の範囲へクランプする(交差がなければ長さ0の射影)。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="roi">対象の矩形(ROI、画像全体なら (0, 0, 幅, 高さ))。</param>
    /// <param name="axes">求める向き。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <param name="progress">進み具合(0〜1。読み終えたタイルの割合。作業スレッドから呼ばれる)。</param>
    /// <returns>求めた向きの射影(求めなかった向きは null)。</returns>
    /// <exception cref="OperationCanceledException">取り消された場合。</exception>
    public static ProjectionResult Compute(
        RawImage image, int frame, RegionOfInterest roi, ProjectionAxes axes,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        RegionOfInterest clamped = roi.Clamp(image.Width, image.Height);
        return ComputeLattice(
            image, frame, new Lattice(clamped.X, clamped.Y, 1, clamped.Width, clamped.Height),
            axes, ChooseTiling(clamped.Width, clamped.Height), cancellationToken, progress);
    }

    /// <summary>画像全体の射影を求める。</summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="axes">求める向き。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <param name="progress">進み具合(0〜1。読み終えたタイルの割合。作業スレッドから呼ばれる)。</param>
    /// <returns>求めた向きの射影(求めなかった向きは null)。</returns>
    /// <exception cref="OperationCanceledException">取り消された場合。</exception>
    public static ProjectionResult ComputeWholeImage(
        RawImage image, int frame, ProjectionAxes axes, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        return Compute(
            image, frame, new RegionOfInterest(0, 0, image.Width, image.Height), axes, cancellationToken, progress);
    }

    /// <summary>
    /// 1つの Bayer チャネルの格子の射影を求める。位置は格子の列・行(元画像では2画素おき)で、
    /// 格子の画素(そのチャネルの画素)だけを平均する。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="region">対象の格子(画像の範囲内であること)。</param>
    /// <param name="axes">求める向き。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <param name="progress">進み具合(0〜1。読み終えたタイルの割合。作業スレッドから呼ばれる)。</param>
    /// <returns>求めた向きの射影(求めなかった向きは null)。</returns>
    /// <exception cref="ArgumentOutOfRangeException">格子が画像の範囲外の場合。</exception>
    /// <exception cref="OperationCanceledException">取り消された場合。</exception>
    public static ProjectionResult Compute(
        RawImage image, int frame, ChannelRegion region, ProjectionAxes axes,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        if (!region.IsWithin(image.Width, image.Height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(region), region, $"領域が画像({image.Width}×{image.Height})の範囲外です。");
        }

        int width = Math.Max(0, region.Width);
        int height = Math.Max(0, region.Height);
        return ComputeLattice(
            image, frame, new Lattice(region.X, region.Y, 2, width, height),
            axes, ChooseTiling(width, height), cancellationToken, progress);
    }

    /// <summary>
    /// 対象の大きさからタイルの大きさを決める。部分和を持つ向き(列の帯が2本以上なら行、行の帯が2本以上なら列)の
    /// 長さを <see cref="MaxPartialLength"/> 以下に抑え、1タイルをおよそ <see cref="TargetTilePixels"/> 画素にする。
    /// </summary>
    /// <param name="width">対象の幅(格子なら格子の列数)。</param>
    /// <param name="height">対象の高さ(格子なら格子の行数)。</param>
    /// <returns>タイルの大きさ。</returns>
    internal static ProjectionTiling ChooseTiling(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return new ProjectionTiling(1, 1);
        }

        int stripWidth = Math.Min(width, MaxPartialLength);

        // 行の少ない横長の対象は、列の帯を広げて1本の帯で全行を読む(行の帯が1本なら列は直接書けるので
        // 列の部分和は要らない。帯を細かくするとタイルが小さくなりすぎる)
        if (width > MaxPartialLength && (long)MaxPartialLength * height < TargetTilePixels)
        {
            stripWidth = (int)Math.Min(width, TargetTilePixels / height);
        }

        int bandHeight = (int)Math.Clamp(TargetTilePixels / stripWidth, 1, height);
        return new ProjectionTiling(stripWidth, bandHeight);
    }

    /// <summary>
    /// 格子(矩形は刻み1)の射影を、指定したタイルの大きさで求める(タイルの大きさを変えて検証するため internal)。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="lattice">対象の格子(画像の範囲内であること)。</param>
    /// <param name="axes">求める向き。</param>
    /// <param name="tiling">タイルの大きさ。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <param name="progress">進み具合(読み終えたタイルの割合)。</param>
    /// <returns>求めた向きの射影。</returns>
    internal static ProjectionResult ComputeLattice(
        RawImage image, int frame, Lattice lattice, ProjectionAxes axes, ProjectionTiling tiling,
        CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        bool horizontal = (axes & ProjectionAxes.Horizontal) != 0;
        bool vertical = (axes & ProjectionAxes.Vertical) != 0;
        cancellationToken.ThrowIfCancellationRequested();
        if (lattice.Width == 0 || lattice.Height == 0)
        {
            return new ProjectionResult(
                horizontal ? ProjectionProfile.Empty() : null, vertical ? ProjectionProfile.Empty() : null);
        }

        if (!horizontal && !vertical)
        {
            return new ProjectionResult(null, null);
        }

        var columns = horizontal ? new Accumulator(lattice.Width) : null;
        var rows = vertical ? new Accumulator(lattice.Height) : null;
        int stripWidth = Math.Clamp(tiling.StripWidth, 1, lattice.Width);
        int bandHeight = Math.Clamp(tiling.BandHeight, 1, lattice.Height);
        int strips = (lattice.Width + stripWidth - 1) / stripWidth;
        int bands = (lattice.Height + bandHeight - 1) / bandHeight;
        long tileCount = (long)strips * bands;
        if (tileCount > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(tiling), tiling, "タイルが多すぎます。");
        }

        int shift = 16 - image.Format.BitDepth;
        object gate = new();
        int finished = 0;
        Parallel.For(
            0,
            (int)tileCount,
            new ParallelOptions { CancellationToken = cancellationToken },
            tile =>
            {
                int i0 = tile % strips * stripWidth;
                int j0 = tile / strips * bandHeight;
                var area = new Tile(
                    i0, j0, Math.Min(stripWidth, lattice.Width - i0), Math.Min(bandHeight, lattice.Height - j0));

                // 帯が1本の向きは、その位置を読むタイルが1つだけなので結果へ直接書く
                AccumulateTile(
                    image, frame, lattice, area, shift, columns, directColumns: bands == 1,
                    rows, directRows: strips == 1, gate, cancellationToken);
                progress?.Report((double)Interlocked.Increment(ref finished) / tileCount);
            });

        cancellationToken.ThrowIfCancellationRequested();
        return new ProjectionResult(
            columns?.ToProfile(lattice.Height), rows?.ToProfile(lattice.Width));
    }

    /// <summary>1タイルを読み、列・行ごとの和・最小・最大を集める。</summary>
    private static void AccumulateTile(
        RawImage image, int frame, Lattice lattice, Tile area, int shift,
        Accumulator? columns, bool directColumns, Accumulator? rows, bool directRows,
        object gate, CancellationToken cancellationToken)
    {
        int step = lattice.Step;
        int span = (step * (area.Width - 1)) + 1;
        Accumulator? columnPart = columns is null ? null : directColumns ? columns : new Accumulator(area.Width);
        int columnOffset = directColumns ? area.X : 0;
        Accumulator? rowPart = rows is null ? null : directRows ? rows : new Accumulator(area.Height);
        int rowOffset = directRows ? area.Y : 0;
        ushort[] buffer = ArrayPool<ushort>.Shared.Rent(span);
        try
        {
            int sourceX = lattice.X + (step * area.X);
            for (int j = 0; j < area.Height; j++)
            {
                if ((j & 31) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                image.CopyRegion(frame, sourceX, lattice.Y + (step * (area.Y + j)), span, 1, buffer);
                if (columnPart is not null)
                {
                    long[] sums = columnPart.Sums;
                    int[] mins = columnPart.Mins;
                    int[] maxs = columnPart.Maxs;
                    for (int i = 0; i < area.Width; i++)
                    {
                        int code = buffer[i * step] >> shift;
                        int k = columnOffset + i;
                        sums[k] += code;
                        if (code < mins[k])
                        {
                            mins[k] = code;
                        }

                        if (code > maxs[k])
                        {
                            maxs[k] = code;
                        }
                    }
                }

                if (rowPart is not null)
                {
                    long sum = 0;
                    int min = int.MaxValue;
                    int max = int.MinValue;
                    for (int i = 0; i < area.Width; i++)
                    {
                        int code = buffer[i * step] >> shift;
                        sum += code;
                        if (code < min)
                        {
                            min = code;
                        }

                        if (code > max)
                        {
                            max = code;
                        }
                    }

                    rowPart.Add(rowOffset + j, sum, min, max);
                }
            }
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(buffer);
        }

        if ((columnPart is not null && !directColumns) || (rowPart is not null && !directRows))
        {
            lock (gate)
            {
                if (!directColumns)
                {
                    columns?.MergeFrom(columnPart!, area.X);
                }

                if (!directRows)
                {
                    rows?.MergeFrom(rowPart!, area.Y);
                }
            }
        }
    }

    /// <summary>
    /// 射影を取る格子。元画像の画素 (X + Step·i, Y + Step·j)(0 ≤ i &lt; Width, 0 ≤ j &lt; Height)の集合。
    /// 矩形は刻み1、1チャネルの格子は刻み2。
    /// </summary>
    /// <param name="X">先頭画素の元画像X座標。</param>
    /// <param name="Y">先頭画素の元画像Y座標。</param>
    /// <param name="Step">刻み(画素)。</param>
    /// <param name="Width">列の数。</param>
    /// <param name="Height">行の数。</param>
    internal readonly record struct Lattice(int X, int Y, int Step, int Width, int Height);

    /// <summary>格子の中のタイル(格子の列・行の番号)。</summary>
    private readonly record struct Tile(int X, int Y, int Width, int Height);

    /// <summary>位置ごとの和・最小・最大。</summary>
    private sealed class Accumulator
    {
        internal readonly long[] Sums;
        internal readonly int[] Mins;
        internal readonly int[] Maxs;

        internal Accumulator(int length)
        {
            Sums = new long[length];
            Mins = new int[length];
            Maxs = new int[length];
            Array.Fill(Mins, int.MaxValue);
            Array.Fill(Maxs, int.MinValue);
        }

        internal void Add(int index, long sum, int min, int max)
        {
            Sums[index] += sum;
            if (min < Mins[index])
            {
                Mins[index] = min;
            }

            if (max > Maxs[index])
            {
                Maxs[index] = max;
            }
        }

        internal void MergeFrom(Accumulator part, int offset)
        {
            for (int i = 0; i < part.Sums.Length; i++)
            {
                Add(offset + i, part.Sums[i], part.Mins[i], part.Maxs[i]);
            }
        }

        internal ProjectionProfile ToProfile(long samplesPerPosition)
        {
            var mean = new double[Sums.Length];
            var min = new double[Sums.Length];
            var max = new double[Sums.Length];
            for (int i = 0; i < mean.Length; i++)
            {
                mean[i] = (double)Sums[i] / samplesPerPosition;
                min[i] = Mins[i];
                max[i] = Maxs[i];
            }

            return new ProjectionProfile
            {
                Mean = mean, Min = min, Max = max, SamplesPerPosition = samplesPerPosition,
            };
        }
    }
}

/// <summary>射影を読むタイルの大きさ(格子の列・行の数)。</summary>
/// <param name="StripWidth">列の帯の幅。</param>
/// <param name="BandHeight">行の帯の高さ。</param>
internal readonly record struct ProjectionTiling(int StripWidth, int BandHeight);
