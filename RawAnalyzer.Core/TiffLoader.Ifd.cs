using System.Buffers.Binary;
using System.Text;

namespace RawAnalyzer.Core;

public static unsafe partial class TiffLoader
{
    // ------------------------------------------------------------------ ヘッダ・IFD

    /// <summary>ファイル全体の書式(バイト順、クラシック/BigTIFF)。</summary>
    private readonly record struct TiffHeader(bool BigEndian, bool BigTiff)
    {
        public int EntrySize => BigTiff ? 20 : 12;

        public int EntryCountSize => BigTiff ? 8 : 2;

        public int OffsetSize => BigTiff ? 8 : 4;

        public int InlineValueSize => BigTiff ? 8 : 4;
    }

    private static bool TryParseHeader(TiffBytes data, out TiffHeader header, out string reason)
    {
        header = default;
        bool bigEndian = data[0] == (byte)'M' && data[1] == (byte)'M';
        bool littleEndian = data[0] == (byte)'I' && data[1] == (byte)'I';
        if (!bigEndian && !littleEndian)
        {
            reason = "TIFFのバイトオーダーマークが不正です。";
            return false;
        }

        ushort magic = data.ReadU16(2, bigEndian);
        if (magic == 42)
        {
            header = new TiffHeader(bigEndian, false);
            reason = "";
            return true;
        }

        if (magic == 43)
        {
            if (data.Length < 16 || data.ReadU16(4, bigEndian) != 8 || data.ReadU16(6, bigEndian) != 0)
            {
                reason = "BigTIFFのヘッダが不正です。";
                return false;
            }

            header = new TiffHeader(bigEndian, true);
            reason = "";
            return true;
        }

        reason = "TIFFのマジックナンバーが不正です。";
        return false;
    }

    private static long ReadOffset(TiffBytes data, long position, TiffHeader header)
    {
        if (!header.BigTiff)
        {
            return data.ReadU32(position, header.BigEndian);
        }

        ulong value = data.ReadU64(position, header.BigEndian);
        if (value > long.MaxValue)
        {
            throw new InvalidDataException("BigTIFFのオフセットが大きすぎます。");
        }

        return (long)value;
    }

    private static long FirstIfdOffset(TiffBytes data, TiffHeader header)
    {
        return ReadOffset(data, header.BigTiff ? 8 : 4, header);
    }

    private readonly record struct IfdEntry(ushort Tag, ushort Type, long Count, long ValueFieldOffset);

    private static long ReadEntryCount(TiffBytes data, long ifdOffset, TiffHeader header)
    {
        if (!header.BigTiff)
        {
            return data.ReadU16(ifdOffset, header.BigEndian);
        }

        ulong count = data.ReadU64(ifdOffset, header.BigEndian);
        if (count > 1 << 20)
        {
            throw new InvalidDataException("IFDのエントリ数が大きすぎます。");
        }

        return (long)count;
    }

    private static List<IfdEntry> ReadIfd(TiffBytes data, long ifdOffset, TiffHeader header)
    {
        // 範囲検査は加算せず引き算で書く。BigTIFF・LONG8 のオフセットは long.MaxValue 近くまで入り得て、
        // 位置 + 長さ が負へ回り込むと検査を素通りする(以下の範囲検査も同じ)
        if (ifdOffset < 8 || ifdOffset > data.Length - header.EntryCountSize)
        {
            throw new InvalidDataException("IFDオフセットが不正です。");
        }

        long entryCount = ReadEntryCount(data, ifdOffset, header);
        long first = ifdOffset + header.EntryCountSize;
        long end = first + (entryCount * header.EntrySize) + header.OffsetSize;
        if (end > data.Length)
        {
            throw new InvalidDataException("IFDがファイル範囲外を指しています。");
        }

        var entries = new List<IfdEntry>((int)entryCount);
        for (long i = 0; i < entryCount; i++)
        {
            long entryOffset = first + (i * header.EntrySize);
            long count = header.BigTiff
                ? (long)data.ReadU64(entryOffset + 4, header.BigEndian)
                : data.ReadU32(entryOffset + 4, header.BigEndian);
            entries.Add(new IfdEntry(
                data.ReadU16(entryOffset, header.BigEndian),
                data.ReadU16(entryOffset + 2, header.BigEndian),
                count,
                entryOffset + 4 + (header.BigTiff ? 8 : 4)));
        }

        return entries;
    }

