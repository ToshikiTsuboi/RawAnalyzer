using System.Buffers.Binary;

namespace RawViewer.Core;

/// <summary>
/// 16bit非圧縮グレースケールTIFF(リトルエンディアン、ストリップ形式)の書き出し。
/// 行単位のストリーミングで巨大画像にも対応する(TiffLoaderで再読込可能)。
/// </summary>
public static class TiffWriter
{
    private const int EntryCount = 9;

    /// <summary>
    /// 画像の1フレームを16bitグレースケールTIFFとして保存する。
    /// 画素値は内部表現(16bitフルスケール)をそのまま書き出す。
    /// </summary>
    /// <param name="image">保存する画像。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="path">出力先パス。</param>
    /// <param name="progress">進捗通知(0〜1)。</param>
    /// <param name="cancellationToken">キャンセルトークン。キャンセル時は出力ファイルを削除する。</param>
    /// <param name="rowsPerStripOverride">ストリップあたりの行数の明示指定(既定は約1MB単位)。</param>
    /// <exception cref="NotSupportedException">データが4GBを超えTIFFの32bitオフセットで表現できない場合。</exception>
    /// <exception cref="OperationCanceledException">キャンセルされた場合。</exception>
    public static void SaveGray16(
        RawImage image,
        int frame,
        string path,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        int? rowsPerStripOverride = null)
    {
        int width = image.Width;
        int height = image.Height;
        long rowBytes = (long)width * 2;
        int rowsPerStrip = rowsPerStripOverride
            ?? Math.Max(1, (int)((1 << 20) / rowBytes));
        rowsPerStrip = Math.Clamp(rowsPerStrip, 1, height);
        int stripCount = (height + rowsPerStrip - 1) / rowsPerStrip;

        int ifdSize = 2 + EntryCount * 12 + 4;
        int arraysOffset = 8 + ifdSize;
        int arraysSize = stripCount > 1 ? stripCount * 8 : 0;
        long dataOffset = arraysOffset + arraysSize;
        long totalSize = dataOffset + rowBytes * height;
        if (totalSize > uint.MaxValue)
        {
            throw new NotSupportedException(
                "TIFFの32bitオフセット上限(4GB)を超えるため保存できません。raw形式を使用してください。");
        }

        try
        {
            using var stream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
            WriteHeaderAndIfd(
                stream, width, height, rowsPerStrip, stripCount, arraysOffset, dataOffset, rowBytes);

            // 画素データ(リトルエンディアン)
            var pixelRow = new ushort[width];
            var byteRow = new byte[width * 2];
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                image.CopyRegion(frame, 0, y, width, 1, pixelRow);
                for (int x = 0; x < width; x++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        byteRow.AsSpan(x * 2, 2), pixelRow[x]);
                }

                stream.Write(byteRow, 0, byteRow.Length);
                if ((y & 255) == 0 || y == height - 1)
                {
                    progress?.Report((double)(y + 1) / height);
                }
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(path);
            throw;
        }
        catch (Exception)
        {
            TryDelete(path);
            throw;
        }
    }

    private static void WriteHeaderAndIfd(
        FileStream stream, int width, int height, int rowsPerStrip, int stripCount,
        int arraysOffset, long dataOffset, long rowBytes)
    {
        var header = new byte[arraysOffset + (stripCount > 1 ? stripCount * 8 : 0)];
        header[0] = (byte)'I';
        header[1] = (byte)'I';
        WriteU16(header, 2, 42);
        WriteU32(header, 4, 8);

        WriteU16(header, 8, EntryCount);
        int entry = 10;
        entry = WriteEntry(header, entry, 256, 4, 1, (uint)width);
        entry = WriteEntry(header, entry, 257, 4, 1, (uint)height);
        entry = WriteEntry(header, entry, 258, 3, 1, 16);
        entry = WriteEntry(header, entry, 259, 3, 1, 1);
        entry = WriteEntry(header, entry, 262, 3, 1, 1);
        entry = stripCount == 1
            ? WriteEntry(header, entry, 273, 4, 1, (uint)dataOffset)
            : WriteEntry(header, entry, 273, 4, (uint)stripCount, (uint)arraysOffset);
        entry = WriteEntry(header, entry, 277, 3, 1, 1);
        entry = WriteEntry(header, entry, 278, 4, 1, (uint)rowsPerStrip);
        entry = stripCount == 1
            ? WriteEntry(header, entry, 279, 4, 1, (uint)(rowBytes * height))
            : WriteEntry(header, entry, 279, 4, (uint)stripCount,
                (uint)(arraysOffset + stripCount * 4));
        WriteU32(header, entry, 0);

        if (stripCount > 1)
        {
            long offset = dataOffset;
            for (int strip = 0; strip < stripCount; strip++)
            {
                int rows = Math.Min(rowsPerStrip, height - strip * rowsPerStrip);
                long bytes = rowBytes * rows;
                WriteU32(header, arraysOffset + strip * 4, (uint)offset);
                WriteU32(header, arraysOffset + stripCount * 4 + strip * 4, (uint)bytes);
                offset += bytes;
            }
        }

        stream.Write(header, 0, header.Length);
    }

    private static void WriteU16(byte[] buffer, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), value);
    }

    private static void WriteU32(byte[] buffer, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);
    }

    private static int WriteEntry(
        byte[] buffer, int offset, ushort tag, ushort type, uint count, uint value)
    {
        WriteU16(buffer, offset, tag);
        WriteU16(buffer, offset + 2, type);
        WriteU32(buffer, offset + 4, count);
        if (type == 3 && count == 1)
        {
            WriteU16(buffer, offset + 8, (ushort)value);
        }
        else
        {
            WriteU32(buffer, offset + 8, value);
        }

        return offset + 12;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
