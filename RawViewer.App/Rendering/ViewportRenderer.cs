using System.Buffers;
using RawViewer.Core;

namespace RawViewer.App.Rendering;

/// <summary>ビューポートの表示モード。</summary>
public enum ViewportDisplayMode
{
    /// <summary>Rawグレースケール表示。</summary>
    Raw,

    /// <summary>デモザイクせず各画素をBayer色で着色表示。</summary>
    BayerColor,

    /// <summary>カラー現像(黒レベル→WB→デモザイク→ガンマ)。</summary>
    ColorDevelop,

    /// <summary>R/Gr/Gb/Bの2x2タイル並置表示。</summary>
    ChannelSplit,
}

/// <summary>描画要求(スナップショット)。</summary>
public sealed class RenderRequest
{
    /// <summary>画素供給元。</summary>
    public required RenderSource Source { get; init; }

    /// <summary>グレー表示用LUT。</summary>
    public required DisplayLut Lut { get; init; }

    /// <summary>表示モード。</summary>
    public ViewportDisplayMode Mode { get; init; } = ViewportDisplayMode.Raw;

    /// <summary>Bayerパターン。</summary>
    public BayerPattern Pattern { get; init; } = BayerPattern.None;

    /// <summary>カラー現像LUT(ColorDevelopで必須)。</summary>
    public DevelopLuts? DevelopLuts { get; init; }

    /// <summary>
    /// HDR分割表示用のフレーム別LUT。設定時はX座標をSegmentWidthで区切って
    /// セグメントごとに適用する(Rawモードのみ)。
    /// </summary>
    public IReadOnlyList<DisplayLut>? SegmentLuts { get; init; }

    /// <summary>セグメント幅(元画像px)。</summary>
    public int SegmentWidth { get; init; }
}

/// <summary>
/// ビューポート領域のみをBGRA32へ描画するレンダラ。
/// 最近傍サンプリング+LUT適用。行単位でParallel.For並列化。
/// 巨大画像でも全画素処理は行わず、可視領域ぶんのみ読み出す。
/// </summary>
public static class ViewportRenderer
{
    /// <summary>画像外領域の背景グレー値(キャンバス色相当)。</summary>
    public const byte BackgroundGray = 22;

    /// <summary>この画素数以下の可視領域はバイリニアデモザイクで現像する。</summary>
    public const long BilinearRegionBudget = 6_000_000;