    private static long NextIfdOffset(TiffBytes data, long ifdOffset, TiffHeader header)
    {
        long entryCount = ReadEntryCount(data, ifdOffset, header);
        return ReadOffset(data, ifdOffset + header.EntryCountSize + (entryCount * header.EntrySize), header);
    }

    /// <summary>メインIFDチェーンを辿る。</summary>
    private static List<long> ReadChain(TiffBytes data, TiffHeader header, CancellationToken ct)
    {
        var offsets = new List<long>();
        var visited = new HashSet<long>();
        long offset = FirstIfdOffset(data, header);
        while (offset != 0)
        {
            ct.ThrowIfCancellationRequested();
            if (offset < 8 || !visited.Add(offset))
            {
                throw new InvalidDataException("TIFFのページ参照が不正、または循環しています。");
            }

            if (offsets.Count >= MaxPages)
            {
                throw new NotSupportedException($"TIFFは最大{MaxPages:N0}ページまで対応します。");
            }

            offsets.Add(offset);
            offset = NextIfdOffset(data, offset, header);
        }

        if (offsets.Count == 0)
        {
            throw new InvalidDataException("TIFFに画像ページがありません。");
        }

        return offsets;
    }

    // ------------------------------------------------------------------ タグ値

    /// <summary>
    /// タグ値の所在。<see cref="Count"/> は検証済みだが、確保はまだ行っていない。
    /// </summary>
    private readonly record struct TagValues(long Offset, int ValueSize, long Count, ushort Type);

    /// <summary>
    /// 配列として読み出すタグの要素数上限。
    /// </summary>
    /// <remarks>
    /// 破損・悪意あるIFDが巨大な Count を宣言してもファイル長の範囲チェックだけでは
    /// 数GBの確保が通ってしまうため、確保前に妥当な上限で弾く。
    /// </remarks>
    private const long MaxTagArrayCount = 1 << 24;

