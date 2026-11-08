using System.Text.RegularExpressions;

namespace RawAnalyzer.Core;

public static unsafe partial class TiffLoader
{
    // ------------------------------------------------------------------ ページ表

    /// <summary>
    /// アプリから見た1ページ。WicFrameはWICのフレーム番号(到達不能なら-1)。
    /// </summary>
    private readonly record struct PageRef(long IfdOffset, int WicFrame, long VirtualIndex, bool Virtual);

    /// <summary>ページ表を使い回すファイルの版。パス・長さ・更新日時が同じなら同じページ表とみなす。</summary>
    private readonly record struct PageTableKey(string Path, long Length, DateTime LastWriteUtc);

    /// <summary>ページ表を覚えておくファイルの数(表示中のスタック・比較ペイン・一括書き出し)。</summary>
    private const int PageTableCacheSize = 4;

    private static readonly object PageTableCacheGate = new();

    /// <summary>最近使ったページ表(先頭が最新)。ページ表は作った後は変更しない。</summary>
    private static readonly List<(PageTableKey Key, PageRef[] Pages)> PageTableCache = new();

    /// <summary>
    /// ページ表を返す。同じファイルの同じ版のページ表を覚えていればそれを使い、なければ作って覚える。
    /// </summary>
    /// <remarks>
    /// ページを1枚読むたびに(TryReadSampleInfo・TryProbePixelLayout・自前復号でそれぞれ)IFDチェーン全体を
    /// 解析すると、多ページTIFFの再生・一括書き出しが O(N²) になる。ファイルが書き換わって長さか
    /// 更新日時が変われば作り直すので、ページ数の変化は従来どおり検出できる。
    /// </remarks>
    private static IReadOnlyList<PageRef> ReadPageTable(TiffBytes data, TiffHeader header, CancellationToken ct)
    {
        if (data.CacheKey is not { } key)
        {
            return BuildPageTable(data, header, ct);
        }

        lock (PageTableCacheGate)
        {
            int index = PageTableCache.FindIndex(entry => entry.Key == key);
            if (index >= 0)
            {
                (PageTableKey Key, PageRef[] Pages) found = PageTableCache[index];
                PageTableCache.RemoveAt(index);
                PageTableCache.Insert(0, found);
                return found.Pages;
            }
        }

        PageRef[] pages = BuildPageTable(data, header, ct).ToArray();
        lock (PageTableCacheGate)
        {
            PageTableCache.RemoveAll(entry => entry.Key == key);
            PageTableCache.Insert(0, (key, pages));
            if (PageTableCache.Count > PageTableCacheSize)
            {
                PageTableCache.RemoveAt(PageTableCache.Count - 1);
            }
        }

        return pages;
    }

    /// <summary>
    /// ページ表を作る。主チェーンの各IFDと、その全解像度SubIFD(DNGの本体はここにある)を
    /// ページにし、縮小画像(NewSubfileType bit0)は除く。
    /// </summary>
    /// <remarks>
    /// WICも縮小IFDをフレームに数えないため、こう数えるとWICのフレーム番号と一致する。
    /// 主チェーンが1ページだけでImageJの「images=N」がある場合は、画素データが
    /// 連続していることを確かめたうえでN個の仮想ページに展開する
    /// (ImageJが4GB超のスタックを書くときの形式)。
    /// </remarks>
    private static List<PageRef> BuildPageTable(TiffBytes data, TiffHeader header, CancellationToken ct)
    {
        List<long> chain = ReadChain(data, header, ct);
        var pages = new List<PageRef>();
        int wicFrame = 0;
        List<IfdEntry>? firstEntries = null;
        foreach (long ifd in chain)
        {
            ct.ThrowIfCancellationRequested();
            List<IfdEntry> entries = ReadIfd(data, ifd, header);
            firstEntries ??= entries;
            long subfileType = GetScalar(entries, data, TagNewSubfileType, header) ?? 0;
            bool reduced = (subfileType & 1) != 0;
            if (!reduced)
            {
                pages.Add(new PageRef(ifd, wicFrame, 0, false));
                wicFrame++;
            }

            long[]? subIfds = TryGetSubIfds(entries, data, header);
            if (subIfds is null)
            {
                continue;
            }

            foreach (long sub in subIfds.Take(MaxSubIfds))
            {
                if (sub < 8 || sub > data.Length - header.EntryCountSize || sub == ifd)
                {
                    continue;
                }

                try
                {
                    List<IfdEntry> subEntries = ReadIfd(data, sub, header);
                    long subType = GetScalar(subEntries, data, TagNewSubfileType, header) ?? 0;
                    if ((subType & 1) == 0)
                    {
                        pages.Add(new PageRef(sub, -1, 0, false));
                    }
                }
                catch (InvalidDataException)
                {
                    // 壊れたSubIFDは無視して主チェーンだけを使う
                }
            }

            if (pages.Count > MaxPages)
            {
                throw new NotSupportedException($"TIFFは最大{MaxPages:N0}ページまで対応します。");
            }
        }

        if (pages.Count == 0)
        {
            // 全IFDが縮小画像を名乗る不自然なファイル。従来どおり主チェーンをそのままページにする
            for (int i = 0; i < chain.Count; i++)
            {
                pages.Add(new PageRef(chain[i], i, 0, false));
            }
        }

        if (pages.Count == 1 && chain.Count == 1 && pages[0].IfdOffset == chain[0])
        {
            int frames = ReadImageJVirtualFrames(data, header, firstEntries!);
            if (frames > 1)
            {
                pages.Clear();
                for (int k = 0; k < frames; k++)
                {
                    pages.Add(new PageRef(chain[0], k == 0 ? 0 : -1, k, true));
                }
            }
        }

        return pages;
    }

