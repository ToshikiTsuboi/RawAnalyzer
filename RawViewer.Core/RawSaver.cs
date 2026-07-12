using System.Buffers.Binary;

namespace RawViewer.Core;

/// <summary>
/// Rawバイナリの保存。全画素データを行単位でストリーミング書き出しする
/// (表示間引きではなく元データを出力)。
/// </summary>
public static class RawSaver
{
    /// <summary>
    /// 画像を16bitコンテナのRawバイナリとして保存する。
    /// 画素値はフォーマットのビット深度へ逆正規化され、指定の詰め方向・
    /// エンディアンで書き出される。全フレームを連結出力する。
    /// </summary>
    /// <param name="image">保存する画像。</param>
    /// <param name="path">出力先パス。</param>
    /// <param name="packing">出力の詰め方向。</param>
    /// <param name="endianness">出力のバイト順。</param>
    /// <param name="progress">進捗通知(0〜1)。</param>
    /// <param name="cancellationToken">キャンセルトークン。キャンセル時は出力ファイルを削除する。</param>
    /// <exception cref="OperationCanceledException">キャンセルされた場合。</exception>
    public static void Save(
        RawImage image,
        string path,
        BitPacking packing,
        Endianness endianness,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int bitDepth = image.Format.BitDepth;
        int shift = 16 - bitDepth;
        int bytesPerPixel = bitDepth <= 8 ? 1 : 2;
        int width = image.Width;
        long totalRows = (long)image.Height * image.FrameCount;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
            var pixelRow = new ushort[width];
            var byteRow = new byte[width * bytesPerPixel];
            long rowIndex = 0;
            for (int frame = 0; frame < image.FrameCount; frame++)
            {
                for (int y = 0; y < image.Height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    image.CopyRegion(frame, 0, y, width, 1, pixelRow);
                    EncodeRow(pixelRow, byteRow, bitDepth, shift, packing, endianness);
                    stream.Write(byteRow, 0, byteRow.Length);
                    rowIndex++;
                    if ((rowIndex & 255) == 0 || rowIndex == totalRows)
                    {
                        progress?.Report((double)rowIndex / totalRows);
                    }
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

    private static void EncodeRow(
        ReadOnlySpan<ushort> pixels, Span<byte> destination,
        int bitDepth, int shift, BitPacking packing, Endianness endianness)
    {
        if (bitDepth <= 8)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                destination[i] = (byte)(pixels[i] >> 8);
            }

            return;
        }

        int mask = 0xFFFF << shift;
        for (int i = 0; i < pixels.Length; i++)
        {
            ushort container = packing == BitPacking.Lsb
                ? (ushort)(pixels[i] >> shift)
                : (ushort)(pixels[i] & mask);
            if (endianness == Endianness.Little)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(i * 2, 2), container);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(i * 2, 2), container);
            }
        }
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
