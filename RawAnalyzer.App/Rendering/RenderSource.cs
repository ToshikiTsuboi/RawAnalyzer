using System.Buffers;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Rendering;

/// <summary>
/// ビューポート描画の画素供給元(元画像またはピラミッドレベル)の抽象化。
/// </summary>
public abstract class RenderSource
{
    /// <summary>元画像(レベル0)の幅。</summary>
    public abstract int SourceWidth { get; }

    /// <summary>元画像(レベル0)の高さ。</summary>
    public abstract int SourceHeight { get; }

    /// <summary>このソースの縮小率(1=元画像)。</summary>
    public abstract int Factor { get; }

    /// <summary>このソース自身の幅(SourceWidth / Factor 相当)。</summary>
    public abstract int LevelWidth { get; }

    /// <summary>このソース自身の高さ。</summary>
    public abstract int LevelHeight { get; }

    /// <summary>
    /// 画像として描く範囲の幅(ソース座標)。ここから右の列は画像の外として背景にする。
    /// 既定は <see cref="SourceWidth"/>。
    /// </summary>
    /// <remarks>
    /// 縮小レベルをそのレベルの座標のまま描くソース(<see cref="BayerLevelRenderSource"/>)では、元画像の幅を
    /// 縮小率で割った小数になる(元画像の範囲ちょうどまで描く)。
    /// </remarks>
    public virtual double ExtentWidth => SourceWidth;

    /// <summary>画像として描く範囲の高さ(ソース座標)。<see cref="ExtentWidth"/> の縦方向版。</summary>
    public virtual double ExtentHeight => SourceHeight;

    /// <summary>
    /// 同じ画素データを指すソースなら等値になるキー(描画結果のキャッシュ照合用)。
    /// </summary>
    public abstract object CacheKey { get; }

    /// <summary>
    /// 横方向の継ぎ目(ソースX座標)。ここから右の列はレベルX座標を <see cref="SeamLevelX"/> から
    /// 数え直す。継ぎ目のないソースは <see cref="int.MaxValue"/>。
    /// </summary>
    /// <remarks>
    /// 左右を別々に縮小して並べるソース(チャネル分割の縮小表示)用。全体を一様に縮小すると、
    /// 左側の幅が縮小率で割り切れないとき、右側が等倍での位置より手前から始まってしまう。
    /// </remarks>
    public virtual int SeamX => int.MaxValue;

    /// <summary><see cref="SeamX"/> の位置に対応するレベルX座標。</summary>
    public virtual int SeamLevelX => 0;

    /// <summary>縦方向の継ぎ目(ソースY座標)。<see cref="SeamX"/> の縦方向版。</summary>
    public virtual int SeamY => int.MaxValue;

    /// <summary><see cref="SeamY"/> の位置に対応するレベルY座標。</summary>
    public virtual int SeamLevelY => 0;

    /// <summary>
    /// 左から同じ幅で並置した区画を区画ごとに縮小したソース(HDR分割ビューの縮小ピラミッド。
    /// <see cref="PyramidLevel.SegmentWidth"/>)の、区画の幅(ソース座標)。区画に分けないソースは0。
    /// </summary>
    /// <remarks>
    /// 区画 s のレベルX座標は s×<see cref="LevelSegmentWidth"/> から数え直す。並置画像全体を一様に縮小すると、
    /// 区画の幅が縮小率で割り切れないとき境目のブロックが両方の区画の画素を平均する。
    /// </remarks>
    public virtual int SegmentWidth => 0;

    /// <summary>区画1つぶんのレベルの幅(<see cref="SegmentWidth"/> が0なら0)。</summary>
    public virtual int LevelSegmentWidth => 0;

    /// <summary>
    /// ソースX座標(0以上)を、継ぎ目・区画を考慮してレベルX座標へ写す(範囲へのクランプはしない)。
    /// </summary>
    /// <param name="sourceX">ソースX座標。</param>
    /// <returns>レベルX座標。</returns>
    public int ToLevelX(double sourceX)
    {
        int segmentWidth = SegmentWidth;
        if (segmentWidth > 0)
        {
            // 区画は列の整数除算で決める(描画で段ごとのLUTを選ぶのと同じ)
            int column = (int)sourceX;
            int segment = column / segmentWidth;
            return (segment * LevelSegmentWidth) + ((column - (segment * segmentWidth)) / Factor);
        }

        return ToLevel(sourceX, SeamX, SeamLevelX);
    }

