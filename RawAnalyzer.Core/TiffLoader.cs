using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace RawAnalyzer.Core;

/// <summary>
/// 非圧縮グレースケールTIFFの画素データがファイル上で連続して置かれている場合の配置。
/// </summary>
/// <param name="Width">幅(画素数)。</param>
/// <param name="Height">高さ(画素数)。</param>
/// <param name="BitDepth">1画素あたりのビット数(8または16)。</param>
/// <param name="Endianness">画素値のバイト順。</param>
/// <param name="DataOffset">画素データ先頭のファイル内オフセット。</param>
public sealed record TiffPixelLayout(
    int Width, int Height, int BitDepth, Endianness Endianness, long DataOffset);

/// <summary>
/// 8/16bit 非圧縮グレースケールTIFF(ストリップ形式)の最小パーサ。
/// リトル/ビッグエンディアン両対応。CoreはWICを参照できないため自前実装。
/// </summary>
public static unsafe class TiffLoader
{
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagBitsPerSample = 258;
    private const ushort TagCompression = 259;
    private const ushort TagPhotometric = 262;
    private const ushort TagStripOffsets = 273;
    private const ushort TagSamplesPerPixel = 277;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagSampleFormat = 339;

    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;

    /// <summary>読み込みを許可する最大画素数。ヘッダの不正値でOOMにしないための上限。</summary>
    public const long MaxPixels = 2_000_000_000;

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
    /// ヘッダだけを読み、画素データが連続配置されているかを調べる。
    /// </summary>
    /// <remarks>
    /// 成立すれば <see cref="ToRawFormat"/> 経由で <see cref="RawLoader"/> に渡せる。
    /// 全画素をデコードして持つ必要がなくなり、10億画素級のTIFFでも
    /// MemoryMappedFile のオンデマンド読み出しで扱える。
    /// </remarks>
    /// <param name="path">TIFFファイルのパス。</param>
    /// <param name="layout">画素データの配置。判定できない場合はnull。</param>
    /// <param name="reason">扱えない場合の理由(表示用)。</param>
    /// <returns>連続配置として扱えるならtrue。</returns>
    public static bool TryProbePixelLayout(
        string path, out TiffPixelLayout? layout, out string reason)
    {
        layout = null;
        reason = "";
        long fileLength;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                reason = "ファイルがありません。";
                return false;
            }

