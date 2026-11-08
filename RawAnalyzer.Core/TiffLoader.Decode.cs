using System.Buffers.Binary;
using System.Numerics;

namespace RawAnalyzer.Core;

public static unsafe partial class TiffLoader
{
    // ------------------------------------------------------------------ 自前復号

    private static readonly int[] NativeBits = { 8, 10, 12, 14, 16, 24, 32, 64 };

    private static bool IsNativelyDecodable(PageLayout page, out string reason)
    {
        if (page.Compression != 1)
        {
            reason = $"圧縮ページ({DescribeCompression(page.Compression)})は自前では復号しません。";
            return false;
        }

        // 多チャネルはグレー(グレー+アルファなど)だけを対象に、先頭チャネルを読む。CFA/LinearRawは1サンプルのみ
        if (page.Spp != 1
            && (page.Spp is < 1 or > MaxSamplesPerPixel || page.Photometric is not (0 or 1) || page.Planar is not (1 or 2)))
        {
            reason = "1サンプル/画素のページと、多チャネルのグレー(チャンキー/プレーン分離)のみ自前で復号します" +
                $"(SamplesPerPixel={page.Spp}、Photometric={page.Photometric}、PlanarConfiguration={page.Planar})。";
            return false;
        }

        if (page.FillOrder != 1)
        {
            reason = $"FillOrder={page.FillOrder} は未対応です。";
            return false;
        }

        if (page.Photometric is not (0 or 1 or PhotometricCfa or PhotometricLinearRaw))
        {
            reason = $"Photometric={page.Photometric} は自前では復号しません。";
            return false;
        }

        if (Array.IndexOf(NativeBits, page.Bits) < 0)
        {
            reason = $"BitsPerSample={page.Bits} は未対応です。";
            return false;
        }

        bool formatOk = page.Format switch
        {
            1 => true,
            2 => page.Bits is 8 or 16 or 32 or 64,
            3 => page.Bits is 16 or 32 or 64,
            _ => false,
        };
        if (!formatOk)
        {
            reason = $"SampleFormat={page.Format} × {page.Bits}bit は未対応です。";
            return false;
        }

        if (page.Width == 0 || page.Height == 0)
        {
            reason = "画像サイズが不正です。";
            return false;
        }

        if ((long)page.Width * page.Height > MaxPixels)
        {
            reason = $"画像が大きすぎます({page.Width}×{page.Height})。上限は {MaxPixels / 1_000_000} M画素です。";
            return false;
        }

        reason = "";
        return true;
    }