    /// <summary>ソースY座標(0以上)を、継ぎ目を考慮してレベルY座標へ写す(範囲へのクランプはしない)。</summary>
    /// <param name="sourceY">ソースY座標。</param>
    /// <returns>レベルY座標。</returns>
    public int ToLevelY(double sourceY) => ToLevel(sourceY, SeamY, SeamLevelY);

    /// <summary>指定行の一部を読み出す。</summary>
    /// <param name="levelY">レベル座標系の行番号。</param>
    /// <param name="levelX">レベル座標系の開始X。</param>
    /// <param name="count">画素数。</param>
    /// <param name="destination">出力バッファ。</param>
    public abstract void ReadRow(int levelY, int levelX, int count, Span<ushort> destination);

    private int ToLevel(double source, int seam, int seamLevel)
    {
        return source < seam
            ? (int)(source / Factor)
            : seamLevel + (int)((source - seam) / Factor);
    }
}

/// <summary>元画像(RawImage)を等倍で供給するソース。</summary>
public sealed class RawImageRenderSource : RenderSource
{
    private readonly RawImage _image;
    private readonly int _frame;

    /// <summary>ソースを生成する。</summary>
    /// <param name="image">元画像。</param>
    /// <param name="frame">フレーム番号。</param>
    public RawImageRenderSource(RawImage image, int frame)
    {
        _image = image;
        _frame = frame;
    }

    /// <inheritdoc />
    public override int SourceWidth => _image.Width;

    /// <inheritdoc />
    public override int SourceHeight => _image.Height;

    /// <inheritdoc />
    public override int Factor => 1;

    /// <inheritdoc />
    public override int LevelWidth => _image.Width;

    /// <inheritdoc />
    public override int LevelHeight => _image.Height;

    /// <inheritdoc />
    public override object CacheKey => (_image, _frame);

    /// <inheritdoc />
    public override void ReadRow(int levelY, int levelX, int count, Span<ushort> destination)
    {
        _image.CopyRegion(_frame, levelX, levelY, count, 1, destination);
    }
}

/// <summary>
/// Bayer位相を保った縮小レベル(<see cref="BayerPyramid"/> のレベル)を、レベルの座標のまま等倍として
/// 供給するソース(Bayerカラー・カラー現像の縮小描画用。呼び出し側がズームと原点を縮小率で補正する)。
/// </summary>
/// <remarks>
/// レベルは元画像の幅・高さを 2×縮小率 で割った端数を切り捨てているので、レベルをそのまま描くと元画像の
/// 右端・下端の端数(最大 2×縮小率−1 画素)が背景になり、元画像の座標で描くROIなどとずれる。
/// 描く範囲は元画像の範囲(元画像の寸法÷縮小率)とし、レベルにない端数の列・行は同じBayer位相の
/// 最終列・最終行で埋める(チャネル分割の縮小表示と同じ埋め方)。
/// </remarks>
public sealed class BayerLevelRenderSource : RenderSource
{
    private readonly RawImage _level;

    /// <summary>ソースを生成する。</summary>
    /// <param name="level">縮小レベル(幅・高さは2以上の偶数、フレームは1枚)。</param>
    /// <param name="factor">縮小率(1以上)。</param>
    /// <param name="imageWidth">元画像の幅。</param>
    /// <param name="imageHeight">元画像の高さ。</param>
    public BayerLevelRenderSource(RawImage level, int factor, int imageWidth, int imageHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
        _level = level;
        ExtentWidth = (double)imageWidth / factor;
        ExtentHeight = (double)imageHeight / factor;

        // 端数の列・行を含めるよう切り上げる(レベルより大きい分は ReadRow が埋める)
        SourceWidth = Math.Max(level.Width, (imageWidth + factor - 1) / factor);
        SourceHeight = Math.Max(level.Height, (imageHeight + factor - 1) / factor);
    }

    /// <inheritdoc />
    public override int SourceWidth { get; }

    /// <inheritdoc />
    public override int SourceHeight { get; }

    /// <inheritdoc />
    public override int Factor => 1;

    /// <inheritdoc />
    public override int LevelWidth => SourceWidth;

    /// <inheritdoc />
    public override int LevelHeight => SourceHeight;

    /// <inheritdoc />
    public override double ExtentWidth { get; }

    /// <inheritdoc />
    public override double ExtentHeight { get; }

    /// <inheritdoc />
    public override object CacheKey => (_level, nameof(BayerLevelRenderSource));

