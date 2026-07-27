namespace RawViewer.Core;

/// <summary>
/// 8bit出力用の焼き込みレンダリング(表示LUT・現像結果を全画素へ適用)。
/// エンコード(PNG/JPEG)はApp層のWICが担当し、Coreは画素バッファのみ生成する。
/// </summary>
public static class ImageExport
{
    /// <summary>
    /// 表示LUTを全画素に適用したグレー8bitバッファを生成する(行並列)。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="lut">表示LUT。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>width×height のグレー8bitバッファ。</returns>
    public static byte[] RenderGray8(
        RawImage image, int frame, DisplayLut lut, CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        var gray = new byte[(long)width * height];

        Parallel.For(
            0,
            height,
            () => new ushort[width],
            (y, state, row) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    state.Stop();
                    return row;
                }

                image.CopyRegion(frame, 0, y, width, 1, row);
                lut.Apply(row, gray.AsSpan(y * width, width));
                return row;
            },
            _ => { });

        cancellationToken.ThrowIfCancellationRequested();
        return gray;
    }

    /// <summary>
    /// カラー現像(バイリニアデモザイク+現像LUT)を全画素に適用した
    /// RGB24バッファを生成する。バンド単位で処理し境界1行を重複させる。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="pattern">Bayerパターン。</param>
    /// <param name="luts">現像LUT。</param>
    /// <param name="progress">進捗通知(0〜1)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>width×height×3 のRGB24バッファ(R,G,Bの順)。</returns>
    public static byte[] DevelopRgb24(
        RawImage image,
        int frame,
        BayerPattern pattern,
        DevelopLuts luts,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        var rgb24 = new byte[(long)width * height * 3];
        const int bandRows = 256;

        // バンドごとに確保すると1億画素で約800MBのLOH割り当てになる。
        // 最大バンド高さぶんを一度だけ確保して使い回す
        int maxBandHeight = Math.Min(height, bandRows + 2);
        var mosaic = new ushort[(long)width * maxBandHeight];
        var rgb16 = new ushort[(long)width * maxBandHeight * 3];

        for (int bandY = 0; bandY < height; bandY += bandRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rows = Math.Min(bandRows, height - bandY);
            int top = Math.Max(0, bandY - 1);
            int bottom = Math.Min(height - 1, bandY + rows);
            int bandHeight = bottom - top + 1;

            for (int y = 0; y < bandHeight; y++)
            {
                image.CopyRegion(frame, 0, top + y, width, 1,
                    mosaic.AsSpan(y * width, width));
            }

            ColorPipeline.DemosaicBilinear(
                mosaic, width, bandHeight, 0, top, pattern, rgb16, cancellationToken);

            int skip = bandY - top;
            Parallel.For(0, rows, r =>
            {
                int sourceRow = skip + r;
                long destOffset = ((long)(bandY + r) * width) * 3;
                int srcOffset = sourceRow * width * 3;
                for (int x = 0; x < width; x++)
                {
                    int si = srcOffset + x * 3;
                    long di = destOffset + x * 3;
                    luts.Convert(rgb16[si], rgb16[si + 1], rgb16[si + 2],
                        out byte r8, out byte g8, out byte b8);
                    rgb24[di] = r8;
                    rgb24[di + 1] = g8;
                    rgb24[di + 2] = b8;
                }
            });

            progress?.Report((double)(bandY + rows) / height);
        }

        return rgb24;
    }
}
