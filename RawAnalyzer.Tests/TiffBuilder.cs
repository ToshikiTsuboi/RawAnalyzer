using System.Buffers.Binary;

namespace RawAnalyzer.Tests;

/// <summary>拡張子を指定できる一時ファイル。</summary>
internal sealed class TempTiff : IDisposable
{
    public string Path { get; }

    private TempTiff(string path)
    {
        Path = path;
    }

    public static TempTiff Write(byte[] bytes, string extension = ".tif")
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, bytes);
        return new TempTiff(path);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// クラシック/BigTIFF、LE/BE、ストリップ/タイル、SubIFD、任意タグを書ける小さなTIFFライタ。
/// テストで使うTIFFのバイト列はすべてここで作る。
/// </summary>
/// <remarks>
/// 各ページは画素ブロック → IFD → インライン化できないタグ値の順に並ぶ。バイト位置に依存する
/// テストは固定オフセットを決め打ちせず、ヘッダと IFD を辿って位置を求めること(TiffStackTests 参照)。
/// </remarks>
internal sealed class TiffBuilder(bool bigEndian = false, bool bigTiff = false)
{
    internal sealed class Page
    {
        /// <summary>タグ番号 → (型, 値)。値は long[] か byte[]。</summary>
        public Dictionary<ushort, (ushort Type, object Values)> Tags { get; } = new();

        public List<byte[]> Blocks { get; } = new();

        public int[]? BlockOrder { get; set; }

        public bool Tiles { get; set; }

        public List<Page> SubIfds { get; } = new();

        /// <summary>最後のブロックの直後に置く生データ(ImageJの連続フレーム用)。</summary>
        public byte[]? Trailer { get; set; }

        /// <summary>このページのバイト列(画素ブロックとIFD)の前に置く未使用バイト数。IFDが詰めて並ばないファイルを作る。</summary>
        public int GapBefore { get; set; }
    }

    private readonly List<byte> _buf = new();

    public static Page GrayPage(
        int width, int height, int bits, byte[] samples, int photometric = 1, int compression = 1,
        int sampleFormat = 1, int samplesPerPixel = 1, int rowsPerStrip = int.MaxValue)
    {
        var page = new Page();
        page.Tags[256] = (4, new long[] { width });
        page.Tags[257] = (4, new long[] { height });
        page.Tags[258] = (3, Enumerable.Repeat((long)bits, samplesPerPixel).ToArray());
        page.Tags[259] = (3, new long[] { compression });
        page.Tags[262] = (3, new long[] { photometric });
        page.Tags[277] = (3, new long[] { samplesPerPixel });
        if (sampleFormat != 1)
        {
            page.Tags[339] = (3, Enumerable.Repeat((long)sampleFormat, samplesPerPixel).ToArray());
        }

        int rows = Math.Min(rowsPerStrip, height);
        page.Tags[278] = (4, new long[] { rows });
        int rowBytes = ((width * bits * samplesPerPixel) + 7) / 8;
        for (int y = 0; y < height; y += rows)
        {
            int n = Math.Min(rows, height - y);
            page.Blocks.Add(samples.AsSpan(y * rowBytes, n * rowBytes).ToArray());
        }

        return page;
    }

    public static Page RgbPage(int width, int height, byte[] samples)
    {
        var page = GrayPage(width, height, 8, samples, photometric: 2, samplesPerPixel: 3);
        return page;
    }

    public static Page TiledGrayPage(
        int width, int height, int bits, byte[] samples, int tileWidth, int tileHeight)
    {
        var page = GrayPage(width, height, bits, samples);
        page.Tags.Remove(278);
        page.Blocks.Clear();
        page.Tiles = true;
        page.Tags[322] = (4, new long[] { tileWidth });
        page.Tags[323] = (4, new long[] { tileHeight });
        int bytesPer = bits / 8;
        for (int ty = 0; ty < height; ty += tileHeight)
        {
            for (int tx = 0; tx < width; tx += tileWidth)
            {
                var tile = new byte[tileWidth * tileHeight * bytesPer];
                for (int r = 0; r < tileHeight; r++)
                {
                    for (int c = 0; c < tileWidth; c++)
                    {
                        int x = tx + c;
                        int y = ty + r;
                        if (x < width && y < height)
                        {
                            samples.AsSpan(((y * width) + x) * bytesPer, bytesPer)
                                .CopyTo(tile.AsSpan(((r * tileWidth) + c) * bytesPer));
                        }
                    }
                }

                page.Blocks.Add(tile);
            }
        }

        return page;
    }

    /// <summary>行頭バイト境界・MSBファーストで詰め込む(TIFFのFillOrder=1)。</summary>
    public static byte[] PackRows(int[] values, int width, int bits)
    {
        int height = values.Length / width;
        int rowBytes = ((width * bits) + 7) / 8;
        var bytes = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            long acc = 0;
            int n = 0;
            int pos = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                acc = (acc << bits) | (uint)values[(y * width) + x];
                n += bits;
                while (n >= 8)
                {
                    bytes[pos++] = (byte)(acc >> (n - 8));
                    n -= 8;
                }
            }