    /// <inheritdoc />
    public override void ReadRow(int levelY, int levelX, int count, Span<ushort> destination)
    {
        int y = ToLevel(levelY, _level.Height);
        int inside = Math.Clamp(_level.Width - levelX, 0, count);
        if (inside > 0)
        {
            _level.CopyRegion(0, levelX, y, inside, 1, destination[..inside]);
        }

        if (inside == count)
        {
            return;
        }

        // レベルの右にある端数の列は、同じ位相の最終列(偶数列なら最後から2列目、奇数列なら最終列)で埋める
        Span<ushort> lastPair = stackalloc ushort[2];
        _level.CopyRegion(0, _level.Width - 2, y, 2, 1, lastPair);
        for (int i = inside; i < count; i++)
        {
            destination[i] = lastPair[(levelX + i) & 1];
        }
    }

    /// <summary>レベルの外の座標を、同じ位相(偶奇)の最終の座標へ写す。</summary>
    private static int ToLevel(int coordinate, int length)
    {
        return coordinate < length ? coordinate : length - 2 + (coordinate & 1);
    }
}

/// <summary>ピラミッドレベルを供給するソース。</summary>
public sealed class PyramidLevelRenderSource : RenderSource
{
    private readonly PyramidLevel _level;

    /// <summary>ソースを生成する。</summary>
    /// <param name="level">ピラミッドレベル。</param>
    /// <param name="sourceWidth">元画像の幅。</param>
    /// <param name="sourceHeight">元画像の高さ。</param>
    public PyramidLevelRenderSource(PyramidLevel level, int sourceWidth, int sourceHeight)
    {
        _level = level;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    /// <inheritdoc />
    public override int SourceWidth { get; }

    /// <inheritdoc />
    public override int SourceHeight { get; }

    /// <inheritdoc />
    public override int Factor => _level.Factor;

    /// <inheritdoc />
    public override int LevelWidth => _level.Width;

    /// <inheritdoc />
    public override int LevelHeight => _level.Height;

    /// <inheritdoc />
    public override object CacheKey => _level;

    /// <inheritdoc />
    public override int SegmentWidth => _level.SegmentWidth;

    /// <inheritdoc />
    public override int LevelSegmentWidth => _level.LevelSegmentWidth;

    /// <inheritdoc />
    public override void ReadRow(int levelY, int levelX, int count, Span<ushort> destination)
    {
        _level.GetRow(levelY).Slice(levelX, count).CopyTo(destination);
    }
}

/// <summary>
/// R/Gr/Gb/Bの2x2タイル並置表示用に座標を写像するソース。
/// タイル画像を実体化せず、行読み出し時に元画像(またはBayer縮小レベル)からストライド抽出する。
/// </summary>
/// <remarks>
/// ソース座標は常に等倍のタイル画像の座標で、象限の境目は元画像の幅・高さ(偶数へ切り詰め)の半分。
/// 縮小レベルから描くときは象限ごとに縮小して並べ、右・下の象限を等倍と同じ境目から始める
/// (<see cref="RenderSource.SeamX"/>)。レベルのタイルを一様に並べると、象限の幅・高さが
/// 縮小率で割り切れないとき右・下の象限が境目より手前から始まり、ROIの象限判定
/// (<see cref="BayerSplit.TryMapTiledRegion"/>)と食い違う。
/// 縮小レベルは象限の幅・高さを縮小率で割った端数を切り捨てているため、各象限の最後の
/// 端数ぶんはそのチャネルのレベルの最終列・最終行で埋める。
/// </remarks>
public sealed class ChannelSplitRenderSource : RenderSource
{
    private readonly RawImage _image;
    private readonly int _frame;
    private readonly int _factor;
    private readonly int _tiledWidth;
    private readonly int _tiledHeight;

    // 読み出す画像(元画像またはレベル)の1チャネルぶんの幅・高さ
    private readonly int _channelWidth;
    private readonly int _channelHeight;

    // レベル座標での象限の幅・高さ(等倍の象限を縮小率で割って切り上げ)
    private readonly int _levelQuadWidth;
    private readonly int _levelQuadHeight;

    /// <summary>元画像を等倍で描くソースを生成する。</summary>
    /// <param name="image">元画像。</param>
    /// <param name="frame">フレーム番号。</param>
    public ChannelSplitRenderSource(RawImage image, int frame)
        : this(image, frame, 1, image.Width & ~1, image.Height & ~1)
    {
    }