    private static int TypeSize(ushort type)
    {
        return type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 13 => 4,
            16 or 17 or 18 => 8,
            _ => 0,
        };
    }

    private static TagValues? FindTagValues(
        List<IfdEntry> entries, TiffBytes data, ushort tag, TiffHeader header)
    {
        foreach (IfdEntry entry in entries)
        {
            if (entry.Tag != tag)
            {
                continue;
            }

            int valueSize = TypeSize(entry.Type);
            if (valueSize == 0)
            {
                throw new InvalidDataException($"タグ{tag}の型{entry.Type}はサポートされません。");
            }

            if (entry.Count < 0 || entry.Count > long.MaxValue / 8)
            {
                throw new InvalidDataException($"タグ{tag}の要素数が不正です。");
            }

            long totalSize = valueSize * entry.Count;

            // インライン格納(クラシック4byte、BigTIFF 8byte以下)か、オフセット参照か
            long valueOffset = totalSize <= header.InlineValueSize
                ? entry.ValueFieldOffset
                : ReadOffset(data, entry.ValueFieldOffset, header);
            if (valueOffset < 0 || valueOffset > data.Length - totalSize)
            {
                throw new InvalidDataException($"タグ{tag}の値がファイル範囲外を指しています。");
            }

            return new TagValues(valueOffset, valueSize, entry.Count, entry.Type);
        }

        return null;
    }

    private static long ReadValue(TiffBytes data, long offset, int size, bool bigEndian)
    {
        return size switch
        {
            1 => data[offset],
            2 => data.ReadU16(offset, bigEndian),
            4 => data.ReadU32(offset, bigEndian),
            _ => (long)data.ReadU64(offset, bigEndian),
        };
    }

    private static long? GetScalar(
        List<IfdEntry> entries, TiffBytes data, ushort tag, TiffHeader header)
    {
        // 先頭の1値しか使わないので、宣言された要素数がいくら大きくても確保しない
        if (FindTagValues(entries, data, tag, header) is not { Count: > 0 } found)
        {
            return null;
        }

        return ReadValue(data, found.Offset, found.ValueSize, header.BigEndian);
    }

    private static long[]? GetArray(
        List<IfdEntry> entries, TiffBytes data, ushort tag, TiffHeader header)
    {
        if (FindTagValues(entries, data, tag, header) is not { } found)
        {
            return null;
        }

        if (found.Count > MaxTagArrayCount)
        {
            throw new InvalidDataException(
                $"タグ{tag}の要素数({found.Count})が上限({MaxTagArrayCount})を超えています。");
        }

        var values = new long[found.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = ReadValue(data, found.Offset + ((long)i * found.ValueSize), found.ValueSize, header.BigEndian);
        }

        return values;
    }

    private static string? GetAscii(
        List<IfdEntry> entries, TiffBytes data, ushort tag, TiffHeader header, int maxLength)
    {
        if (FindTagValues(entries, data, tag, header) is not { Count: > 0, ValueSize: 1 } found)
        {
            return null;
        }

        int length = (int)Math.Min(found.Count, maxLength);
        ReadOnlySpan<byte> bytes = data.Slice(found.Offset, length);
        int terminator = bytes.IndexOf((byte)0);
        if (terminator >= 0)
        {
            bytes = bytes[..terminator];
        }

        return Encoding.ASCII.GetString(bytes);
    }

    // ------------------------------------------------------------------ バイト列アクセス

    /// <summary>
    /// ファイル全体へのlongオフセットアクセス。クラシックTIFFのオフセット上限は
    /// uint32(4GB-1)なので、Spanのint長では2GB超のファイル後方に届かない。
    /// 範囲外参照はすべてInvalidDataException(不正TIFFの報告)にする。
    /// </summary>
    /// <remarks>
    /// マップしたメモリ(またはメモリ上のバイト列)を指すか、ネットワーク上のファイルを読む
    /// <see cref="StreamWindow"/> を持つ。後者の <see cref="Slice"/> が返すスパンは次の読み出しまで有効で、
    /// 呼び出し側はどれも読み出しの間にスパンを持ち越さない。
    /// </remarks>
    private readonly struct TiffBytes
    {
        private readonly byte* _data;
        private readonly StreamWindow? _stream;

        public TiffBytes(byte* data, long length)
        {
            _data = data;
            _stream = null;
            Length = length;
        }

        public TiffBytes(StreamWindow stream)
        {
            _data = null;
            _stream = stream;
            Length = stream.Length;
        }

        public long Length { get; }

        /// <summary>ページ表を使い回すためのファイルの版(メモリ上のバイト列ではnull)。</summary>
        public PageTableKey? CacheKey { get; init; }

        public byte this[long offset] => Slice(offset, 1)[0];

        public ushort ReadU16(long offset, bool bigEndian)
        {
            return bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(Slice(offset, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(Slice(offset, 2));
        }

        public uint ReadU32(long offset, bool bigEndian)
        {
            return bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(Slice(offset, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));
        }

        public ulong ReadU64(long offset, bool bigEndian)
        {
            return bigEndian
                ? BinaryPrimitives.ReadUInt64BigEndian(Slice(offset, 8))
                : BinaryPrimitives.ReadUInt64LittleEndian(Slice(offset, 8));
        }

        public ReadOnlySpan<byte> Slice(long offset, int length)
        {
            CheckRange(offset, length);
            return _stream is null
                ? new ReadOnlySpan<byte>(_data + offset, length)
                : _stream.Read(offset, length);
        }

        private void CheckRange(long offset, int length)
        {
            // offset + length と書くと、long.MaxValue 近くの offset で負へ回り込んで素通りし、写像の外を
            // 読んでプロセスごと落ちる(AccessViolationException は捕捉できない)。Length ≥ 0 なので引き算は溢れない
            if (offset < 0 || length < 0 || offset > Length - length)
            {
                throw new InvalidDataException("参照がファイル範囲外を指しています。");
            }
        }
    }

    /// <summary>
    /// マップせずにファイルを読む窓。読んだ範囲(小さな読み出しは前方をまとめて <see cref="BlockSize"/> まで)を
    /// 持ち、続く読み出しがその中なら読み直さない(IFDのエントリ、ストリップ内の行)。
    /// </summary>
    private sealed class StreamWindow(FileStream stream, long length)
    {
        private const int BlockSize = 64 * 1024;
        private byte[] _buffer = new byte[BlockSize];
        private long _start = -1;
        private int _count;

        public long Length { get; } = length;

        /// <summary>範囲を読む(範囲は呼び出し側で検査済み)。返すスパンは次の読み出しまで有効。</summary>
        public ReadOnlySpan<byte> Read(long offset, int length)
        {
            if (_start >= 0 && offset >= _start && offset - _start <= _count - length)
            {
                return _buffer.AsSpan((int)(offset - _start), length);
            }

            int count = (int)Math.Min(Math.Max(length, BlockSize), Length - offset);
            if (_buffer.Length < count)
            {
                _buffer = new byte[count];
            }

            _start = -1;
            BeforeStreamRead.Value?.Invoke(offset, count);
            stream.Position = offset;
            stream.ReadExactly(_buffer, 0, count);
            _start = offset;
            _count = count;
            return _buffer.AsSpan(0, length);
        }
    }
}
