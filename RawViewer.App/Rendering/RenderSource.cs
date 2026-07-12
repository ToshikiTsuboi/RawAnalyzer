using System.Buffers;
using RawViewer.Core;

namespace RawViewer.App.Rendering;

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

    /// <summary>指定行の一部を読み出す。</summary>
    /// <param name="levelY">レベル座標系の行番号。</param>
    /// <param name="levelX">レベル座標系の開始X。</param>
    /// <param name="count">画素数。</param>
    /// <param name="destination">出力バッファ。</param>
    public abstract void ReadRow(int levelY, int levelX, int count, Span<ushort> destination);
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
    public override void ReadRow(int levelY, int levelX, int count, Span<ushort> destination)
    {
        _image.CopyRegion(_frame, levelX, levelY, count, 1, destination);
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
    public override void ReadRow(int levelY, int levelX, int count, Span<ushort> destination)
    {
        _level.GetRow(levelY).Slice(levelX, count).CopyTo(destination);
    }
}

/// <summary>
/// R/Gr/Gb/Bの2x2タイル並置表示用に座標を写像するソース(等倍のみ)。
/// タイル画像を実体化せず、行読み出し時に元画像からストライド抽出する。
/// </summary>
public sealed class ChannelSplitRenderSource : RenderSource
{
    private readonly RawImage _image;
    private readonly int _frame;
    private readonly int _evenWidth;
    private readonly int _evenHeight;

    /// <summary>ソースを生成する。</summary>
    /// <param name="image">元画像。</param>
    /// <param name="frame">フレーム番号。</param>
    public ChannelSplitRenderSource(RawImage image, int frame)
    {
        _image = image;
        _frame = frame;
        _evenWidth = image.Width & ~1;
        _evenHeight = image.Height & ~1;
    }

    /// <inheritdoc />
    public override int SourceWidth => _evenWidth;

    /// <inheritdoc />
    public override int SourceHeight => _evenHeight;

    /// <inheritdoc />
    public override int Factor => 1;

    /// <inheritdoc />
    public override int LevelWidth => _evenWidth;

    /// <inheritdoc />
    public override int LevelHeight => _evenHeight;

    /// <inheritdoc />
    public override void ReadRow(int levelY, int levelX, int count, Span<ushort> destination)
    {
        // タイル行は元画像の1行(sy固定)からストライド2で取り出せる
        (_, int sourceY) = BayerSplit.MapTiledToSource(0, levelY, _evenWidth, _evenHeight);
        ushort[] sourceRow = ArrayPool<ushort>.Shared.Rent(_evenWidth);
        try
        {
            _image.CopyRegion(_frame, 0, sourceY, _evenWidth, 1, sourceRow);
            int quadWidth = _evenWidth / 2;
            for (int i = 0; i < count; i++)
            {
                int tiledX = levelX + i;
                int quadX = tiledX / quadWidth;
                int innerX = tiledX - quadX * quadWidth;
                destination[i] = sourceRow[innerX * 2 + quadX];
            }
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(sourceRow);
        }
    }
}