            if (n > 0)
            {
                bytes[pos] = (byte)(acc << (8 - n));
            }
        }

        return bytes;
    }

    /// <summary>8/16bit の整数値列をサンプルバイト列にする(16bit は指定のバイト順で書く)。</summary>
    public static byte[] SampleBytes(ushort[] values, int bits, bool bigEndian = false)
    {
        if (bits == 8)
        {
            return Array.ConvertAll(values, v => checked((byte)v));
        }

        if (bits != 16)
        {
            throw new ArgumentOutOfRangeException(nameof(bits), bits, "8bit か 16bit のみ扱えます。");
        }

        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), values[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
            }
        }

        return bytes;
    }

    public byte[] Build(params Page[] pages)
    {
        _buf.Clear();
        _buf.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        if (bigTiff)
        {
            AddU16(43);
            AddU16(8);
            AddU16(0);
            AddU64(0);
        }
        else
        {
            AddU16(42);
            AddU32(0);
        }

        long linkPosition = bigTiff ? 8 : 4;
        foreach (Page page in pages)
        {
            long ifd = WritePage(page);
            PatchOffset(linkPosition, ifd);
            linkPosition = _nextLink;
        }

        return _buf.ToArray();
    }

    private long _nextLink;

    private long WritePage(Page page)
    {
        var subOffsets = new List<long>();
        foreach (Page sub in page.SubIfds)
        {
            subOffsets.Add(WritePage(sub));
        }

        if (page.GapBefore > 0)
        {
            _buf.AddRange(new byte[page.GapBefore]);
        }

        var tags = new Dictionary<ushort, (ushort Type, object Values)>(page.Tags);
        if (page.Blocks.Count > 0)
        {
            int[] order = page.BlockOrder ?? Enumerable.Range(0, page.Blocks.Count).ToArray();
            var offsets = new long[page.Blocks.Count];
            for (int k = 0; k < order.Length; k++)
            {
                int i = order[k];
                Align();
                offsets[i] = _buf.Count;
                _buf.AddRange(page.Blocks[i]);
                if (k == order.Length - 1 && page.Trailer is not null)
                {
                    _buf.AddRange(page.Trailer);
                }
            }

            ushort offsetType = bigTiff ? (ushort)16 : (ushort)4;
            tags[page.Tiles ? (ushort)324 : (ushort)273] = (offsetType, offsets);
            tags[page.Tiles ? (ushort)325 : (ushort)279] = (4, page.Blocks.Select(b => (long)b.Length).ToArray());
        }

        if (subOffsets.Count > 0)
        {
            tags[330] = (bigTiff ? (ushort)18 : (ushort)13, subOffsets.ToArray());
        }

        Align();
        long ifdOffset = _buf.Count;
        int entrySize = bigTiff ? 20 : 12;
        int inline = bigTiff ? 8 : 4;
        long extraBase = ifdOffset + (bigTiff ? 8 : 2) + ((long)tags.Count * entrySize) + (bigTiff ? 8 : 4);
        var extra = new List<byte>();
        var entries = new List<byte>();
        foreach (ushort tag in tags.Keys.OrderBy(t => t))
        {
            (ushort type, object values) = tags[tag];
            byte[] raw;
            long count;
            if (values is byte[] bytes)
            {
                raw = bytes;
                count = bytes.Length;
            }
            else
            {
                var numbers = (long[])values;
                int size = type switch { 1 or 2 or 7 => 1, 3 => 2, 4 or 13 => 4, 16 or 17 or 18 => 8, _ => throw new NotSupportedException() };
                raw = new byte[numbers.Length * size];
                for (int i = 0; i < numbers.Length; i++)
                {
                    WriteNumber(raw.AsSpan(i * size, size), numbers[i]);
                }

                count = numbers.Length;
            }

            var entry = new byte[entrySize];
            WriteNumber(entry.AsSpan(0, 2), tag);
            WriteNumber(entry.AsSpan(2, 2), type);
            WriteNumber(entry.AsSpan(4, bigTiff ? 8 : 4), count);
            int fieldOffset = bigTiff ? 12 : 8;
            if (raw.Length <= inline)
            {
                raw.CopyTo(entry.AsSpan(fieldOffset));
            }
            else
            {
                if (extra.Count % 2 == 1)
                {
                    extra.Add(0);
                }

                WriteNumber(entry.AsSpan(fieldOffset, inline), extraBase + extra.Count);
                extra.AddRange(raw);
            }

            entries.AddRange(entry);
        }

        if (bigTiff)
        {
            AddU64((ulong)tags.Count);
        }
        else
        {
            AddU16((ushort)tags.Count);
        }

        _buf.AddRange(entries);
        _nextLink = _buf.Count;
        if (bigTiff)
        {
            AddU64(0);
        }
        else
        {
            AddU32(0);
        }

        _buf.AddRange(extra);
        return ifdOffset;
    }

    private void Align()
    {
        if (_buf.Count % 2 == 1)
        {
            _buf.Add(0);
        }
    }

    private void PatchOffset(long position, long value)
    {
        var tmp = new byte[bigTiff ? 8 : 4];
        WriteNumber(tmp, value);
        for (int i = 0; i < tmp.Length; i++)
        {
            _buf[(int)position + i] = tmp[i];
        }
    }

    private void WriteNumber(Span<byte> destination, long value)
    {
        switch (destination.Length)
        {
            case 1:
                destination[0] = (byte)value;
                break;
            case 2:
                if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)value);
                else BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)value);
                break;
            case 4:
                if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)value);
                else BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)value);
                break;
            default:
                if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)value);
                else BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)value);
                break;
        }
    }

    private void AddU16(ushort value)
    {
        var tmp = new byte[2];
        WriteNumber(tmp, value);
        _buf.AddRange(tmp);
    }

    private void AddU32(uint value)
    {
        var tmp = new byte[4];
        WriteNumber(tmp, value);
        _buf.AddRange(tmp);
    }

    private void AddU64(ulong value)
    {
        var tmp = new byte[8];
        WriteNumber(tmp, (long)value);
        _buf.AddRange(tmp);
    }
}
