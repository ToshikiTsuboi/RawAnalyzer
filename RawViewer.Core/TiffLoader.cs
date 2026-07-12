using System.Buffers.Binary;

namespace RawViewer.Core;

/// <summary>
/// 8/16bit 非圧縮グレースケールTIFF(ストリップ形式)の最小パーサ。
/// リトル/ビッグエンディアン両対応。CoreはWICを参照できないため自前実装。
/// </summary>
public static class TiffLoader
{
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagBitsPerSample = 258;
    private const ushort TagCompression = 259;
    private const ushort TagStripOffsets = 273;
    private const ushort TagSamplesPerPixel = 277;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;

    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;

    /// <summary>
    /// TIFFファイルを読み込み、16bitフルスケールへ正規化したRawImageを返す。
    /// </summary>
    /// <param name="path">TIFFファイルのパス。</param>
    /// <returns>読み込まれた画像。</returns>
    /// <exception cref="InvalidDataException">TIFFとして不正、またはサポート外の形式の場合。</exception>
    public static RawImage Load(string path)
    {
        return Load(File.ReadAllBytes(path));
    }

    /// <summary>
    /// メモリ上のTIFFデータを読み込み、16bitフルスケールへ正規化したRawImageを返す。
    /// </summary>
    /// <param name="data">TIFFファイル全体のバイト列。</param>
    /// <returns>読み込まれた画像。</returns>
    /// <exception cref="InvalidDataException">TIFFとして不正、またはサポート外の形式の場合。</exception>
    public static RawImage Load(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            throw new InvalidDataException("TIFFヘッダが不足しています。");
        }

        bool bigEndian = data[0] == (byte)'M' && data[1] == (byte)'M';
        bool littleEndian = data[0] == (byte)'I' && data[1] == (byte)'I';
        if (!bigEndian && !littleEndian)
        {
            throw new InvalidDataException("TIFFのバイトオーダーマークが不正です。");
        }

        if (ReadU16(data, 2, bigEndian) != 42)
        {
            throw new InvalidDataException("TIFFのマジックナンバーが不正です。");
        }

        long ifdOffset = ReadU32(data, 4, bigEndian);
        var entries = ReadIfd(data, ifdOffset, bigEndian);

        uint width = GetScalar(entries, data, TagImageWidth, bigEndian)
            ?? throw new InvalidDataException("ImageWidthタグがありません。");
        uint height = GetScalar(entries, data, TagImageLength, bigEndian)
            ?? throw new InvalidDataException("ImageLengthタグがありません。");
        uint bits = GetScalar(entries, data, TagBitsPerSample, bigEndian)
            ?? throw new InvalidDataException("BitsPerSampleタグがありません。");
        uint compression = GetScalar(entries, data, TagCompression, bigEndian) ?? 1;
        uint samplesPerPixel = GetScalar(entries, data, TagSamplesPerPixel, bigEndian) ?? 1;
        uint rowsPerStrip = GetScalar(entries, data, TagRowsPerStrip, bigEndian) ?? height;

        if (compression != 1)
        {
            throw new InvalidDataException($"非圧縮TIFFのみサポートします(Compression={compression})。");
        }

        if (samplesPerPixel != 1)
        {
            throw new InvalidDataException(
                $"グレースケールTIFFのみサポートします(SamplesPerPixel={samplesPerPixel})。");
        }