            fileLength = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = ex.Message;
            return false;
        }

        if (fileLength < 8)
        {
            reason = "TIFFヘッダが不足しています。";
            return false;
        }

        try
        {
            // ヘッダとIFDの位置はファイル末尾のこともあるため全体をマップする。
            // 実際に触れたページしか読み込まれないので巨大ファイルでも安価
            using MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
                path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
            using MemoryMappedViewAccessor accessor =
                mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            try
            {
                int mapped = (int)Math.Min(fileLength, int.MaxValue);
                var data = new ReadOnlySpan<byte>(pointer + accessor.PointerOffset, mapped);
                return TryProbeCore(data, fileLength, out layout, out reason);
            }
            finally
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException
                or ArgumentException or NotSupportedException)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// <see cref="TryProbePixelLayout"/> の結果をRaw読み込み用のフォーマットへ変換する。
    /// </summary>
    /// <param name="layout">画素データの配置。</param>
    /// <returns>フォーマット記述子。</returns>
    public static RawFormat ToRawFormat(TiffPixelLayout layout)
    {
        return new RawFormat
        {
            Width = layout.Width,
            Height = layout.Height,
            BitDepth = layout.BitDepth,

            // TIFFのサンプル値はそのビット深度でのフルスケール。
            // 内部表現(16bit)へは value << (16-N) で合わせるため下詰め扱いになる
            Packing = BitPacking.Lsb,
            Endianness = layout.Endianness,
            HeaderOffset = layout.DataOffset,
        };
    }

    private static bool TryProbeCore(
        ReadOnlySpan<byte> data, long fileLength, out TiffPixelLayout? layout, out string reason)
    {
        layout = null;
        bool bigEndian = data[0] == (byte)'M' && data[1] == (byte)'M';
        bool littleEndian = data[0] == (byte)'I' && data[1] == (byte)'I';
        if (!bigEndian && !littleEndian)
        {
            reason = "TIFFのバイトオーダーマークが不正です。";
            return false;
        }

        if (ReadU16(data, 2, bigEndian) != 42)
        {
            reason = "TIFFのマジックナンバーが不正です(BigTIFFは未対応)。";
            return false;
        }

        List<IfdEntry> entries = ReadIfd(data, ReadU32(data, 4, bigEndian), bigEndian);
        uint width = GetScalar(entries, data, TagImageWidth, bigEndian) ?? 0;
        uint height = GetScalar(entries, data, TagImageLength, bigEndian) ?? 0;
        uint bits = GetScalar(entries, data, TagBitsPerSample, bigEndian) ?? 0;
        uint compression = GetScalar(entries, data, TagCompression, bigEndian) ?? 1;
        uint samples = GetScalar(entries, data, TagSamplesPerPixel, bigEndian) ?? 1;
        uint rowsPerStrip = GetScalar(entries, data, TagRowsPerStrip, bigEndian) ?? height;
        uint photometric = GetScalar(entries, data, TagPhotometric, bigEndian) ?? 1;
        uint sampleFormat = GetScalar(entries, data, TagSampleFormat, bigEndian) ?? 1;

        if (compression != 1)
        {
            reason = $"非圧縮TIFFのみ直接読み出せます(Compression={compression})。";
            return false;
        }

        if (samples != 1)
        {
            reason = $"グレースケールTIFFのみ直接読み出せます(SamplesPerPixel={samples})。";
            return false;
        }

        if (bits is not (8 or 16))
        {
            reason = $"8/16bit TIFFのみ直接読み出せます(BitsPerSample={bits})。";
            return false;
        }

        if (photometric != 1)
        {
            // 0 = WhiteIsZero は値が反転しているので、そのまま画素として扱えない
            reason = $"BlackIsZeroのTIFFのみ直接読み出せます(Photometric={photometric})。";
            return false;
        }

        if (sampleFormat != 1)
        {
            reason = $"符号なし整数のTIFFのみ直接読み出せます(SampleFormat={sampleFormat})。";
            return false;
        }

        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue
            || rowsPerStrip == 0)
        {
            reason = "画像サイズが不正です。";
            return false;
        }

        uint[]? offsets = GetArray(entries, data, TagStripOffsets, bigEndian);
        uint[]? counts = GetArray(entries, data, TagStripByteCounts, bigEndian);
        if (offsets is null || counts is null || offsets.Length == 0
            || offsets.Length != counts.Length)
        {
            reason = "ストリップ情報が不正です。";
            return false;
        }

        // ストリップが昇順かつ隙間なく並んでいれば、raw と同じ連続データとして扱える
        int bytesPerSample = bits == 8 ? 1 : 2;
        long total = (long)width * height * bytesPerSample;
        long expected = offsets[0];
        for (int i = 0; i < offsets.Length; i++)
        {
            if (offsets[i] != expected)
            {
                reason = "ストリップが連続していないため直接読み出せません。";
                return false;
            }

            expected += counts[i];
        }

        if (expected - offsets[0] != total)
        {
            reason = "ストリップの合計バイト数が画像サイズと一致しません。";
            return false;
        }

        if (offsets[0] + total > fileLength)
        {
            reason = "画素データがファイル範囲外を指しています。";
            return false;
        }

        layout = new TiffPixelLayout(
            (int)width, (int)height, (int)bits,
            bigEndian ? Endianness.Big : Endianness.Little, offsets[0]);
        reason = "";
        return true;
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

        // 配列を確保する前に総画素数を検証する。ヘッダが巨大値を主張していると
        // OutOfMemoryException になり、不正データの報告として役に立たない
        long declaredPixels = (long)width * height;
        if (declaredPixels > MaxPixels)
        {
            throw new InvalidDataException(
                $"画像が大きすぎます({width}×{height})。上限は {MaxPixels / 1_000_000} M画素です。");
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

            // checked((int)) だと int.MaxValue 超のオフセットが OverflowException になり、
            // 不正TIFFの報告にならない。uint のまま範囲判定してから絞る
            // (負値へ落とすと後段の Slice が別の例外になるため先に弾く)
            long valueOffset = totalSize <= 4
                ? entry.ValueFieldOffset
                : ReadU32(data, entry.ValueFieldOffset, bigEndian);
            if (valueOffset < 0 || valueOffset + totalSize > data.Length)
            {
                throw new InvalidDataException($"タグ{tag}の値がファイル範囲外を指しています。");
            }

            // ここまでで valueOffset + totalSize <= data.Length を確認済みなのでintに収まる
            int start = (int)valueOffset;
            var values = new uint[entry.Count];
            for (int i = 0; i < entry.Count; i++)
            {
                values[i] = valueSize == 2
                    ? ReadU16(data, start + i * 2, bigEndian)
                    : ReadU32(data, start + i * 4, bigEndian);
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
