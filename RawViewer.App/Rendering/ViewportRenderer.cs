using RawViewer.Core;

namespace RawViewer.App.Rendering;

/// <summary>
/// ビューポート領域のみをグレースケール8bitへ描画するレンダラ。
/// 最近傍サンプリング + DisplayLut適用。行単位でParallel.For並列化。
/// </summary>
public static class ViewportRenderer
{
    /// <summary>画像外領域の背景グレー値(キャンバス色相当)。</summary>
    public const byte BackgroundGray = 22;

    /// <summary>
    /// ビューポートを描画する。
    /// </summary>
    /// <param name="source">画素供給元。</param>
    /// <param name="lut">表示LUT。</param>
    /// <param name="zoom">ズーム率(表示px / 元画像px)。</param>
    /// <param name="originX">ビューポート左上に対応する元画像X座標。</param>
    /// <param name="originY">ビューポート左上に対応する元画像Y座標。</param>
    /// <param name="destWidth">出力幅(px)。</param>
    /// <param name="destHeight">出力高さ(px)。</param>
    /// <param name="destination">destWidth × destHeight 以上のグレー8bit出力バッファ。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <exception cref="OperationCanceledException">キャンセルされた場合。</exception>
    public static void Render(
        RenderSource source,
        DisplayLut lut,
        double zoom,
        double originX,
        double originY,
        int destWidth,
        int destHeight,
        byte[] destination,
        CancellationToken cancellationToken)
    {
        int factor = source.Factor;
        double invZoom = 1.0 / zoom;
        int sourceWidth = source.SourceWidth;
        int sourceHeight = source.SourceHeight;

        Parallel.For(
            0,
            destHeight,
            () => new ushort[source.LevelWidth],
            (destY, state, rowBuffer) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    state.Stop();
                    return rowBuffer;
                }

                int rowOffset = destY * destWidth;
                double srcY = originY + (destY + 0.5) * invZoom;
                if (srcY < 0 || srcY >= sourceHeight)
                {
                    destination.AsSpan(rowOffset, destWidth).Fill(BackgroundGray);
                    return rowBuffer;
                }

                int levelY = Math.Min((int)(srcY / factor), source.LevelHeight - 1);

                // 可視範囲のdest列区間を求める
                int dx0 = 0;
                while (dx0 < destWidth && originX + (dx0 + 0.5) * invZoom < 0)
                {
                    dx0++;
                }

                int dx1 = destWidth - 1;
                while (dx1 >= dx0 && originX + (dx1 + 0.5) * invZoom >= sourceWidth)
                {
                    dx1--;
                }

                if (dx1 < dx0)
                {
                    destination.AsSpan(rowOffset, destWidth).Fill(BackgroundGray);
                    return rowBuffer;
                }

                int levelX0 = Math.Clamp(
                    (int)((originX + (dx0 + 0.5) * invZoom) / factor), 0, source.LevelWidth - 1);
                int levelX1 = Math.Clamp(
                    (int)((originX + (dx1 + 0.5) * invZoom) / factor), 0, source.LevelWidth - 1);
                int count = levelX1 - levelX0 + 1;
                source.ReadRow(levelY, levelX0, count, rowBuffer.AsSpan(0, count));

                destination.AsSpan(rowOffset, dx0).Fill(BackgroundGray);
                destination.AsSpan(rowOffset + dx1 + 1, destWidth - dx1 - 1).Fill(BackgroundGray);
                for (int dx = dx0; dx <= dx1; dx++)
                {
                    int levelX = Math.Clamp(
                        (int)((originX + (dx + 0.5) * invZoom) / factor) - levelX0, 0, count - 1);
                    destination[rowOffset + dx] = lut.Map(rowBuffer[levelX]);
                }

                return rowBuffer;
            },
            _ => { });

        cancellationToken.ThrowIfCancellationRequested();
    }
}