    /// <summary>
    /// ビューポートを描画する。
    /// </summary>
    /// <param name="request">描画要求。</param>
    /// <param name="zoom">ズーム率(表示px / 元画像px)。</param>
    /// <param name="originX">ビューポート左上に対応する元画像X座標。</param>
    /// <param name="originY">ビューポート左上に対応する元画像Y座標。</param>
    /// <param name="destWidth">出力幅(px)。</param>
    /// <param name="destHeight">出力高さ(px)。</param>
    /// <param name="destination">destWidth × destHeight × 4 以上のBGRA出力バッファ。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <exception cref="OperationCanceledException">キャンセルされた場合。</exception>
    public static void Render(
        RenderRequest request,
        double zoom,
        double originX,
        double originY,
        int destWidth,
        int destHeight,
        byte[] destination,
        CancellationToken cancellationToken)
    {
        bool colorPossible = request.Pattern != BayerPattern.None
            && request.Source.Factor == 1;
        switch (request.Mode)
        {
            case ViewportDisplayMode.BayerColor when colorPossible:
                RenderBayerColor(
                    request, zoom, originX, originY, destWidth, destHeight,
                    destination, cancellationToken);
                break;
            case ViewportDisplayMode.ColorDevelop when colorPossible
                && request.DevelopLuts is not null:
                RenderDevelop(
                    request, zoom, originX, originY, destWidth, destHeight,
                    destination, cancellationToken);
                break;
            default:
                RenderGray(
                    request, zoom, originX, originY, destWidth, destHeight,
                    destination, cancellationToken);
                break;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void FillBackground(Span<byte> bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            bgra[i] = BackgroundGray;
            bgra[i + 1] = BackgroundGray;
            bgra[i + 2] = BackgroundGray;
            bgra[i + 3] = 255;
        }
    }

    private readonly record struct RowSpan(int Dx0, int Dx1, int LevelX0, int Count, int LevelY);

    /// <summary>行の可視範囲とレベル座標区間を求める。不可視ならnull。</summary>
    private static RowSpan? ComputeRowSpan(
        RenderSource source, double zoom, double originX, double originY,
        int destWidth, int destY)
    {
        int factor = source.Factor;
        double invZoom = 1.0 / zoom;
        double srcY = originY + (destY + 0.5) * invZoom;
        if (srcY < 0 || srcY >= source.SourceHeight)
        {
            return null;
        }

        int levelY = Math.Min((int)(srcY / factor), source.LevelHeight - 1);
        int dx0 = 0;
        while (dx0 < destWidth && originX + (dx0 + 0.5) * invZoom < 0)
        {
            dx0++;
        }

        int dx1 = destWidth - 1;
        while (dx1 >= dx0 && originX + (dx1 + 0.5) * invZoom >= source.SourceWidth)
        {
            dx1--;
        }

        if (dx1 < dx0)
        {
            return null;
        }

        int levelX0 = Math.Clamp(
            (int)((originX + (dx0 + 0.5) * invZoom) / factor), 0, source.LevelWidth - 1);
        int levelX1 = Math.Clamp(
            (int)((originX + (dx1 + 0.5) * invZoom) / factor), 0, source.LevelWidth - 1);
        return new RowSpan(dx0, dx1, levelX0, levelX1 - levelX0 + 1, levelY);
    }

    private static void RenderGray(
        RenderRequest request, double zoom, double originX, double originY,
        int destWidth, int destHeight, byte[] destination, CancellationToken ct)
    {
        RenderSource source = request.Source;
        DisplayLut lut = request.Lut;
        int factor = source.Factor;
        double invZoom = 1.0 / zoom;

        Parallel.For(
            0,
            destHeight,
            () => new ushort[source.LevelWidth],
            (destY, state, rowBuffer) =>
            {
                if (ct.IsCancellationRequested)
                {
                    state.Stop();
                    return rowBuffer;
                }

                int rowOffset = destY * destWidth * 4;
                Span<byte> destRow = destination.AsSpan(rowOffset, destWidth * 4);
                RowSpan? span = ComputeRowSpan(source, zoom, originX, originY, destWidth, destY);
                if (span is not { } s)
                {
                    FillBackground(destRow);
                    return rowBuffer;
                }

                source.ReadRow(s.LevelY, s.LevelX0, s.Count, rowBuffer.AsSpan(0, s.Count));
                FillBackground(destRow[..(s.Dx0 * 4)]);
                FillBackground(destRow[((s.Dx1 + 1) * 4)..]);
                IReadOnlyList<DisplayLut>? segmentLuts = request.SegmentLuts;
                int segmentWidth = Math.Max(1, request.SegmentWidth);
                for (int dx = s.Dx0; dx <= s.Dx1; dx++)
                {
                    double srcX = originX + (dx + 0.5) * invZoom;
                    int levelX = Math.Clamp(
                        (int)(srcX / factor) - s.LevelX0, 0, s.Count - 1);
                    DisplayLut activeLut = segmentLuts is null
                        ? lut
                        : segmentLuts[Math.Clamp(
                            (int)srcX / segmentWidth, 0, segmentLuts.Count - 1)];
                    byte d = activeLut.Map(rowBuffer[levelX]);
                    int o = dx * 4;
                    destRow[o] = d;
                    destRow[o + 1] = d;
                    destRow[o + 2] = d;
                    destRow[o + 3] = 255;
                }

                return rowBuffer;
            },
            _ => { });
    }

    private static void RenderBayerColor(
        RenderRequest request, double zoom, double originX, double originY,
        int destWidth, int destHeight, byte[] destination, CancellationToken ct)
    {
        RenderSource source = request.Source;
        DisplayLut lut = request.Lut;
        BayerPattern pattern = request.Pattern;
        double invZoom = 1.0 / zoom;

        Parallel.For(
            0,
            destHeight,
            () => new ushort[source.LevelWidth],
            (destY, state, rowBuffer) =>
            {
                if (ct.IsCancellationRequested)
                {
                    state.Stop();
                    return rowBuffer;
                }

                int rowOffset = destY * destWidth * 4;
                Span<byte> destRow = destination.AsSpan(rowOffset, destWidth * 4);
                RowSpan? span = ComputeRowSpan(source, zoom, originX, originY, destWidth, destY);
                if (span is not { } s)
                {
                    FillBackground(destRow);
                    return rowBuffer;
                }

                source.ReadRow(s.LevelY, s.LevelX0, s.Count, rowBuffer.AsSpan(0, s.Count));
                FillBackground(destRow[..(s.Dx0 * 4)]);
                FillBackground(destRow[((s.Dx1 + 1) * 4)..]);
                for (int dx = s.Dx0; dx <= s.Dx1; dx++)
                {
                    int srcX = Math.Clamp(
                        (int)(originX + (dx + 0.5) * invZoom), 0, source.SourceWidth - 1);
                    byte d = lut.Map(rowBuffer[Math.Clamp(srcX - s.LevelX0, 0, s.Count - 1)]);
                    byte dim = (byte)(d * 2 / 5);
                    BayerChannel channel = BayerHelper.GetChannel(pattern, srcX, s.LevelY);
                    int o = dx * 4;
                    destRow[o] = channel == BayerChannel.B ? d : dim;
                    destRow[o + 1] = channel is BayerChannel.Gr or BayerChannel.Gb ? d : dim;
                    destRow[o + 2] = channel == BayerChannel.R ? d : dim;
                    destRow[o + 3] = 255;
                }

                return rowBuffer;
            },
            _ => { });
    }

    private static void RenderDevelop(
        RenderRequest request, double zoom, double originX, double originY,
        int destWidth, int destHeight, byte[] destination, CancellationToken ct)
    {
        RenderSource source = request.Source;
        double invZoom = 1.0 / zoom;

        // 可視ソース領域(偶数整列+境界2px)
        int sx0 = Math.Clamp((int)originX - 2, 0, Math.Max(0, source.SourceWidth - 1)) & ~1;
        int sy0 = Math.Clamp((int)originY - 2, 0, Math.Max(0, source.SourceHeight - 1)) & ~1;
        int sx1 = Math.Clamp((int)(originX + destWidth * invZoom) + 2, 0, source.SourceWidth - 1);
        int sy1 = Math.Clamp((int)(originY + destHeight * invZoom) + 2, 0, source.SourceHeight - 1);
        long regionPixels = (long)(sx1 - sx0 + 1) * (sy1 - sy0 + 1);

        if (regionPixels <= BilinearRegionBudget)
        {
            RenderDevelopBilinear(
                request, zoom, originX, originY, destWidth, destHeight,
                destination, sx0, sy0, sx1, sy1, ct);
        }
        else
        {
            RenderDevelopBlock(
                request, zoom, originX, originY, destWidth, destHeight, destination, ct);
        }
    }

    private static void RenderDevelopBilinear(
        RenderRequest request, double zoom, double originX, double originY,
        int destWidth, int destHeight, byte[] destination,
        int sx0, int sy0, int sx1, int sy1, CancellationToken ct)
    {
        RenderSource source = request.Source;
        DevelopLuts luts = request.DevelopLuts!;
        double invZoom = 1.0 / zoom;
        int rectW = sx1 - sx0 + 1;
        int rectH = sy1 - sy0 + 1;

        ushort[] mosaic = ArrayPool<ushort>.Shared.Rent(rectW * rectH);
        ushort[] rgb = ArrayPool<ushort>.Shared.Rent(rectW * rectH * 3);
        try
        {
            Parallel.For(0, rectH, y =>
            {
                if (!ct.IsCancellationRequested)
                {
                    source.ReadRow(sy0 + y, sx0, rectW, mosaic.AsSpan(y * rectW, rectW));
                }
            });
            ct.ThrowIfCancellationRequested();

            ColorPipeline.DemosaicBilinear(
                mosaic, rectW, rectH, sx0, sy0, request.Pattern, rgb);
            ct.ThrowIfCancellationRequested();

            Parallel.For(0, destHeight, destY =>
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                Span<byte> destRow = destination.AsSpan(destY * destWidth * 4, destWidth * 4);
                double srcY = originY + (destY + 0.5) * invZoom;
                if (srcY < 0 || srcY >= source.SourceHeight)
                {
                    FillBackground(destRow);
                    return;
                }

                int ry = Math.Clamp((int)srcY - sy0, 0, rectH - 1);
                for (int dx = 0; dx < destWidth; dx++)
                {
                    double srcX = originX + (dx + 0.5) * invZoom;
                    int o = dx * 4;
                    if (srcX < 0 || srcX >= source.SourceWidth)
                    {
                        destRow[o] = BackgroundGray;
                        destRow[o + 1] = BackgroundGray;
                        destRow[o + 2] = BackgroundGray;
                        destRow[o + 3] = 255;
                        continue;
                    }

                    int rx = Math.Clamp((int)srcX - sx0, 0, rectW - 1);
                    int index = (ry * rectW + rx) * 3;
                    luts.Convert(rgb[index], rgb[index + 1], rgb[index + 2],
                        out byte r8, out byte g8, out byte b8);
                    destRow[o] = b8;
                    destRow[o + 1] = g8;
                    destRow[o + 2] = r8;
                    destRow[o + 3] = 255;
                }
            });
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(mosaic);
            ArrayPool<ushort>.Shared.Return(rgb);
        }
    }