    private static long[]? TryGetSubIfds(List<IfdEntry> entries, TiffBytes data, TiffHeader header)
    {
        try
        {
            return GetArray(entries, data, TagSubIfds, header);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static readonly Regex ImageJImagesPattern = new(@"(?m)^images=(\d+)\s*$", RegexOptions.CultureInvariant);

    /// <summary>ImageJの単一IFD+連続画素スタックなら、そのフレーム数(2以上)を返す。</summary>
    private static int ReadImageJVirtualFrames(TiffBytes data, TiffHeader header, List<IfdEntry> entries)
    {
        string? description = GetAscii(entries, data, TagImageDescription, header, 4096);
        if (description is null || !description.StartsWith("ImageJ=", StringComparison.Ordinal))
        {
            return 1;
        }

        Match match = ImageJImagesPattern.Match(description);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int frames) || frames <= 1)
        {
            return 1;
        }

        // 連続配置の非圧縮1サンプルでなければ仮想ページにできない
        long compression = GetScalar(entries, data, TagCompression, header) ?? 1;
        long spp = GetScalar(entries, data, TagSamplesPerPixel, header) ?? 1;
        long width = GetScalar(entries, data, TagImageWidth, header) ?? 0;
        long height = GetScalar(entries, data, TagImageLength, header) ?? 0;
        long bits = GetScalar(entries, data, TagBitsPerSample, header) ?? 0;
        if (compression != 1 || spp != 1 || width <= 0 || height <= 0 || bits is <= 0 or > 64)
        {
            return 1;
        }

        long[]? offsets = GetArray(entries, data, TagStripOffsets, header);
        long[]? counts = GetArray(entries, data, TagStripByteCounts, header);
        if (offsets is null || offsets.Length == 0 || counts is null || counts.Length != offsets.Length)
        {
            return 1;
        }

        long frameBytes = height * ((width * bits + 7) / 8);
        long expected = offsets[0];
        for (int i = 0; i < offsets.Length; i++)
        {
            if (offsets[i] != expected)
            {
                return 1;
            }

            expected += counts[i];
        }

        if (expected - offsets[0] != frameBytes || frameBytes <= 0)
        {
            return 1;
        }

        long available = (data.Length - offsets[0]) / frameBytes;
        int usable = (int)Math.Min(frames, Math.Min(available, MaxPages));
        return Math.Max(usable, 1);
    }

    // ------------------------------------------------------------------ ページ構造

    /// <summary>1ページのタグを解釈した結果。ExtraSample は ExtraSamples の先頭の値(なければ-1)。</summary>
    private sealed record PageLayout(
        int Width, int Height, int Bits, int Spp, int Format, int Compression, int Photometric,
        int Planar, int FillOrder, bool Tiled, int TileWidth, int TileHeight, long RowsPerStrip,
        long[] Offsets, long[] Counts, BayerPattern Bayer, bool Virtual, int Predictor, int ExtraSample);

