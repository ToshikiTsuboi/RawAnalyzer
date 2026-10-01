using System.Buffers;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Rendering;

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

    /// <summary>デコード済みカラー画像(JPEG/PNG/カラーTIFF)のRGB表示。</summary>
    TrueColor,
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

    /// <summary>セグメント幅(元画素px)。</summary>
    public int SegmentWidth { get; init; }

    /// <summary>ゼブラ(飽和/黒潰れ警告)を表示するか(グレー系モードのみ)。</summary>
    public bool ZebraEnabled { get; init; }

    /// <summary>デコード済みカラー画像(TrueColorモードで使用)。</summary>
    public ColorImage? Color { get; init; }

    /// <summary>
    /// デモザイク結果の使い回し先(省略時は毎回やり直す)。
    /// </summary>
    public DemosaicCache? DemosaicCache { get; init; }
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

    /// <summary>ゼブラの飽和閾値(フルスケールの98%)。</summary>
    public const ushort ZebraSaturationThreshold = 64224;

    /// <summary>ゼブラの黒潰れ閾値(フルスケールの2%)。</summary>
    public const ushort ZebraBlackThreshold = 1310;

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
            case ViewportDisplayMode.TrueColor when request.Color is not null:
                RenderTrueColor(
                    request, zoom, originX, originY, destWidth, destHeight,
                    destination, cancellationToken);
                break;
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

    /// <summary>
    /// 可視dest列区間 [Dx0, Dx1] を閉形式で求める。空ならfalse。
    /// </summary>
    /// <remarks>
    /// dest列 dx のソース座標は originX + (dx + 0.5) / zoom で単調増加するため、
    /// 線形探索は不要。従来はdest行ごとに最大2×destWidth回のループを回していた。
    /// </remarks>
    private static bool TryComputeVisibleColumns(
        double zoom, double originX, double sourceWidth, int destWidth,
        out int dx0, out int dx1)
    {
        // originX + (dx + 0.5)/zoom >= 0        → dx >= -originX*zoom - 0.5
        // originX + (dx + 0.5)/zoom <  width    → dx <  (width - originX)*zoom - 0.5
        double firstExact = -originX * zoom - 0.5;
        double lastExclusive = (sourceWidth - originX) * zoom - 0.5;

        dx0 = Math.Max(0, (int)Math.Ceiling(firstExact));
        dx1 = Math.Min(destWidth - 1, (int)Math.Ceiling(lastExclusive) - 1);

        // 浮動小数の丸めで1画素ずれることがあるため境界だけ実値で詰める
        while (dx0 < destWidth && originX + (dx0 + 0.5) / zoom < 0)
        {
            dx0++;
        }

        while (dx0 > 0 && originX + (dx0 - 0.5) / zoom >= 0)
        {
            dx0--;
        }

        while (dx1 >= dx0 && originX + (dx1 + 0.5) / zoom >= sourceWidth)
        {
            dx1--;
        }

        while (dx1 + 1 < destWidth && originX + (dx1 + 1.5) / zoom < sourceWidth)
        {
            dx1++;
        }

        return dx1 >= dx0;
    }

    /// <summary>行の可視範囲とレベル座標区間を求める。不可視ならnull。</summary>
    private static RowSpan? ComputeRowSpan(
        RenderSource source, double zoom, double originX, double originY,
        int destWidth, int destY)
    {
        double invZoom = 1.0 / zoom;
        double srcY = originY + (destY + 0.5) * invZoom;
        if (srcY < 0 || srcY >= source.ExtentHeight)
        {
            return null;
        }

        int levelY = Math.Min(source.ToLevelY(srcY), source.LevelHeight - 1);
        if (!TryComputeVisibleColumns(
                zoom, originX, source.ExtentWidth, destWidth, out int dx0, out int dx1))
        {
            return null;
        }

        int levelX0 = Math.Clamp(
            source.ToLevelX(originX + (dx0 + 0.5) * invZoom), 0, source.LevelWidth - 1);
        int levelX1 = Math.Clamp(
            source.ToLevelX(originX + (dx1 + 0.5) * invZoom), 0, source.LevelWidth - 1);
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

        // 左右を別々に縮小して並べるソース(チャネル分割の縮小表示)は、
        // 継ぎ目から右の列のレベル座標を継ぎ目の位置から数え直す
        int seamX = source.SeamX;
        int seamLevelX = source.SeamLevelX;
        double invFactor = 1.0 / factor;

        // 行バッファは幅数万でLOH行きになるため、描画ごとに確保せずプールから借りる
        Parallel.For(
            0,
            destHeight,
            () => ArrayPool<ushort>.Shared.Rent(source.LevelWidth),
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
                bool zebra = request.ZebraEnabled;

                // ループ不変の除算を外へ出す(画素ごとの割り算をなくす)
                double invZoomOverFactor = invZoom / factor;
                double originOverFactor = originX / factor;
                for (int dx = s.Dx0; dx <= s.Dx1; dx++)
                {
                    double srcX = originX + ((dx + 0.5) * invZoom);
                    int levelX = Math.Clamp(
                        (srcX < seamX
                            ? (int)(originOverFactor + ((dx + 0.5) * invZoomOverFactor))
                            : seamLevelX + (int)((srcX - seamX) * invFactor))
                            - s.LevelX0,
                        0,
                        s.Count - 1);
                    // 段は描く画素の列の整数除算で決める。1/段幅 を掛けると、段幅によっては
                    // 段幅×fl(1/段幅) が1未満になり、境目ちょうどの列が前の段のLUTで描かれる
                    DisplayLut activeLut = segmentLuts is null
                        ? lut
                        : segmentLuts[Math.Clamp(
                            (int)srcX / segmentWidth, 0, segmentLuts.Count - 1)];
                    ushort value = rowBuffer[levelX];
                    byte d = activeLut.Map(value);
                    int o = dx * 4;

                    // ゼブラ: 斜めストライプで飽和=赤 / 黒潰れ=青
                    if (zebra && ((dx + destY) & 7) < 4)
                    {
                        if (value >= ZebraSaturationThreshold)
                        {
                            destRow[o] = 0x3C;
                            destRow[o + 1] = 0x50;
                            destRow[o + 2] = 0xE6;
                            destRow[o + 3] = 255;
                            continue;
                        }

                        if (value <= ZebraBlackThreshold)
                        {
                            destRow[o] = 0xE6;
                            destRow[o + 1] = 0x78;
                            destRow[o + 2] = 0x3C;
                            destRow[o + 3] = 255;
                            continue;
                        }
                    }

                    destRow[o] = d;
                    destRow[o + 1] = d;
                    destRow[o + 2] = d;
                    destRow[o + 3] = 255;
                }

                return rowBuffer;
            },
            buffer => ArrayPool<ushort>.Shared.Return(buffer));
    }

    private static void RenderTrueColor(
        RenderRequest request, double zoom, double originX, double originY,
        int destWidth, int destHeight, byte[] destination, CancellationToken ct)
    {
        ColorImage color = request.Color!;
        DisplayLut lut = request.Lut;
        bool zebra = request.ZebraEnabled;
        double invZoom = 1.0 / zoom;

        Parallel.For(
            0,
            destHeight,
            () => ArrayPool<ushort>.Shared.Rent(color.Width * 3),
            (destY, state, rowBuffer) =>
            {
                if (ct.IsCancellationRequested)
                {
                    state.Stop();
                    return rowBuffer;
                }

                Span<byte> destRow = destination.AsSpan(destY * destWidth * 4, destWidth * 4);
                double srcY = originY + (destY + 0.5) * invZoom;
                if (srcY < 0 || srcY >= color.Height)
                {
                    FillBackground(destRow);
                    return rowBuffer;
                }

                // 可視範囲のソース列区間だけ読み出す
                if (!TryComputeVisibleColumns(
                        zoom, originX, color.Width, destWidth, out int dx0, out int dx1))
                {
                    FillBackground(destRow);
                    return rowBuffer;
                }

                int sourceY = Math.Clamp((int)srcY, 0, color.Height - 1);
                int sx0 = Math.Clamp((int)(originX + (dx0 + 0.5) * invZoom), 0, color.Width - 1);
                int sx1 = Math.Clamp((int)(originX + (dx1 + 0.5) * invZoom), 0, color.Width - 1);
                int count = sx1 - sx0 + 1;
                color.CopyRow(sourceY, sx0, count, rowBuffer.AsSpan(0, count * 3));

                FillBackground(destRow[..(dx0 * 4)]);
                FillBackground(destRow[((dx1 + 1) * 4)..]);
                for (int dx = dx0; dx <= dx1; dx++)
                {
                    int sx = Math.Clamp(
                        (int)(originX + (dx + 0.5) * invZoom) - sx0, 0, count - 1);
                    ushort r = rowBuffer[sx * 3];
                    ushort g = rowBuffer[sx * 3 + 1];
                    ushort b = rowBuffer[sx * 3 + 2];
                    int o = dx * 4;

                    if (zebra && ((dx + destY) & 7) < 4)
                    {
                        int peak = Math.Max(r, Math.Max(g, b));
                        int floorValue = Math.Min(r, Math.Min(g, b));
                        if (peak >= ZebraSaturationThreshold)
                        {
                            destRow[o] = 0x3C;
                            destRow[o + 1] = 0x50;
                            destRow[o + 2] = 0xE6;
                            destRow[o + 3] = 255;
                            continue;
                        }

                        if (floorValue <= ZebraBlackThreshold)
                        {
                            destRow[o] = 0xE6;
                            destRow[o + 1] = 0x78;
                            destRow[o + 2] = 0x3C;
                            destRow[o + 3] = 255;
                            continue;
                        }
                    }

                    destRow[o] = lut.Map(b);
                    destRow[o + 1] = lut.Map(g);
                    destRow[o + 2] = lut.Map(r);
                    destRow[o + 3] = 255;
                }

                return rowBuffer;
            },
            buffer => ArrayPool<ushort>.Shared.Return(buffer));
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
            () => ArrayPool<ushort>.Shared.Rent(source.LevelWidth),
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
            buffer => ArrayPool<ushort>.Shared.Return(buffer));
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

        // 可視領域とBayer位相が同じなら、モザイク読み出しとデモザイクは前回の結果でよい
        // (現像スライダー操作で変わるのは最後のLUT適用だけ)
        object cacheKey = (source.CacheKey, request.Pattern, sx0, sy0, sx1, sy1);
        ushort[]? rgb = request.DemosaicCache?.TryGet(cacheKey);
        bool reused = rgb is not null;
        ushort[]? mosaic = null;
        try
        {
            if (rgb is null)
            {
                mosaic = ArrayPool<ushort>.Shared.Rent(rectW * rectH);

                // キャッシュへ載せるのでプールからは借りない(返却後に読まれないように)
                rgb = new ushort[rectW * rectH * 3];
                Parallel.For(0, rectH, y =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        source.ReadRow(sy0 + y, sx0, rectW, mosaic.AsSpan(y * rectW, rectW));
                    }
                });
                ct.ThrowIfCancellationRequested();

                ColorPipeline.DemosaicBilinear(
                    mosaic, rectW, rectH, sx0, sy0, request.Pattern, rgb, ct);
                ct.ThrowIfCancellationRequested();
            }

            Parallel.For(0, destHeight, destY =>
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                Span<byte> destRow = destination.AsSpan(destY * destWidth * 4, destWidth * 4);
                double srcY = originY + (destY + 0.5) * invZoom;
                if (srcY < 0 || srcY >= source.ExtentHeight)
                {
                    FillBackground(destRow);
                    return;
                }

                int ry = Math.Clamp((int)srcY - sy0, 0, rectH - 1);
                for (int dx = 0; dx < destWidth; dx++)
                {
                    double srcX = originX + (dx + 0.5) * invZoom;
                    int o = dx * 4;
                    if (srcX < 0 || srcX >= source.ExtentWidth)
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

            if (!reused)
            {
                request.DemosaicCache?.Store(cacheKey, rgb);
            }
        }
        finally
        {
            if (mosaic is not null)
            {
                ArrayPool<ushort>.Shared.Return(mosaic);
            }
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
        // 偶数へ落とさないと、奇数サイズ画像の最下行・最右列でBayer位相が崩れ
        // R/B両方に緑が入って色が潰れる
        int maxBlockY = Math.Max(0, (source.SourceHeight - 2) & ~1);
        int maxBlockX = Math.Max(0, (source.SourceWidth - 2) & ~1);

        Parallel.For(
            0,
            destHeight,
            () => (Top: ArrayPool<ushort>.Shared.Rent(source.LevelWidth),
                   Bottom: ArrayPool<ushort>.Shared.Rent(source.LevelWidth)),
            (destY, state, buffers) =>
            {
                if (ct.IsCancellationRequested)
                {
                    state.Stop();
                    return buffers;
                }

                Span<byte> destRow = destination.AsSpan(destY * destWidth * 4, destWidth * 4);
                double srcY = originY + (destY + 0.5) * invZoom;
                if (srcY < 0 || srcY >= source.ExtentHeight)
                {
                    FillBackground(destRow);
                    return buffers;
                }

                int blockY = Math.Clamp((int)srcY & ~1, 0, maxBlockY);

                // 可視dest列区間
                if (!TryComputeVisibleColumns(
                        zoom, originX, source.ExtentWidth, destWidth, out int dx0, out int dx1))
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
            buffers =>
            {
                ArrayPool<ushort>.Shared.Return(buffers.Top);
                ArrayPool<ushort>.Shared.Return(buffers.Bottom);
            });
    }
}