    private static void RenderDevelopBlock(
        RenderRequest request, double zoom, double originX, double originY,
        int destWidth, int destHeight, byte[] destination, CancellationToken ct)
    {
        RenderSource source = request.Source;
        DevelopLuts luts = request.DevelopLuts!;
        BayerPattern pattern = request.Pattern;
        double invZoom = 1.0 / zoom;
        int maxBlockY = Math.Max(0, source.SourceHeight - 2);
        int maxBlockX = Math.Max(0, source.SourceWidth - 2);

        Parallel.For(
            0,
            destHeight,
            () => (Top: new ushort[source.LevelWidth], Bottom: new ushort[source.LevelWidth]),
            (destY, state, buffers) =>
            {
                if (ct.IsCancellationRequested)
                {
                    state.Stop();
                    return buffers;
                }

                Span<byte> destRow = destination.AsSpan(destY * destWidth * 4, destWidth * 4);
                double srcY = originY + (destY + 0.5) * invZoom;
                if (srcY < 0 || srcY >= source.SourceHeight)
                {
                    FillBackground(destRow);
                    return buffers;
                }

                int blockY = Math.Clamp((int)srcY & ~1, 0, maxBlockY);

                // 可視dest列区間
                int dx0 = 0;
                while (dx0 < destWidth && originX + (dx0 + 0.5) * invZoom < 0)
                {
                    dx0++;
                }

                int dx1 = destWidth - 1;
                while (dx1 >= dx0 && originX + (dx1 + 0.5) * invZoom >= source.SourceWidth)
                {
                    dx1--;
                }

                if (dx1 < dx0)
                {
                    FillBackground(destRow);
                    return buffers;
                }

                int bx0 = Math.Clamp((int)(originX + (dx0 + 0.5) * invZoom) & ~1, 0, maxBlockX);
                int bx1 = Math.Clamp((int)(originX + (dx1 + 0.5) * invZoom) & ~1, 0, maxBlockX);
                int count = bx1 - bx0 + 2;
                source.ReadRow(blockY, bx0, count, buffers.Top.AsSpan(0, count));
                source.ReadRow(blockY + 1, bx0, count, buffers.Bottom.AsSpan(0, count));

                FillBackground(destRow[..(dx0 * 4)]);
                FillBackground(destRow[((dx1 + 1) * 4)..]);
                for (int dx = dx0; dx <= dx1; dx++)
                {
                    int blockX = Math.Clamp(
                        ((int)(originX + (dx + 0.5) * invZoom) & ~1) - bx0, 0, count - 2);
                    ColorPipeline.BlockToRgb(
                        pattern,
                        buffers.Top[blockX], buffers.Top[blockX + 1],
                        buffers.Bottom[blockX], buffers.Bottom[blockX + 1],
                        out ushort r, out ushort g, out ushort b);
                    luts.Convert(r, g, b, out byte r8, out byte g8, out byte b8);
                    int o = dx * 4;
                    destRow[o] = b8;
                    destRow[o + 1] = g8;
                    destRow[o + 2] = r8;
                    destRow[o + 3] = 255;
                }

                return buffers;
            },
            _ => { });
    }
}
