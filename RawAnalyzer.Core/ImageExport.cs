namespace RawAnalyzer.Core;

/// <summary>
/// 8bit出力用の焼き込みレンダリング(表示LUT・現像結果を全画素へ適用)。
/// エンコード(PNG/JPEG)はApp層のWICが担当し、Coreは画素バッファのみ生成する。
/// </summary>
public static class ImageExport
{
    /// <summary>
    /// 1つのバッファに確保できる最大画素数。
    /// </summary>
    /// <remarks>
    /// .NETの配列は要素数が int.MaxValue までなので、RGB24(3バイト/画素)では
    /// 約7.1億画素で確保自体が失敗する。10億画素のカラー書き出しは
    /// この形式では原理的に成立しないため、確保前に理由の分かる形で断る。
    /// </remarks>
    public const long MaxRgb24Pixels = int.MaxValue / 3;

    /// <summary>グレー8bitで1つのバッファに確保できる最大画素数。</summary>
    public const long MaxGray8Pixels = int.MaxValue;

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
        EnsureExportable((long)image.Width * image.Height, MaxGray8Pixels, "8bitグレー");
        var gray = new byte[(long)image.Width * image.Height];
        RenderGray8(image, frame, lut, gray, cancellationToken);
        return gray;
    }

    /// <summary>
    /// 表示LUTを全画素に適用したグレー8bitを、指定バッファへ書き込む。
    /// </summary>
    /// <remarks>
    /// 動画書き出しのようにフレームごとに呼ぶ用途では、確保を使い回すために
    /// こちらを使う(width×height はLOH行きになりGC負荷が無視できない)。
    /// </remarks>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="lut">表示LUT。</param>
    /// <param name="destination">出力先(width×height 以上)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <exception cref="ArgumentException">出力先が小さい場合。</exception>
    public static void RenderGray8(
        RawImage image, int frame, DisplayLut lut, byte[] destination,
        CancellationToken cancellationToken = default)
    {
        int width = image.Width;
        int height = image.Height;
        EnsureExportable((long)width * height, MaxGray8Pixels, "8bitグレー");
        if (destination.LongLength < (long)width * height)
        {
            throw new ArgumentException(
                $"出力バッファが小さすぎます({destination.LongLength} < {(long)width * height})。",
                nameof(destination));
        }

        byte[] gray = destination;
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
    /// <param name="destination">
    /// 出力先。nullなら新たに確保する(動画書き出しでは使い回す)。
    /// </param>
    /// <returns>width×height×3 のRGB24バッファ(R,G,Bの順)。</returns>
    public static byte[] DevelopRgb24(
        RawImage image,
        int frame,
        BayerPattern pattern,
        DevelopLuts luts,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        byte[]? destination = null)
    {
        int width = image.Width;
        int height = image.Height;
        EnsureExportable((long)width * height, MaxRgb24Pixels, "カラー(RGB24)");
        byte[] rgb24 = destination ?? new byte[(long)width * height * 3];
        if (rgb24.LongLength < (long)width * height * 3)
        {
            throw new ArgumentException("出力バッファが小さすぎます。", nameof(destination));
        }
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

    /// <summary>
    /// カラー画像をRGB48(16bit×3)へ取り出す。表示LUTは適用しない。
    /// </summary>
    /// <remarks>
    /// 16bit形式での保存用。輝度化すると色情報が失われるため、
    /// カラー素材はチャネルを保ったまま書き出す。
    /// </remarks>
    /// <param name="color">対象のカラー画像。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>width×height×3 のRGB48バッファ(R,G,Bの順)。</returns>
    /// <exception cref="NotSupportedException">画素数が上限を超える場合。</exception>
    public static ushort[] RenderColorRgb48(
        ColorImage color, CancellationToken cancellationToken = default)
    {
        int width = color.Width;
        int height = color.Height;
        EnsureExportable((long)width * height, MaxRgb24Pixels, "カラー(RGB48)");
        var rgb48 = new ushort[(long)width * height * 3];

        Parallel.For(
            0,
            height,
            new ParallelOptions { CancellationToken = cancellationToken },
            y => color.CopyRow(y, 0, width, rgb48.AsSpan(y * width * 3, width * 3)));

        cancellationToken.ThrowIfCancellationRequested();
        return rgb48;
    }

    /// <summary>
    /// 表示LUTを各チャネルへ適用したRGB24バッファを生成する(行並列)。
    /// TIFF/PNG等から読み込んだカラー画像のバッチ焼き込み用
    /// (既にRGBなのでデモザイク・WB・マトリクスは適用しない)。
    /// </summary>
    /// <param name="color">対象カラー画像。</param>
    /// <param name="lut">表示LUT。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>width×height×3 のRGB24バッファ(R,G,Bの順)。</returns>
    public static byte[] RenderColorRgb24(
        ColorImage color, DisplayLut lut, CancellationToken cancellationToken = default)
    {
        EnsureExportable((long)color.Width * color.Height, MaxRgb24Pixels, "カラー(RGB24)");
        var rgb24 = new byte[(long)color.Width * color.Height * 3];
        RenderColorRgb24(color, lut, rgb24, cancellationToken);
        return rgb24;
    }

    /// <summary>
    /// カラー画像へ表示LUTを適用したRGB24を、指定バッファへ書き込む。
    /// </summary>
    /// <remarks>
    /// 動画書き出しのようにフレームごとに呼ぶ用途では、確保を使い回すために
    /// こちらを使う(4Kで約25MB/フレームがLOH行きになる)。
    /// </remarks>
    /// <param name="color">対象のカラー画像。</param>
    /// <param name="lut">表示LUT。</param>
    /// <param name="destination">出力先(width×height×3 以上)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <exception cref="ArgumentException">出力先が小さい場合。</exception>
    public static void RenderColorRgb24(
        ColorImage color, DisplayLut lut, byte[] destination,
        CancellationToken cancellationToken = default)
    {
        int width = color.Width;
        int height = color.Height;
        EnsureExportable((long)width * height, MaxRgb24Pixels, "カラー(RGB24)");
        if (destination.LongLength < (long)width * height * 3)
        {
            throw new ArgumentException("出力バッファが小さすぎます。", nameof(destination));
        }

        byte[] rgb24 = destination;

        Parallel.For(
            0,
            height,
            () => new ushort[width * 3],
            (y, state, row) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    state.Stop();
                    return row;
                }

                color.CopyRow(y, 0, width, row);

                // LUTは要素単位なのでインターリーブRGBにもそのまま使える
                lut.Apply(row, rgb24.AsSpan(y * width * 3, width * 3));
                return row;
            },
            _ => { });

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 1バッファに収まるかを確保前に確かめる。
    /// </summary>
    /// <param name="pixels">画素数。</param>
    /// <param name="limit">この形式で確保できる上限画素数。</param>
    /// <param name="what">形式名(メッセージ用)。</param>
    /// <exception cref="NotSupportedException">上限を超える場合。</exception>
    private static void EnsureExportable(long pixels, long limit, string what)
    {
        if (pixels > limit)
        {
            throw new NotSupportedException(
                $"{pixels / 1_000_000.0:F0}M画素は{what}書き出しの上限 " +
                $"{limit / 1_000_000.0:F0}M画素を超えています" +
                "(1つの配列に収まらないため)。raw または 16bit TIFF で保存してください。");
        }
    }
}