    private static PageLayout ReadPageLayout(TiffBytes data, TiffHeader header, PageRef page)
    {
        List<IfdEntry> entries = ReadIfd(data, page.IfdOffset, header);
        long width = GetScalar(entries, data, TagImageWidth, header) ?? 0;
        long height = GetScalar(entries, data, TagImageLength, header) ?? 0;
        long bits = GetScalar(entries, data, TagBitsPerSample, header) ?? 1; // 欠落時は1bit(2値画像の慣習)
        long compression = GetScalar(entries, data, TagCompression, header) ?? 1;
        long spp = GetScalar(entries, data, TagSamplesPerPixel, header) ?? 1;
        long photometric = GetScalar(entries, data, TagPhotometric, header) ?? 1;
        long format = GetScalar(entries, data, TagSampleFormat, header) ?? 1;
        if (format == SampleFormatUndefined)
        {
            // 未定義(4)はTIFF 6.0の勧めどおりタグがないとき(符号なし整数)と同じに読む。区別すると
            // 16bitが値域換算で引き伸ばされ、1億画素超の直接読み出しもできず、SampleFormat=1 と違う値で開く
            format = 1;
        }

        long planar = GetScalar(entries, data, TagPlanarConfiguration, header) ?? 1;
        long predictor = GetScalar(entries, data, TagPredictor, header) ?? 1;
        long fillOrder = GetScalar(entries, data, TagFillOrder, header) ?? 1;
        long rowsPerStrip = GetScalar(entries, data, TagRowsPerStrip, header) ?? height;
        long tileWidth = GetScalar(entries, data, TagTileWidth, header) ?? 0;
        long tileLength = GetScalar(entries, data, TagTileLength, header) ?? 0;
        if (width < 0 || width > int.MaxValue || height < 0 || height > int.MaxValue)
        {
            throw new InvalidDataException("画像サイズが不正です。");
        }

        bool tiled = tileWidth > 0 && tileLength > 0
            && FindTagValues(entries, data, TagTileOffsets, header) is not null;
        long[] offsets = (tiled
            ? GetArray(entries, data, TagTileOffsets, header)
            : GetArray(entries, data, TagStripOffsets, header)) ?? Array.Empty<long>();
        long[] counts = (tiled
            ? GetArray(entries, data, TagTileByteCounts, header)
            : GetArray(entries, data, TagStripByteCounts, header)) ?? Array.Empty<long>();

        if (page.Virtual)
        {
            // ImageJ仮想ページ: 先頭フレームからの等間隔配置を1ストリップとして表す
            long frameBytes = height * ((width * bits + 7) / 8);
            offsets = new[] { offsets[0] + (page.VirtualIndex * frameBytes) };
            counts = new[] { frameBytes };
            rowsPerStrip = height;
            tiled = false;
        }

        BayerPattern bayer = photometric == PhotometricCfa
            ? ReadCfaPattern(entries, data, header)
            : BayerPattern.None;

        return new PageLayout(
            (int)width, (int)height, (int)Math.Clamp(bits, 0, int.MaxValue), (int)Math.Clamp(spp, 0, int.MaxValue),
            (int)format, (int)Math.Clamp(compression, 0, int.MaxValue), (int)Math.Clamp(photometric, 0, int.MaxValue),
            (int)planar, (int)fillOrder, tiled,
            (int)Math.Clamp(tileWidth, 0, int.MaxValue), (int)Math.Clamp(tileLength, 0, int.MaxValue),
            rowsPerStrip, offsets, counts, bayer, page.Virtual, (int)Math.Clamp(predictor, 0, int.MaxValue),
            ReadFirstExtraSample(entries, data, header));
    }

    /// <summary>
    /// ExtraSamples の先頭の値(0=未指定、1=関連アルファ、2=非関連アルファ)。タグがなければ-1。
    /// 画素の読み出しには使わないので、タグが壊れていても読込自体は止めずに-1とする。
    /// </summary>
    private static int ReadFirstExtraSample(List<IfdEntry> entries, TiffBytes data, TiffHeader header)
    {
        try
        {
            return GetScalar(entries, data, TagExtraSamples, header) is { } value && value is >= 0 and <= int.MaxValue
                ? (int)value
                : -1;
        }
        catch (InvalidDataException)
        {
            return -1;
        }
    }

