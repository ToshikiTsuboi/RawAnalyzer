using System.Buffers.Binary;
using RawViewer.Core;

namespace RawViewer.Tests;

/// <summary>テスト用のRaw/TIFFバイト列生成ヘルパ。</summary>
internal static class TestData
{
    /// <summary>Nビットの生値列をフォーマットに従ってファイルバイト列へエンコードする。</summary>
    public static byte[] EncodeRawFile(ushort[] rawValues, RawFormat format)
    {
        using var stream = new MemoryStream();
        for (long i = 0; i < format.HeaderOffset; i++)
        {
            stream.WriteByte(0xEE);
        }

        Span<byte> buffer = stackalloc byte[2];
        foreach (ushort value in rawValues)
        {
            if (format.BytesPerPixel == 1)
            {
                stream.WriteByte((byte)value);
                continue;
            }

            ushort container = format.Packing == BitPacking.Lsb
                ? value
                : (ushort)(value << (16 - format.BitDepth));
            if (format.Endianness == Endianness.Little)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(buffer, container);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(buffer, container);
            }

            stream.Write(buffer);
        }

        return stream.ToArray();
    }

    /// <summary>決定的なNビットテストパターンを生成する。0と最大値を必ず含む。</summary>
    public static ushort[] MakePattern(int count, int bitDepth)
    {
        int max = (1 << bitDepth) - 1;
        var values = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = (ushort)((i * 2654435761u) % ((uint)max + 1));
        }

        values[0] = 0;
        if (count > 1)
        {
            values[1] = (ushort)max;
        }

        return values;
    }

    /// <summary>一時ファイルへ書き出し、パスを返す。</summary>
    public static string WriteTempFile(byte[] bytes)
    {
        string dir = Path.Combine(Path.GetTempPath(), "RawViewerTests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>最小構成の非圧縮グレースケールTIFFを生成する。</summary>
    public static byte[] BuildTiff(
        ushort[] pixels, int width, int height, int bits, bool bigEndian,
        int rowsPerStrip = int.MaxValue, ushort compression = 1)
    {
        rowsPerStrip = Math.Min(rowsPerStrip, height);
        int stripCount = (height + rowsPerStrip - 1) / rowsPerStrip;
        int bytesPerSample = bits == 8 ? 1 : 2;

        const int entryCount = 9;
        const int ifdOffset = 8;
        int ifdSize = 2 + entryCount * 12 + 4;
        int arraysOffset = ifdOffset + ifdSize;
        int arraysSize = stripCount > 1 ? stripCount * 8 : 0;
        int dataOffset = arraysOffset + arraysSize;

        var stripOffsets = new uint[stripCount];
        var stripByteCounts = new uint[stripCount];
        int cursor = dataOffset;
        for (int s = 0; s < stripCount; s++)
        {
            int rows = Math.Min(rowsPerStrip, height - s * rowsPerStrip);
            stripOffsets[s] = (uint)cursor;
            stripByteCounts[s] = (uint)(rows * width * bytesPerSample);
            cursor += (int)stripByteCounts[s];
        }

        var data = new byte[cursor];
        var w = new TiffWriter(data, bigEndian);

        data[0] = data[1] = bigEndian ? (byte)'M' : (byte)'I';
        w.U16(2, 42);
        w.U32(4, ifdOffset);

        w.U16(ifdOffset, entryCount);
        int e = ifdOffset + 2;
        e = w.Entry(e, 256, 4, 1, (uint)width);
        e = w.Entry(e, 257, 4, 1, (uint)height);
        e = w.Entry(e, 258, 3, 1, (uint)bits);
        e = w.Entry(e, 259, 3, 1, compression);
        e = w.Entry(e, 262, 3, 1, 1);
        e = stripCount == 1
            ? w.Entry(e, 273, 4, 1, stripOffsets[0])
            : w.Entry(e, 273, 4, (uint)stripCount, (uint)arraysOffset);
        e = w.Entry(e, 277, 3, 1, 1);
        e = w.Entry(e, 278, 4, 1, (uint)rowsPerStrip);
        e = stripCount == 1
            ? w.Entry(e, 279, 4, 1, stripByteCounts[0])
            : w.Entry(e, 279, 4, (uint)stripCount, (uint)(arraysOffset + stripCount * 4));
        w.U32(e, 0);

        if (stripCount > 1)
        {
            for (int s = 0; s < stripCount; s++)
            {
                w.U32(arraysOffset + s * 4, stripOffsets[s]);
                w.U32(arraysOffset + stripCount * 4 + s * 4, stripByteCounts[s]);
            }
        }

        for (int i = 0; i < pixels.Length; i++)
        {
            if (bits == 8)
            {
                data[dataOffset + i] = (byte)pixels[i];
            }
            else
            {
                w.U16(dataOffset + i * 2, pixels[i]);
            }
        }

        return data;
    }

    private struct TiffWriter(byte[] data, bool bigEndian)
    {
        public void U16(int offset, ushort value)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
            }
        }

        public void U32(int offset, uint value)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
            }
        }

        public int Entry(int offset, ushort tag, ushort type, uint count, uint value)
        {
            U16(offset, tag);
            U16(offset + 2, type);
            U32(offset + 4, count);
            if (type == 3 && count == 1)
            {
                U16(offset + 8, (ushort)value);
            }
            else
            {
                U32(offset + 8, value);
            }

            return offset + 12;
        }
    }
}