    /// <summary>Bayer位相を保った縮小レベルから描くソースを生成する。</summary>
    /// <param name="level">
    /// 縮小レベル(<see cref="BayerPyramid"/> のレベル。縮小率1なら元画像)。各チャネルの
    /// (i, j) 画素が、元画像の同じチャネルの 縮小率×縮小率 ブロック (i, j) を表すこと。
    /// </param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="factor">縮小率(1以上)。</param>
    /// <param name="tiledWidth">等倍のタイル画像の幅(元画像の幅を偶数へ切り詰めた値)。</param>
    /// <param name="tiledHeight">等倍のタイル画像の高さ(元画像の高さを偶数へ切り詰めた値)。</param>
    public ChannelSplitRenderSource(
        RawImage level, int frame, int factor, int tiledWidth, int tiledHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
        _image = level;
        _frame = frame;
        _factor = factor;
        _tiledWidth = tiledWidth;
        _tiledHeight = tiledHeight;
        _channelWidth = level.Width / 2;
        _channelHeight = level.Height / 2;
        _levelQuadWidth = (tiledWidth / 2 + factor - 1) / factor;
        _levelQuadHeight = (tiledHeight / 2 + factor - 1) / factor;
    }

    /// <inheritdoc />
    public override int SourceWidth => _tiledWidth;

    /// <inheritdoc />
    public override int SourceHeight => _tiledHeight;

    /// <inheritdoc />
    public override int Factor => _factor;

    /// <inheritdoc />
    public override int LevelWidth => _levelQuadWidth * 2;

    /// <inheritdoc />
    public override int LevelHeight => _levelQuadHeight * 2;

    /// <inheritdoc />
    public override object CacheKey => (_image, _frame, nameof(ChannelSplitRenderSource));

    /// <inheritdoc />
    public override int SeamX => _tiledWidth / 2;

    /// <inheritdoc />
    public override int SeamLevelX => _levelQuadWidth;

    /// <inheritdoc />
    public override int SeamY => _tiledHeight / 2;

    /// <inheritdoc />
    public override int SeamLevelY => _levelQuadHeight;

    /// <inheritdoc />
    public override void ReadRow(int levelY, int levelX, int count, Span<ushort> destination)
    {
        // タイル行は読み出す画像の1行(sy固定)からストライド2で取り出せる
        int sourceY = ToSource(levelY, _levelQuadHeight, _channelHeight);

        // 必要な元画素のX範囲だけ読む。従来は count に関係なく常に全幅を読んでいたため、
        // 拡大表示ではdest行ごとに数万画素を捨てていた
        int lastLevelX = levelX + count - 1;
        int maxX = _channelWidth * 2 - 1;
        int startX;
        int endX;
        if ((levelX < _levelQuadWidth) == (lastLevelX < _levelQuadWidth))
        {
            // 同一象限内: sourceX = innerX*2 + quadX で単調増加
            startX = ToSource(levelX, _levelQuadWidth, _channelWidth);
            endX = ToSource(lastLevelX, _levelQuadWidth, _channelWidth);
        }
        else
        {
            // 象限の境界をまたぐ場合は両側が必要になるため全幅
            startX = 0;
            endX = maxX;
        }

        startX = Math.Clamp(startX, 0, maxX);
        endX = Math.Clamp(endX, startX, maxX);
        int span = endX - startX + 1;

        ushort[] sourceRow = ArrayPool<ushort>.Shared.Rent(span);
        try
        {
            _image.CopyRegion(_frame, startX, sourceY, span, 1, sourceRow);
            for (int i = 0; i < count; i++)
            {
                destination[i] =
                    sourceRow[ToSource(levelX + i, _levelQuadWidth, _channelWidth) - startX];
            }
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(sourceRow);
        }
    }

    /// <summary>
    /// レベル座標(象限ごとに縮小したタイル座標)を、読み出す画像の座標へ写す。
    /// 象限の端数(レベルが切り捨てた分)は、そのチャネルの最終列・最終行で埋める。
    /// </summary>
    /// <param name="level">レベル座標。</param>
    /// <param name="levelQuad">レベル座標での象限の長さ。</param>
    /// <param name="channelLength">読み出す画像の1チャネルぶんの長さ。</param>
    /// <returns>読み出す画像の座標。</returns>
    private static int ToSource(int level, int levelQuad, int channelLength)
    {
        int quad = level < levelQuad ? 0 : 1;
        int inner = Math.Min(level - quad * levelQuad, channelLength - 1);
        return inner * 2 + quad;
    }
}
