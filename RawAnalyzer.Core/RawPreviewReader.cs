namespace RawAnalyzer.Core;

/// <summary>先頭フレームから抽出した、16bit 正規化済みのプレビュー。</summary>
/// <param name="Width">間引き後の予定幅。</param>
/// <param name="Height">間引き後の予定高さ。</param>
/// <param name="Pixels">読み取れた行の画素を行優先で格納する。長さは幅×<paramref name="RowsRead"/>。</param>
/// <param name="RowsRead">完全に読み取れた間引き後の行数。</param>
/// <param name="Step">元画像の行・列で使った間引き間隔。</param>
public sealed record RawPreview(int Width, int Height, ushort[] Pixels, int RowsRead, long Step);

/// <summary>RAW の先頭フレームを、必要な画素だけシークして読み取る。</summary>
public static class RawPreviewReader
{
    /// <summary>ファイルの先頭フレームから間引きプレビューを読み取る。</summary>
    /// <param name="path">RAW ファイルのパス。</param>
    /// <param name="format">ヘッダオフセットと画素の解釈を含む形式。</param>
    /// <param name="maxWidth">間引き後の最大幅。</param>
    /// <param name="maxHeight">間引き後の最大高さ。</param>
    /// <param name="cancellationToken">読み取りの取り消し。</param>
    /// <returns>間引き後の画素と、実際に読み取れた行数。</returns>
    /// <remarks>抽出位置と端数の扱いはストリームを受け取るオーバーロードと同じ。</remarks>
    public static RawPreview ReadDecimated(string path, RawFormat format, int maxWidth, int maxHeight,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 0, FileOptions.RandomAccess);
        return ReadDecimated(stream, format, maxWidth, maxHeight, cancellationToken);
    }

    /// <summary>読み取り・シークが可能なストリームから間引きプレビューを読み取る。</summary>
    /// <param name="stream">長さを取得でき、位置 0 がファイルの先頭を表すストリーム。読み取り後も開いたままにする。</param>
    /// <param name="format">ヘッダオフセットと画素の解釈を含む形式。</param>
    /// <param name="maxWidth">間引き後の最大幅。正の値。</param>
    /// <param name="maxHeight">間引き後の最大高さ。正の値。</param>
    /// <param name="cancellationToken">読み取りの取り消し。各行と間引き画素の間で確認する。</param>
    /// <returns>予定寸法と、完全に読み取れた行だけの画素。</returns>
    /// <remarks>
    /// 先頭フレームの左上 (0, 0) を起点に、行・列を同じ間隔で抽出する。
    /// 間隔は幅と高さがそれぞれの上限に収まる最小の正整数。Bayer で間引く場合は次の偶数へ切り上げ、
    /// 左上と同じ色の画素を採る。間引きが不要なら Bayer モザイクを維持する。
    /// 末尾に元画像の1行分が揃っていない場合、その行は返さない。HDR の行を並べ替えない。
    /// </remarks>
    public static RawPreview ReadDecimated(Stream stream, RawFormat format, int maxWidth, int maxHeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        cancellationToken.ThrowIfCancellationRequested();
        format.Validate();
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("読み取りとシークが可能なストリームが必要です。", nameof(stream));
        }

        long step = Math.Max(CeilingDivide(format.Width, maxWidth), CeilingDivide(format.Height, maxHeight));
        if (format.Bayer != BayerPattern.None && step > 1 && (step & 1) != 0)
        {
            step++;
        }

        int width = (int)CeilingDivide(format.Width, step);
        int height = (int)CeilingDivide(format.Height, step);
        int bytesPerPixel = format.BytesPerPixel;
        long rowBytes = (long)format.Width * bytesPerPixel;
        long completeRows = Math.Min(format.Height, Math.Max(0, stream.Length - format.HeaderOffset) / rowBytes);
        cancellationToken.ThrowIfCancellationRequested();
        int availableRows = (int)CeilingDivide(completeRows, step);
        var pixels = new ushort[checked(width * availableRows)];
        if (availableRows == 0)
        {
            return new RawPreview(width, height, pixels, 0, step);
        }

        var row = new byte[checked(width * bytesPerPixel)];
        int rowsRead = 0;
        for (; rowsRead < availableRows; rowsRead++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long offset = format.HeaderOffset + rowsRead * step * rowBytes;
            if (!ReadRow(stream, row, offset, step, bytesPerPixel, cancellationToken))
            {
                break;
            }

            PixelNormalizer.Normalize(row, pixels.AsSpan(rowsRead * width, width),
                format.BitDepth, format.Packing, format.Endianness);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (rowsRead < availableRows)
        {
            Array.Resize(ref pixels, rowsRead * width);
        }

        return new RawPreview(width, height, pixels, rowsRead, step);
    }

    private static long CeilingDivide(long value, long divisor) => (value + divisor - 1) / divisor;

    private static bool ReadRow(Stream stream, Span<byte> row, long offset, long step, int bytesPerPixel,
        CancellationToken cancellationToken)
    {
        if (step == 1)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            return ReadFully(stream, row, cancellationToken);
        }

        for (int i = 0; i < row.Length; i += bytesPerPixel)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Seek(offset + i * step, SeekOrigin.Begin);
            if (!ReadFully(stream, row.Slice(i, bytesPerPixel), cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReadFully(Stream stream, Span<byte> destination, CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(destination);
            if (read == 0)
            {
                return false;
            }

            destination = destination[read..];
        }

        return true;
    }
}