    /// <summary>CFARepeatPatternDim=2×2 の CFAPattern(0=R,1=G,2=B) を Bayer 配列へ写す。</summary>
    /// <remarks>
    /// DNG の CFAPattern は ActiveArea の左上を起点とする(DNG SDK・dcraw/LibRaw・rawspeed の解釈)。このアプリは
    /// 遮光域を含む画像全体を開くので、ActiveArea の上端・左端の偶奇だけずらして画像全体の (0,0) 起点の配列にする
    /// (ずらさないと奇数のとき R↔Gb・Gr↔B が入れ替わる)。
    /// </remarks>
    private static BayerPattern ReadCfaPattern(List<IfdEntry> entries, TiffBytes data, TiffHeader header)
    {
        try
        {
            long[]? dim = GetArray(entries, data, TagCfaRepeatPatternDim, header);
            long[]? relative = GetArray(entries, data, TagCfaPattern, header);
            if (relative is null || relative.Length < 4 || (dim is not null && (dim.Length < 2 || dim[0] != 2 || dim[1] != 2)))
            {
                return BayerPattern.None;
            }

            (int rowPhase, int columnPhase) = ReadActiveAreaPhase(entries, data, header);
            var pattern = new long[4];
            for (int row = 0; row < 2; row++)
            {
                for (int column = 0; column < 2; column++)
                {
                    pattern[(row * 2) + column] = relative[(((row + rowPhase) & 1) * 2) + ((column + columnPhase) & 1)];
                }
            }

            return (pattern[0], pattern[1], pattern[2], pattern[3]) switch
            {
                (0, 1, 1, 2) => BayerPattern.Rggb,
                (2, 1, 1, 0) => BayerPattern.Bggr,
                (1, 0, 2, 1) => BayerPattern.Grbg,
                (1, 2, 0, 1) => BayerPattern.Gbrg,
                _ => BayerPattern.None,
            };
        }
        catch (InvalidDataException)
        {
            return BayerPattern.None;
        }
    }

    /// <summary>
    /// ActiveArea(上端, 左端, 下端, 右端)の上端・左端の偶奇。タグがない・読めないときは (0, 0)
    /// (従来どおり画像全体の (0,0) 起点。Bayer の自動設定は止めない)。
    /// </summary>
    private static (int Row, int Column) ReadActiveAreaPhase(List<IfdEntry> entries, TiffBytes data, TiffHeader header)
    {
        try
        {
            return GetArray(entries, data, TagActiveArea, header) is { Length: >= 2 } area && area[0] >= 0 && area[1] >= 0
                ? ((int)(area[0] & 1), (int)(area[1] & 1))
                : (0, 0);
        }
        catch (InvalidDataException)
        {
            return (0, 0);
        }
    }

    // ------------------------------------------------------------------ 直接読み出し判定

    private static bool TryProbeCore(
        PageLayout page, TiffHeader header, long fileLength, out TiffPixelLayout? layout, out string reason)
    {
        layout = null;
        if (page.Compression != 1)
        {
            reason = $"非圧縮TIFFのみ直接読み出せます(Compression={page.Compression})。";
            return false;
        }

        if (page.Spp != 1)
        {
            reason = $"グレースケールTIFFのみ直接読み出せます(SamplesPerPixel={page.Spp})。";
            return false;
        }

        if (page.Bits is not (8 or 16))
        {
            reason = $"8/16bit TIFFのみ直接読み出せます(BitsPerSample={page.Bits})。";
            return false;
        }

        if (page.Photometric is not (1 or PhotometricCfa or PhotometricLinearRaw))
        {
            // 0 = WhiteIsZero は値が反転しているので、そのまま画素として扱えない
            reason = $"BlackIsZeroのTIFFのみ直接読み出せます(Photometric={page.Photometric})。";
            return false;
        }

        if (page.Format != 1)
        {
            reason = $"符号なし整数のTIFFのみ直接読み出せます(SampleFormat={page.Format})。";
            return false;
        }

        if (page.FillOrder != 1)
        {
            reason = $"FillOrder={page.FillOrder} は直接読み出せません。";
            return false;
        }

        if (page.Width == 0 || page.Height == 0 || page.RowsPerStrip == 0)
        {
            reason = "画像サイズが不正です。";
            return false;
        }

        if (page.Tiled)
        {
            reason = "タイル形式のため直接読み出せません。";
            return false;
        }

        long[] offsets = page.Offsets;
        long[] counts = page.Counts;
        if (offsets.Length == 0 || offsets.Length != counts.Length)
        {
            reason = "ストリップ情報が不正です。";
            return false;
        }

        // ストリップが昇順かつ隙間なく並んでいれば、raw と同じ連続データとして扱える
        int bytesPerSample = page.Bits == 8 ? 1 : 2;
        long total = (long)page.Width * page.Height * bytesPerSample;
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

        if (offsets[0] < 0 || offsets[0] > fileLength - total)
        {
            reason = "画素データがファイル範囲外を指しています。";
            return false;
        }

        layout = new TiffPixelLayout(
            page.Width, page.Height, page.Bits,
            header.BigEndian ? Endianness.Big : Endianness.Little, offsets[0])
        {
            Bayer = page.Bayer,
        };
        reason = "";
        return true;
    }
}