    private static bool IsNativelyDecodableRgb(PageLayout page, out string reason)
    {
        if (page.Compression != 1)
        {
            reason = $"圧縮ページ({DescribeCompression(page.Compression)})は自前では復号しません。";
            return false;
        }

        if (page.Photometric != 2 || page.Spp is < 3 or > 4)
        {
            reason = $"RGB(3〜4サンプル/画素)のページのみ対象です(Photometric={page.Photometric}、SamplesPerPixel={page.Spp})。";
            return false;
        }

        if (page.Planar is not (1 or 2))
        {
            reason = $"PlanarConfiguration={page.Planar} は未対応です。";
            return false;
        }

        if (page.FillOrder != 1)
        {
            reason = $"FillOrder={page.FillOrder} は未対応です。";
            return false;
        }

        bool formatOk = page.Format switch
        {
            1 => page.Bits is 24 or 32 or 64,
            2 => page.Bits is 8 or 16 or 32 or 64,
            3 => page.Bits is 16 or 32 or 64,
            _ => false,
        };
        if (!formatOk)
        {
            reason = $"SampleFormat={page.Format} × {page.Bits}bit のRGBは対象外です。";
            return false;
        }

        if (page.Width == 0 || page.Height == 0)
        {
            reason = "画像サイズが不正です。";
            return false;
        }

        if ((long)page.Width * page.Height * 3 > Array.MaxLength)
        {
            reason = $"画像が大きすぎます({page.Width}×{page.Height})。";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>1行分のサンプルを処理するコールバック。</summary>
    private delegate void RowHandler(int y, int x0, int count, ReadOnlySpan<byte> bytes);

    /// <summary>ストリップ/タイルを順に辿り、行ごとにバイト列を渡す。</summary>
    /// <param name="data">ファイル全体。</param>
    /// <param name="page">ページ。</param>
    /// <param name="handler">行ごとの処理。count は画素数(1画素に samplesPerPixel サンプル)。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <param name="progress">進捗。</param>
    /// <param name="progressStart">この走査の進捗の開始値。</param>
    /// <param name="progressSpan">この走査の進捗の幅。</param>
    /// <param name="samplesPerPixel">ストリップ/タイル内の1画素のサンプル数(チャンキーのRGBなら3〜4)。</param>
    /// <param name="plane">プレーン分離(PlanarConfiguration=2)の成分番号。それ以外は0。</param>
    private static void ForEachRow(
        TiffBytes data, PageLayout page, RowHandler handler, CancellationToken ct,
        IProgress<double>? progress, double progressStart, double progressSpan,
        int samplesPerPixel = 1, int plane = 0)
    {
        long[] offsets = page.Offsets;
        long[] counts = page.Counts;
        if (offsets.Length == 0 || counts.Length != offsets.Length)
        {
            throw new InvalidDataException("ストリップ/タイル情報が不正です。");
        }

        if (page.Tiled)
        {
            int tileWidth = page.TileWidth;
            int tileHeight = page.TileHeight;
            if (tileWidth <= 0 || tileHeight <= 0)
            {
                throw new InvalidDataException("タイルサイズが不正です。");
            }

            // タイル寸法は int.MaxValue に丸めて持つので、int のまま足すと 幅(高さ) + タイル寸法 − 1 が
            // 負へ回ってタイル数が0以下になり、1枚も読まずに全画素0の画像を返してしまう
            long tilesAcross = ((long)page.Width + tileWidth - 1) / tileWidth;
            long tilesDown = ((long)page.Height + tileHeight - 1) / tileHeight;
            long tileCount = tilesAcross * tilesDown;
            if (tileCount <= 0)
            {
                throw new InvalidDataException("タイル数が不正です。");
            }

            // プレーン分離では成分ごとに tileCount 枚ずつ並ぶ
            long firstTile = plane * tileCount;
            if (offsets.Length < firstTile + tileCount)
            {
                throw new InvalidDataException("タイル数がタグと一致しません。");
            }

            long tileRowBytes = ((long)tileWidth * page.Bits * samplesPerPixel + 7) / 8;
            if (tileRowBytes > int.MaxValue)
            {
                throw new InvalidDataException("タイル幅が大きすぎます。");
            }

            for (long t = 0; t < tileCount; t++)
            {
                ct.ThrowIfCancellationRequested();
                int x0 = (int)((t % tilesAcross) * tileWidth);
                int y0 = (int)((t / tilesAcross) * tileHeight);
                int rows = Math.Min(tileHeight, page.Height - y0);
                int columns = Math.Min(tileWidth, page.Width - x0);
                long tile = firstTile + t;
                if (counts[tile] < rows * tileRowBytes || offsets[tile] < 0
                    || offsets[tile] > data.Length - (rows * tileRowBytes))
                {
                    throw new InvalidDataException($"タイル{tile}がファイル範囲外、または短すぎます。");
                }

                for (int r = 0; r < rows; r++)
                {
                    // 画像全体が1タイルのような大きなタイルでも中止でき、進捗が進むように、
                    // ストリップ経路と同じく64行ごとにキャンセルを見て、タイル内の行の進みを報告する
                    if (r > 0 && (r & 63) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report(progressStart + (progressSpan * (t + ((double)r / rows)) / tileCount));
                    }

                    handler(y0 + r, x0, columns, data.Slice(offsets[tile] + (r * tileRowBytes), (int)tileRowBytes));
                }

                progress?.Report(progressStart + (progressSpan * (t + 1) / tileCount));
            }

            return;
        }

        long rowBytes = ((long)page.Width * page.Bits * samplesPerPixel + 7) / 8;
        if (rowBytes > int.MaxValue)
        {
            throw new InvalidDataException("画像幅が大きすぎます。");
        }

        long rowsPerStrip = page.RowsPerStrip <= 0 ? page.Height : Math.Min(page.RowsPerStrip, page.Height);
        long y = 0;

        // プレーン分離では成分ごとに StripsPerImage 本ずつ並ぶ
        long firstStrip = plane * ((page.Height + rowsPerStrip - 1) / rowsPerStrip);
        for (long s = firstStrip; s < offsets.Length && y < page.Height; s++)
        {
            ct.ThrowIfCancellationRequested();
            long rows = Math.Min(rowsPerStrip, page.Height - y);
            long needed = rows * rowBytes;
            if (counts[s] < needed || offsets[s] < 0 || offsets[s] > data.Length - needed)
            {
                throw new InvalidDataException($"ストリップ{s}がファイル範囲外、または短すぎます。");
            }

            for (long r = 0; r < rows; r++)
            {
                if ((r & 63) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                handler((int)(y + r), 0, page.Width, data.Slice(offsets[s] + (r * rowBytes), (int)rowBytes));
            }

            y += rows;
            progress?.Report(progressStart + (progressSpan * y / page.Height));
        }

        if (y < page.Height)
        {
            throw new InvalidDataException("ストリップが画像の全行をカバーしていません。");
        }
    }

    /// <summary>1行のビット列をサンプル値(ビット列のまま)に展開する。</summary>
    private static void UnpackRow(ReadOnlySpan<byte> bytes, int count, int bits, bool bigEndian, Span<ulong> values)
    {
        switch (bits)
        {
            case 8:
                for (int i = 0; i < count; i++)
                {
                    values[i] = bytes[i];
                }

                return;
            case 16:
                for (int i = 0; i < count; i++)
                {
                    values[i] = bigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i * 2, 2))
                        : BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(i * 2, 2));
                }

                return;
            case 24:
                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> b = bytes.Slice(i * 3, 3);
                    values[i] = bigEndian
                        ? ((ulong)b[0] << 16) | ((ulong)b[1] << 8) | b[2]
                        : ((ulong)b[2] << 16) | ((ulong)b[1] << 8) | b[0];
                }

                return;
            case 32:
                for (int i = 0; i < count; i++)
                {
                    values[i] = bigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(i * 4, 4))
                        : BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i * 4, 4));
                }

                return;
            case 64:
                for (int i = 0; i < count; i++)
                {
                    values[i] = bigEndian
                        ? BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(i * 8, 8))
                        : BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i * 8, 8));
                }

                return;
            default:
                {
                    // 詰め込み(10/12/14bit)。TIFFは行頭がバイト境界で、ビットはMSBから詰める
                    ulong accumulator = 0;
                    int available = 0;
                    int position = 0;
                    ulong mask = (1UL << bits) - 1;
                    for (int i = 0; i < count; i++)
                    {
                        while (available < bits)
                        {
                            accumulator = (accumulator << 8) | bytes[position++];
                            available += 8;
                        }

                        values[i] = (accumulator >> (available - bits)) & mask;
                        available -= bits;
                    }

                    return;
                }
        }
    }

    private static double ToValue(ulong raw, int bits, int format)
    {
        return format switch
        {
            2 => bits switch
            {
                8 => (sbyte)raw,
                16 => (short)raw,
                32 => (int)raw,
                _ => (long)raw,
            },
            3 => bits switch
            {
                16 => (double)BitConverter.UInt16BitsToHalf((ushort)raw),
                32 => BitConverter.UInt32BitsToSingle((uint)raw),
                _ => BitConverter.UInt64BitsToDouble(raw),
            },
            _ => raw,
        };
    }

    /// <summary>ページのサンプルの解釈(整数のページは値域が 0〜1 でも正規化の慣習を当てない)。</summary>
    private static SampleInterpretation InterpretationOf(PageLayout page) => page.Format switch
    {
        2 => SampleInterpretation.SignedInteger,
        3 => page.Bits == 16 ? SampleInterpretation.HalfFloat : SampleInterpretation.Float,
        _ => SampleInterpretation.UnsignedInteger,
    };

    private static RawImage DecodePage(
        TiffBytes data, TiffHeader header, PageLayout page, CancellationToken ct,
        IProgress<double>? progress, out SampleScaling? scaling)
    {
        int width = page.Width;
        int height = page.Height;
        bool bigEndian = header.BigEndian;

        // WhiteIsZero は大小を反転して BlackIsZero にそろえる。整数は全ビット反転(符号なしは
        // 最大値−v、符号ありは −1−v)、実数は 1−v。WICが8/16bit整数と32bit実数で返す向きと同じにし、
        // サンプル形式・幅や読み込み経路で白黒が逆転しないようにする
        bool invertBits = page.Photometric == 0 && page.Format != 3;
        bool invertValue = page.Photometric == 0 && page.Format == 3;

        // 多チャネルのグレーは先頭チャネルだけを読む。チャンキーは1画素に Spp サンプル並ぶので
        // stride おきに拾い、プレーン分離は先頭プレーンだけを走査する(残りのチャネルは値域にも入れない)
        int stride = page.Planar == 2 ? 1 : page.Spp;
        var codes = new ushort[(long)width * height];
        var values = new ulong[(long)width * stride];

        // 16bit以下の符号なし整数はビット深度を保ったまま内部表現へ
        if (page.Format == 1 && page.Bits <= 16)
        {
            int shift = 16 - page.Bits;
            ulong max = (1UL << page.Bits) - 1;
            ForEachRow(data, page, (y, x0, count, bytes) =>
            {
                UnpackRow(bytes, count * stride, page.Bits, bigEndian, values);
                long index = ((long)y * width) + x0;
                for (int i = 0; i < count; i++)
                {
                    ulong raw = values[i * stride];
                    ulong v = invertBits ? max - raw : raw;
                    codes[index + i] = (ushort)(v << shift);
                }
            }, ct, progress, 0, 1, stride);

            scaling = null;
            return RawImage.FromPixels(
                new RawFormat { Width = width, Height = height, BitDepth = page.Bits, Bayer = page.Bayer },
                codes);
        }

        // それ以外は値域を調べてから16bitへ写す(2パス。MMFなので再読は安価)
        var accumulator = new SampleRangeAccumulator();
        ulong maxUnsigned = page.Bits == 64 ? ulong.MaxValue : (1UL << page.Bits) - 1;
        double Sample(ulong raw)
        {
            double value = ToValue(invertBits ? maxUnsigned - raw : raw, page.Bits, page.Format);
            return invertValue ? 1 - value : value;
        }

        ForEachRow(data, page, (y, x0, count, bytes) =>
        {
            UnpackRow(bytes, count * stride, page.Bits, bigEndian, values);
            for (int i = 0; i < count; i++)
            {
                accumulator.Add(Sample(values[i * stride]));
            }
        }, ct, progress, 0, 0.5, stride);

        SampleScaling result = SampleScaling.FromRange(accumulator.ToRange(), InterpretationOf(page));
        ForEachRow(data, page, (y, x0, count, bytes) =>
        {
            UnpackRow(bytes, count * stride, page.Bits, bigEndian, values);
            long index = ((long)y * width) + x0;
            for (int i = 0; i < count; i++)
            {
                codes[index + i] = result.ToCode(Sample(values[i * stride]));
            }
        }, ct, progress, 0.5, 0.5, stride);

        scaling = result;
        return RawImage.FromPixels(
            new RawFormat { Width = width, Height = height, BitDepth = 16, Bayer = page.Bayer },
            codes);
    }

    private static ColorImage DecodeRgbPage(
        TiffBytes data, TiffHeader header, PageLayout page, CancellationToken ct,
        IProgress<double>? progress, out SampleScaling scaling)
    {
        int width = page.Width;
        int height = page.Height;
        bool bigEndian = header.BigEndian;

        // チャンキーは1回の走査で1画素にsppサンプル、プレーン分離はR/G/Bを1成分ずつ走査する
        bool planar = page.Planar == 2;
        int samplesPerPixel = planar ? 1 : page.Spp;
        int channelsPerPass = planar ? 1 : 3;
        int planes = planar ? 3 : 1;
        var values = new ulong[(long)width * samplesPerPixel];

        // 1パス目: RGBの3成分をまとめた値域(アルファは入れない)。NaN・無限大は「非数N画素」と出すので、
        // いずれかの成分が非有限の画素を1つと数える。プレーン分離は成分ごとに走査するので、画素に印を付けて数える
        var accumulator = new SampleRangeAccumulator();
        long nonFinitePixels = 0;
        ulong[]? nonFiniteMarks = null;
        for (int plane = 0; plane < planes; plane++)
        {
            ForEachRow(data, page, (y, x0, count, bytes) =>
            {
                UnpackRow(bytes, count * samplesPerPixel, page.Bits, bigEndian, values);
                long rowStart = ((long)y * width) + x0;
                for (int i = 0; i < count; i++)
                {
                    bool finite = true;
                    for (int c = 0; c < channelsPerPass; c++)
                    {
                        double value = ToValue(values[(i * samplesPerPixel) + c], page.Bits, page.Format);
                        if (double.IsFinite(value))
                        {
                            accumulator.Add(value);
                        }
                        else
                        {
                            finite = false;
                        }
                    }

                    if (finite)
                    {
                        continue;
                    }

                    if (!planar)
                    {
                        nonFinitePixels++;
                        continue;
                    }

                    long pixel = rowStart + i;
                    nonFiniteMarks ??= new ulong[(((long)width * height) + 63) / 64];
                    nonFiniteMarks[pixel / 64] |= 1UL << (int)(pixel % 64);
                }
            }, ct, progress, 0.5 * plane / planes, 0.5 / planes, samplesPerPixel, plane);
        }

        foreach (ulong marks in nonFiniteMarks ?? Array.Empty<ulong>())
        {
            nonFinitePixels += BitOperations.PopCount(marks);
        }

        // 2パス目: 16bitコードへ写してRGBのインターリーブへ置く
        SampleScaling result = SampleScaling.FromRange(
            accumulator.ToRange() with { NonFiniteCount = nonFinitePixels }, InterpretationOf(page));
        var codes = new ushort[(long)width * height * 3];
        for (int plane = 0; plane < planes; plane++)
        {
            int firstChannel = plane;
            ForEachRow(data, page, (y, x0, count, bytes) =>
            {
                UnpackRow(bytes, count * samplesPerPixel, page.Bits, bigEndian, values);
                long index = (((long)y * width) + x0) * 3;
                for (int i = 0; i < count; i++)
                {
                    for (int c = 0; c < channelsPerPass; c++)
                    {
                        codes[index + (i * 3) + firstChannel + c] =
                            result.ToCode(ToValue(values[(i * samplesPerPixel) + c], page.Bits, page.Format));
                    }
                }
            }, ct, progress, 0.5 + (0.5 * plane / planes), 0.5 / planes, samplesPerPixel, plane);
        }

        scaling = result;
        return ColorImage.FromInterleaved(width, height, 16, codes);
    }
}