        if (bits != 8 && bits != 16)
        {
            throw new InvalidDataException($"8/16bit TIFFのみサポートします(BitsPerSample={bits})。");
        }

        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw new InvalidDataException("画像サイズが不正です。");
        }

        uint[] stripOffsets = GetArray(entries, data, TagStripOffsets, bigEndian)
            ?? throw new InvalidDataException("StripOffsetsタグがありません。");
        uint[] stripByteCounts = GetArray(entries, data, TagStripByteCounts, bigEndian)
            ?? throw new InvalidDataException("StripByteCountsタグがありません。");
        if (stripOffsets.Length != stripByteCounts.Length)
        {
            throw new InvalidDataException("StripOffsetsとStripByteCountsの個数が一致しません。");
        }

        int bytesPerSample = bits == 8 ? 1 : 2;
        var pixels = new ushort[(long)width * height];
        long pixelIndex = 0;
        long remainingRows = height;

        for (int strip = 0; strip < stripOffsets.Length; strip++)
        {
            long stripRows = Math.Min(rowsPerStrip, remainingRows);
            long expectedBytes = stripRows * width * bytesPerSample;
            if (stripByteCounts[strip] != expectedBytes)
            {
                throw new InvalidDataException(
                    $"ストリップ{strip}のバイト数が不正です(期待 {expectedBytes}, 実際 {stripByteCounts[strip]})。");
            }

            long offset = stripOffsets[strip];
            if (offset + expectedBytes > data.Length)
            {
                throw new InvalidDataException($"ストリップ{strip}がファイル範囲外を指しています。");
            }

            ReadOnlySpan<byte> stripData = data.Slice((int)offset, (int)expectedBytes);
            long stripPixels = stripRows * width;
            if (bits == 8)
            {
                for (int i = 0; i < stripPixels; i++)
                {
                    pixels[pixelIndex + i] = (ushort)(stripData[i] << 8);
                }
            }
            else
            {
                for (int i = 0; i < stripPixels; i++)
                {
                    pixels[pixelIndex + i] = ReadU16(stripData, i * 2, bigEndian);
                }
            }

            pixelIndex += stripPixels;
            remainingRows -= stripRows;
        }

        if (remainingRows > 0)
        {
            throw new InvalidDataException("ストリップが画像の全行をカバーしていません。");
        }

        var format = new RawFormat
        {
            Width = (int)width,
            Height = (int)height,
            BitDepth = (int)bits,
            Packing = BitPacking.Lsb,
            Endianness = bigEndian ? Endianness.Big : Endianness.Little,
        };
        return new RawImage(format, pixels);
    }

    private readonly record struct IfdEntry(ushort Tag, ushort Type, uint Count, int ValueFieldOffset);

    private static List<IfdEntry> ReadIfd(ReadOnlySpan<byte> data, long ifdOffset, bool bigEndian)
    {
        if (ifdOffset < 8 || ifdOffset + 2 > data.Length)
        {
            throw new InvalidDataException("IFDオフセットが不正です。");
        }

        int entryCount = ReadU16(data, (int)ifdOffset, bigEndian);
        long end = ifdOffset + 2 + entryCount * 12L + 4;
        if (end > data.Length)
        {
            throw new InvalidDataException("IFDがファイル範囲外を指しています。");
        }

        var entries = new List<IfdEntry>(entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            int entryOffset = (int)(ifdOffset + 2 + i * 12);
            entries.Add(new IfdEntry(
                ReadU16(data, entryOffset, bigEndian),
                ReadU16(data, entryOffset + 2, bigEndian),
                ReadU32(data, entryOffset + 4, bigEndian),
                entryOffset + 8));
        }

        return entries;
    }

    private static uint? GetScalar(
        List<IfdEntry> entries, ReadOnlySpan<byte> data, ushort tag, bool bigEndian)
    {
        uint[]? values = GetArray(entries, data, tag, bigEndian);
        return values is { Length: > 0 } ? values[0] : null;
    }

    private static uint[]? GetArray(
        List<IfdEntry> entries, ReadOnlySpan<byte> data, ushort tag, bool bigEndian)
    {
        foreach (IfdEntry entry in entries)
        {
            if (entry.Tag != tag)
            {
                continue;
            }

            int valueSize = entry.Type switch
            {
                TypeShort => 2,
                TypeLong => 4,
                _ => throw new InvalidDataException($"タグ{tag}の型{entry.Type}はサポートされません。"),
            };

            long totalSize = (long)valueSize * entry.Count;
            int valueOffset = totalSize <= 4
                ? entry.ValueFieldOffset
                : checked((int)ReadU32(data, entry.ValueFieldOffset, bigEndian));
            if (valueOffset + totalSize > data.Length)
            {
                throw new InvalidDataException($"タグ{tag}の値がファイル範囲外を指しています。");
            }

            var values = new uint[entry.Count];
            for (int i = 0; i < entry.Count; i++)
            {
                values[i] = valueSize == 2
                    ? ReadU16(data, valueOffset + i * 2, bigEndian)
                    : ReadU32(data, valueOffset + i * 4, bigEndian);
            }

            return values;
        }

        return null;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }
}
