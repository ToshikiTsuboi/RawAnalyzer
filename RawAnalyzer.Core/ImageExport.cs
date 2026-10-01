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
    /// 左から同じ幅で並置した区画(HDR分割の各段など)ごとに表示LUTを替えて、全画素に適用した
    /// グレー8bitバッファを生成する(行並列)。
    /// </summary>
    /// <remarks>
    /// X座標 x の画素は、区画 min(x / segmentWidth, 区画数 − 1) のLUTで変換する(最後の区画は画像の右端まで)。
    /// HDR分割表示が段ごとのLUTで描くときと同じ割り当て。
    /// </remarks>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="segmentLuts">区画ごとの表示LUT(左の区画から)。</param>
    /// <param name="segmentWidth">区画の幅(画素)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>width×height のグレー8bitバッファ。</returns>
    /// <exception cref="ArgumentException">区画のLUTが1つもない場合。</exception>
    /// <exception cref="ArgumentOutOfRangeException">区画の幅が1未満の場合。</exception>
    public static byte[] RenderGray8(
        RawImage image, int frame, IReadOnlyList<DisplayLut> segmentLuts, int segmentWidth,
        CancellationToken cancellationToken = default)
    {
        DisplayLut[] luts = ToSegmentArray(segmentLuts, segmentWidth);
        int width = image.Width;
        int height = image.Height;
        EnsureExportable((long)width * height, MaxGray8Pixels, "8bitグレー");
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
                Span<byte> destination = gray.AsSpan(y * width, width);
                for (int s = 0; s < luts.Length; s++)
                {
                    (int x0, int x1) = SegmentColumns(s, luts.Length, segmentWidth, width);
                    luts[s].Apply(row.AsSpan(x0, x1 - x0), destination[x0..x1]);
                }

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

        // 画像全体を1つの区画として現像する
        DevelopSegments(image, frame, pattern, [luts], width, rgb24, progress, cancellationToken);
        return rgb24;
    }

    /// <summary>
    /// 同じBayerパターンの画像を左から同じ幅で並置した画像(HDR分割の各段など)を、区画ごとに現像LUTを替えて
    /// カラー現像(バイリニアデモザイク+現像LUT)したRGB24バッファを生成する。
    /// </summary>
    /// <remarks>
    /// 区画の割り当ては <see cref="RenderGray8(RawImage, int, IReadOnlyList{DisplayLut}, int, CancellationToken)"/>
    /// と同じ。各区画は1枚の画像として、区画の左端を列0とするBayer位相でデモザイクし、隣の区画の画素を補間に使わない
    /// (露光の違う段を並べた画像では、継ぎ目の列に混ざった隣の段の値が段ごとに違うゲインで増幅され、境目に筋が出る。
    /// 区画の幅が奇数でも、後ろの区画を並置画像の座標の位相で読み違えない)。
    /// </remarks>
    /// <param name="image">対象画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="pattern">Bayerパターン。</param>
    /// <param name="segmentLuts">区画ごとの現像LUT(左の区画から)。</param>
    /// <param name="segmentWidth">区画の幅(画素)。</param>
    /// <param name="progress">進捗通知(0〜1)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>width×height×3 のRGB24バッファ(R,G,Bの順)。</returns>
    /// <exception cref="ArgumentException">区画のLUTが1つもない場合。</exception>
    /// <exception cref="ArgumentOutOfRangeException">区画の幅が1未満の場合。</exception>
    public static byte[] DevelopRgb24(
        RawImage image,
        int frame,
        BayerPattern pattern,
        IReadOnlyList<DevelopLuts> segmentLuts,
        int segmentWidth,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        DevelopLuts[] luts = ToSegmentArray(segmentLuts, segmentWidth);
        int width = image.Width;
        int height = image.Height;
        EnsureExportable((long)width * height, MaxRgb24Pixels, "カラー(RGB24)");
        var rgb24 = new byte[(long)width * height * 3];
        DevelopSegments(image, frame, pattern, luts, segmentWidth, rgb24, progress, cancellationToken);
        return rgb24;
    }

    /// <summary>
    /// 区画ごとにデモザイクして現像LUTを当て、RGB24へ書き込む。バンド単位で処理し境界1行を重複させる。
    /// </summary>
    private static void DevelopSegments(
        RawImage image, int frame, BayerPattern pattern, DevelopLuts[] segmentLuts, int segmentWidth,
        byte[] rgb24, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        int width = image.Width;
        int height = image.Height;
        const int bandRows = 256;

        // バンドごとに確保すると1億画素で約800MBのLOH割り当てになる。
        // 最大バンド高さ×最も広い区画のぶんを一度だけ確保して使い回す
        int maxColumns = 0;
        for (int s = 0; s < segmentLuts.Length; s++)
        {
            (int x0, int x1) = SegmentColumns(s, segmentLuts.Length, segmentWidth, width);
            maxColumns = Math.Max(maxColumns, x1 - x0);
        }

        int maxBandHeight = Math.Min(height, bandRows + 2);
        var mosaic = new ushort[(long)maxColumns * maxBandHeight];
        var rgb16 = new ushort[(long)maxColumns * maxBandHeight * 3];

        for (int bandY = 0; bandY < height; bandY += bandRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rows = Math.Min(bandRows, height - bandY);
            int top = Math.Max(0, bandY - 1);
            int bottom = Math.Min(height - 1, bandY + rows);
            int bandHeight = bottom - top + 1;
            int skip = bandY - top;

            for (int s = 0; s < segmentLuts.Length; s++)
            {
                (int x0, int x1) = SegmentColumns(s, segmentLuts.Length, segmentWidth, width);
                int columns = x1 - x0;
                if (columns == 0)
                {
                    continue;
                }

                for (int y = 0; y < bandHeight; y++)
                {
                    image.CopyRegion(frame, x0, top + y, columns, 1,
                        mosaic.AsSpan(y * columns, columns));
                }

                // 区画を1枚の画像として、区画の左端を列0とする位相でデモザイクする(隣の区画の画素は補間に使わない)
                ColorPipeline.DemosaicBilinear(
                    mosaic, columns, bandHeight, 0, top, pattern, rgb16, cancellationToken);

                // デモザイクは取り消されると例外を出さずに途中で戻る(ViewportRenderer と同じく呼び出し側で確かめる)。
                // 確かめずに進むと、処理されなかった行の rgb16(確保直後の0や前のバンド・区画の値)をLUT変換して、
                // 最後のバンドでは壊れた画像のまま正常に戻る(一括書き出しが正式名で保存して「完了」になる)
                cancellationToken.ThrowIfCancellationRequested();

                DevelopLuts luts = segmentLuts[s];
                Parallel.For(0, rows, r =>
                {
                    int sourceRow = skip + r;
                    long destOffset = (((long)(bandY + r) * width) + x0) * 3;
                    int srcOffset = sourceRow * columns * 3;
                    for (int x = 0; x < columns; x++)
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
            }

            progress?.Report((double)(bandY + rows) / height);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>区画ごとのLUTを検証し、並列処理から読む配列へ写す。</summary>
    /// <exception cref="ArgumentException">区画のLUTが1つもない場合。</exception>
    /// <exception cref="ArgumentOutOfRangeException">区画の幅が1未満の場合。</exception>
    private static T[] ToSegmentArray<T>(IReadOnlyList<T> segmentLuts, int segmentWidth)
    {
        if (segmentLuts.Count == 0)
        {
            throw new ArgumentException("区画のLUTが1つもありません。", nameof(segmentLuts));
        }

        if (segmentWidth < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(segmentWidth), segmentWidth, "区画の幅は1以上である必要があります。");
        }

        var luts = new T[segmentLuts.Count];
        for (int i = 0; i < luts.Length; i++)
        {
            luts[i] = segmentLuts[i];
        }

        return luts;
    }

    /// <summary>
    /// 区画の列範囲 [X0, X1)。最後の区画は画像の右端までを含み、画像の外から始まる区画は空になる。
    /// </summary>
    private static (int X0, int X1) SegmentColumns(int segment, int segmentCount, int segmentWidth, int width)
    {
        int x0 = (int)Math.Min(width, (long)segment * segmentWidth);
        int x1 = segment == segmentCount - 1
            ? width
            : (int)Math.Min(width, (long)(segment + 1) * segmentWidth);
        return (x0, x1);
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
