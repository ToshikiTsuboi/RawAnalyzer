using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

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
    int Width, int Height, int BitDepth, Endianness Endianness, long DataOffset)
{
    /// <summary>TIFF内のページ数。</summary>
    public int PageCount { get; init; } = 1;

    /// <summary>この配置が表すページ(0起点)。</summary>
    public int PageIndex { get; init; }

    /// <summary>CFA(DNG)ページのBayer配列。グレーならNone。</summary>
    public BayerPattern Bayer { get; init; } = BayerPattern.None;
}

/// <summary>
/// TIFFページのサンプル形式。画素データを読まずにヘッダだけで分かる情報。
/// </summary>
/// <param name="BitsPerSample">1サンプルのビット数。</param>
/// <param name="SamplesPerPixel">1画素のサンプル数(1=グレー、3=RGB)。</param>
/// <param name="SampleFormat">
/// TIFFのSampleFormat(1=符号なし整数、2=符号あり整数、3=IEEE実数)。
/// </param>
/// <param name="PageCount">TIFF内のページ数。</param>
public sealed record TiffSampleInfo(
    int BitsPerSample, int SamplesPerPixel, int SampleFormat, int PageCount)
{
    /// <summary>Compressionタグ(1=非圧縮)。</summary>
    public int Compression { get; init; } = 1;

    /// <summary>PhotometricInterpretationタグ(1=BlackIsZero)。</summary>
    public int Photometric { get; init; } = 1;

    /// <summary>BigTIFF(マジック43)か。</summary>
    public bool IsBigTiff { get; init; }

    /// <summary>
    /// WICのフレーム番号。WICは縮小IFD(NewSubfileType bit0)を数えず、
    /// SubIFDやImageJの仮想ページには到達できない(その場合は-1)。
    /// </summary>
    public int WicFrameIndex { get; init; }

    /// <summary>幅。</summary>
    public int Width { get; init; }

    /// <summary>高さ。</summary>
    public int Height { get; init; }

    /// <summary>CFAページのBayer配列。</summary>
    public BayerPattern Bayer { get; init; } = BayerPattern.None;

    /// <summary>ImageJの単一IFD+連続画素スタックの仮想ページか。</summary>
    public bool IsVirtualPage { get; init; }

    /// <summary>Predictorタグ(1=なし、2=水平差分、3=実数差分)。</summary>
    public int Predictor { get; init; } = 1;

    /// <summary>CFA(32803)またはLinearRaw(34892)か。</summary>
    public bool IsRawPhotometric => Photometric is TiffLoader.PhotometricCfa or TiffLoader.PhotometricLinearRaw;

    /// <summary>32bitサンプルとしての解釈方法。</summary>
    public SampleInterpretation Interpretation => SampleFormat switch
    {
        2 => SampleInterpretation.SignedInteger,
        3 => BitsPerSample == 16 ? SampleInterpretation.HalfFloat : SampleInterpretation.Float,
        _ => SampleInterpretation.UnsignedInteger,
    };
}

/// <summary>
/// TIFFの最小パーサ。クラシック/BigTIFF、リトル/ビッグエンディアン両対応。
/// CoreはWICを参照できないため自前実装。
/// </summary>
/// <remarks>
/// 役割は3つ。(1) 非圧縮16bitグレーの連続配置を見つけて <see cref="RawLoader"/> に渡す、
/// (2) WICが正しく扱えない非圧縮ページ(CFA、10/12/14/24/64bit、符号あり、BigTIFF、
/// ImageJ仮想スタック、16bit実数のRGB)を自前で復号する、(3) WICへ渡す前にヘッダを検査して
/// 「黙って壊れる」形式(未知の圧縮など)を弾く材料を返す。
/// </remarks>
public static unsafe class TiffLoader
{
    private const ushort TagNewSubfileType = 254;
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagBitsPerSample = 258;
    private const ushort TagCompression = 259;
    private const ushort TagPhotometric = 262;
    private const ushort TagFillOrder = 266;
    private const ushort TagImageDescription = 270;
    private const ushort TagStripOffsets = 273;
    private const ushort TagSamplesPerPixel = 277;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagPlanarConfiguration = 284;
    private const ushort TagPredictor = 317;
    private const ushort TagTileWidth = 322;
    private const ushort TagTileLength = 323;
    private const ushort TagTileOffsets = 324;
    private const ushort TagTileByteCounts = 325;
    private const ushort TagSubIfds = 330;
    private const ushort TagSampleFormat = 339;
    private const ushort TagCfaRepeatPatternDim = 33421;
    private const ushort TagCfaPattern = 33422;

    /// <summary>PhotometricInterpretation = CFA(DNG/TIFF-EP)。</summary>
    public const int PhotometricCfa = 32803;

    /// <summary>PhotometricInterpretation = LinearRaw(DNG)。</summary>
    public const int PhotometricLinearRaw = 34892;

    /// <summary>読み込みを許可する最大画素数。ヘッダの不正値でOOMにしないための上限。</summary>
    public const long MaxPixels = 2_000_000_000;

    /// <summary>ディレクトリ列挙の上限。壊れたファイルによる過大確保を防ぐ。</summary>
    public const int MaxPages = 100_000;

    /// <summary>1つのIFDから辿るSubIFDの上限。</summary>
    private const int MaxSubIfds = 16;

    /// <summary>
    /// WICのTIFFデコーダが扱える圧縮方式。これ以外を渡すと、WICはエラーにせず
    /// 全画素0の画像を返す(LZMA/Zstd/WebP/JPEG2000/JPEG XL/LERCで確認)。
    /// </summary>
    private static readonly HashSet<int> WicCompressions = new() { 1, 2, 3, 4, 5, 6, 7, 8, 32773, 32946 };

    /// <summary>WICのTIFFデコーダで正しく読める圧縮方式か。</summary>
    /// <param name="compression">Compressionタグの値。</param>
    /// <returns>WICへ渡してよければtrue。</returns>
    public static bool IsWicCompression(int compression) => WicCompressions.Contains(compression);

    /// <summary>Compressionタグの値を表示名にする。</summary>
    /// <param name="compression">Compressionタグの値。</param>
    /// <returns>表示名。</returns>
    public static string DescribeCompression(int compression)
    {
        string name = compression switch
        {
            1 => "非圧縮",
            2 => "CCITT RLE",
            3 => "CCITT G3",
            4 => "CCITT G4",
            5 => "LZW",
            6 => "旧JPEG",
            7 => "JPEG",
            8 or 32946 => "Deflate",
            32773 => "PackBits",
            32809 => "ThunderScan",
            33003 or 33005 or 34712 or 34713 => "JPEG 2000",
            34887 => "LERC",
            34925 => "LZMA",
            50000 => "Zstd",
            50001 => "WebP",
            50002 => "JPEG XL",
            _ => "不明",
        };
        return $"{name}({compression})";
    }

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
    /// メモリ上のTIFFデータ(先頭ページ)を読み込み、16bitフルスケールへ正規化したRawImageを返す。
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

        fixed (byte* pointer = data)
        {
            return LoadCore(new TiffBytes(pointer, data.Length));
        }
    }

    /// <summary>
    /// ヘッダだけを読み、画素データが連続配置されているかを調べる。
    /// </summary>
    /// <remarks>
    /// 成立すれば <see cref="ToRawFormat"/> 経由で <see cref="RawLoader"/> に渡せる。
    /// 全画素をデコードして持つ必要がなくなり、10億画素級のTIFFでも
    /// MemoryMappedFile のオンデマンド読み出しで扱える。CFA(DNG)ページも対象で、
    /// その場合は <see cref="TiffPixelLayout.Bayer"/> にCFAPatternから求めた配列が入る。
    /// </remarks>
    /// <param name="path">TIFFファイルのパス。</param>
    /// <param name="layout">画素データの配置。判定できない場合はnull。</param>
    /// <param name="reason">扱えない場合の理由(表示用)。</param>
    /// <param name="pageIndex">調べるページ(0起点)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>連続配置として扱えるならtrue。</returns>
    public static bool TryProbePixelLayout(
        string path, out TiffPixelLayout? layout, out string reason,
        int pageIndex = 0, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        cancellationToken.ThrowIfCancellationRequested();
        layout = null;
        reason = "";
        TiffPixelLayout? result = null;
        string why = "";
        bool ok = WithMappedFile(path, ref why, data =>
        {
            if (!TryParseHeader(data, out TiffHeader header, out string headerReason))
            {
                why = headerReason;
                return false;
            }

            List<PageRef> pages = ReadPageTable(data, header, cancellationToken);
            if ((uint)pageIndex >= (uint)pages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex), "TIFFのページ範囲外です。");
            }

            PageLayout page = ReadPageLayout(data, header, pages[pageIndex]);
            if (!TryProbeCore(page, header, data.Length, out result, out why))
            {
                return false;
            }

            result = result! with { PageCount = pages.Count, PageIndex = pageIndex };
            return true;
        });
        layout = result;
        reason = why;
        return ok;
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
            Bayer = layout.Bayer,
        };
    }

    /// <summary>
    /// ヘッダだけを読み、ページのサンプル形式を調べる。
    /// </summary>
    /// <remarks>
    /// WICのTIFFデコーダは32bit整数のページも Gray32Float として返し、
    /// 実数として読むとビット列がそのまま実数に化ける。整数と実数の区別は
    /// ファイル自身のSampleFormatでしか付かないため、ここで読み取る。
    /// 圧縮ページでもタグは読めるので、画素の連続配置は要求しない。
    /// 併せて圧縮方式・Photometric・BigTIFFか・WICのフレーム番号も返し、
    /// 呼び出し側がWICへ渡す前に安全性を判断できるようにする。
    /// </remarks>
    /// <param name="path">TIFFファイルのパス。</param>
    /// <param name="info">サンプル形式。読めない場合はnull。</param>
    /// <param name="reason">読めない場合の理由。</param>
    /// <param name="pageIndex">調べるページ(0起点)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>読み取れたらtrue。</returns>
    public static bool TryReadSampleInfo(
        string path, out TiffSampleInfo? info, out string reason, int pageIndex = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        cancellationToken.ThrowIfCancellationRequested();
        info = null;
        TiffSampleInfo? result = null;
        string why = "";
        bool ok = WithMappedFile(path, ref why, data =>
        {
            if (!TryParseHeader(data, out TiffHeader header, out string headerReason))
            {
                why = headerReason;
                return false;
            }

            List<PageRef> pages = ReadPageTable(data, header, cancellationToken);
            if ((uint)pageIndex >= (uint)pages.Count)
            {
                why = $"ページ{pageIndex}はありません(全{pages.Count}ページ)。";
                return false;
            }

            PageLayout page = ReadPageLayout(data, header, pages[pageIndex]);
            if (page.Bits == 0 || page.Bits > 64 || page.Spp == 0 || page.Spp > 8)
            {
                why = $"BitsPerSample={page.Bits}、SamplesPerPixel={page.Spp} は扱えません。";
                return false;
            }

            result = new TiffSampleInfo(page.Bits, page.Spp, page.Format, pages.Count)
            {
                Compression = page.Compression,
                Photometric = page.Photometric,
                IsBigTiff = header.BigTiff,
                WicFrameIndex = pages[pageIndex].WicFrame,
                Width = page.Width,
                Height = page.Height,
                Bayer = page.Bayer,
                IsVirtualPage = pages[pageIndex].Virtual,
                Predictor = page.Predictor,
            };
            return true;
        });
        info = result;
        reason = why;
        return ok;
    }

    /// <summary>
    /// ヘッダだけを読み、ページのサンプル形式を調べる(理由なし)。
    /// </summary>
    /// <param name="path">TIFFファイルのパス。</param>
    /// <param name="info">サンプル形式。読めない場合はnull。</param>
    /// <param name="pageIndex">調べるページ(0起点)。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>読み取れたらtrue。</returns>
    public static bool TryReadSampleInfo(
        string path, out TiffSampleInfo? info, int pageIndex = 0,
        CancellationToken cancellationToken = default)
    {
        return TryReadSampleInfo(path, out info, out _, pageIndex, cancellationToken);
    }

    /// <summary>
    /// 非圧縮の1サンプル/画素ページを自前で復号する。
    /// </summary>
    /// <remarks>
    /// 対象は BitsPerSample 8/10/12/14/16/24/32/64、SampleFormat 1〜3、
    /// Photometric 0/1/CFA/LinearRaw、ストリップ(順不同可)またはタイル、BigTIFF、
    /// ImageJの仮想ページ。16bit以下の符号なし整数はそのビット深度のまま
    /// (value &lt;&lt; (16-N))、それ以外は値域を調べて16bitコードへ写し、
    /// 対応関係を <paramref name="scaling"/> で返す。
    /// 非対応の形式は false と理由を返す。データ自体が壊れている場合は例外。
    /// </remarks>
    /// <param name="path">TIFFファイルのパス。</param>
    /// <param name="pageIndex">ページ(0起点)。</param>
    /// <param name="image">復号した画像。</param>
    /// <param name="scaling">16bitへ写した場合の対応関係。そのままの場合はnull。</param>
    /// <param name="reason">復号しなかった理由。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <param name="progress">進捗(0〜1)。</param>
    /// <returns>復号したらtrue。</returns>
    /// <exception cref="InvalidDataException">ファイルが壊れている場合。</exception>
    public static bool TryDecodeUncompressed(
        string path, int pageIndex, out RawImage? image, out SampleScaling? scaling,
        out string reason, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        cancellationToken.ThrowIfCancellationRequested();
        image = null;
        scaling = null;
        RawImage? decoded = null;
        SampleScaling? decodedScaling = null;
        string why = "";
        bool ok;
        try
        {
            ok = WithMappedFile(path, ref why, data =>
            {
                if (!TryParseHeader(data, out TiffHeader header, out string headerReason))
                {
                    why = headerReason;
                    return false;
                }

                List<PageRef> pages = ReadPageTable(data, header, cancellationToken);
                if ((uint)pageIndex >= (uint)pages.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(pageIndex), "TIFFのページ範囲外です。");
                }

                PageLayout page = ReadPageLayout(data, header, pages[pageIndex]);
                if (!IsNativelyDecodable(page, out why))
                {
                    return false;
                }

                decoded = DecodePage(data, header, page, cancellationToken, progress, out decodedScaling);
                return true;
            }, rethrowInvalidData: true);
        }
        catch
        {
            decoded?.Dispose();
            throw;
        }

        image = decoded;
        scaling = decodedScaling;
        reason = why;
        return ok;
    }

    /// <summary>
    /// 非圧縮のRGBページ(3〜4サンプル/画素)を自前で復号し、値域から16bitコードへ写す。
    /// </summary>
    /// <remarks>
    /// WICは16bit実数(半精度)のRGBを、0〜1へ切り詰めてsRGBのガンマを掛けた16bit整数
    /// (Rgb48/Rgba64)として返すため、1を超える値・負値・線形性が失われる。元のサンプルを
    /// 直接読んで、32bit実数のRGBと同じくRGBの3成分をまとめた値域で写す(チャネル間の比を保つ)。
    /// 4番目のサンプル(アルファ)は値域にも出力にも入れない。
    /// 対象は Photometric=RGB、BitsPerSample 8/16/24/32/64 のうちWICがそのまま読める
    /// 16bit以下の符号なし整数を除く形式、チャンキー/プレーン分離、ストリップ/タイル、BigTIFF。
    /// 非対応の形式は false と理由を返す。データ自体が壊れている場合は例外。
    /// </remarks>
    /// <param name="path">TIFFファイルのパス。</param>
    /// <param name="pageIndex">ページ(0起点)。</param>
    /// <param name="image">復号した画像(16bit)。</param>
    /// <param name="scaling">16bitへ写した対応関係。</param>
    /// <param name="reason">復号しなかった理由。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <param name="progress">進捗(0〜1)。</param>
    /// <returns>復号したらtrue。</returns>
    /// <exception cref="InvalidDataException">ファイルが壊れている場合。</exception>
    public static bool TryDecodeUncompressedRgb(
        string path, int pageIndex, out ColorImage? image, out SampleScaling? scaling,
        out string reason, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        cancellationToken.ThrowIfCancellationRequested();
        ColorImage? decoded = null;
        SampleScaling? decodedScaling = null;
        string why = "";
        bool ok = WithMappedFile(path, ref why, data =>
        {
            if (!TryParseHeader(data, out TiffHeader header, out string headerReason))
            {
                why = headerReason;
                return false;
            }

            List<PageRef> pages = ReadPageTable(data, header, cancellationToken);
            if ((uint)pageIndex >= (uint)pages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex), "TIFFのページ範囲外です。");
            }

            PageLayout page = ReadPageLayout(data, header, pages[pageIndex]);
            if (!IsNativelyDecodableRgb(page, out why))
            {
                return false;
            }

            decoded = DecodeRgbPage(data, header, page, cancellationToken, progress, out SampleScaling result);
            decodedScaling = result;
            return true;
        }, rethrowInvalidData: true);

        image = decoded;
        scaling = decodedScaling;
        reason = why;
        return ok;
    }

    // ------------------------------------------------------------------ ファイルアクセス

    private static bool WithMappedFile(
        string path, ref string reason, Func<TiffBytes, bool> action, bool rethrowInvalidData = false)
    {
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
                // 2GB超のTIFFはIFDやストリップ配列がファイル後方に置かれることがある。
                // Spanのint長へ切り詰めず longオフセットのままアクセスする
                return action(new TiffBytes(pointer + accessor.PointerOffset, fileLength));
            }
            finally
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        catch (InvalidDataException) when (rethrowInvalidData)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException
                or ArgumentException or NotSupportedException)
        {
            reason = ex.Message;
            return false;
        }
    }

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
        if (ifdOffset < 8 || ifdOffset + header.EntryCountSize > data.Length)
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
            if (valueOffset < 0 || valueOffset + totalSize > data.Length)
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

    // ------------------------------------------------------------------ ページ表

    /// <summary>
    /// アプリから見た1ページ。WicFrameはWICのフレーム番号(到達不能なら-1)。
    /// </summary>
    private readonly record struct PageRef(long IfdOffset, int WicFrame, long VirtualIndex, bool Virtual);

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
    private static List<PageRef> ReadPageTable(TiffBytes data, TiffHeader header, CancellationToken ct)
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
                if (sub < 8 || sub + header.EntryCountSize > data.Length || sub == ifd)
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

    /// <summary>1ページのタグを解釈した結果。</summary>
    private sealed record PageLayout(
        int Width, int Height, int Bits, int Spp, int Format, int Compression, int Photometric,
        int Planar, int FillOrder, bool Tiled, int TileWidth, int TileHeight, long RowsPerStrip,
        long[] Offsets, long[] Counts, BayerPattern Bayer, bool Virtual, int Predictor);

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
            rowsPerStrip, offsets, counts, bayer, page.Virtual, (int)Math.Clamp(predictor, 0, int.MaxValue));
    }

    /// <summary>CFARepeatPatternDim=2×2 の CFAPattern(0=R,1=G,2=B) を Bayer 配列へ写す。</summary>
    private static BayerPattern ReadCfaPattern(List<IfdEntry> entries, TiffBytes data, TiffHeader header)
    {
        try
        {
            long[]? dim = GetArray(entries, data, TagCfaRepeatPatternDim, header);
            long[]? pattern = GetArray(entries, data, TagCfaPattern, header);
            if (pattern is null || pattern.Length < 4 || (dim is not null && (dim.Length < 2 || dim[0] != 2 || dim[1] != 2)))
            {
                return BayerPattern.None;
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

        if (offsets[0] < 0 || offsets[0] + total > fileLength)
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

    // ------------------------------------------------------------------ 自前復号

    private static readonly int[] NativeBits = { 8, 10, 12, 14, 16, 24, 32, 64 };

    private static bool IsNativelyDecodable(PageLayout page, out string reason)
    {
        if (page.Compression != 1)
        {
            reason = $"圧縮ページ({DescribeCompression(page.Compression)})は自前では復号しません。";
            return false;
        }

        if (page.Spp != 1)
        {
            reason = $"1サンプル/画素のページのみ自前で復号します(SamplesPerPixel={page.Spp})。";
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

            long tilesAcross = (page.Width + tileWidth - 1) / tileWidth;
            long tilesDown = (page.Height + tileHeight - 1) / tileHeight;
            long tileCount = tilesAcross * tilesDown;

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
                    || offsets[tile] + (rows * tileRowBytes) > data.Length)
                {
                    throw new InvalidDataException($"タイル{tile}がファイル範囲外、または短すぎます。");
                }

                for (int r = 0; r < rows; r++)
                {
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
            if (counts[s] < needed || offsets[s] < 0 || offsets[s] + needed > data.Length)
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

    private static RawImage DecodePage(
        TiffBytes data, TiffHeader header, PageLayout page, CancellationToken ct,
        IProgress<double>? progress, out SampleScaling? scaling)
    {
        int width = page.Width;
        int height = page.Height;
        bool bigEndian = header.BigEndian;
        bool invert = page.Photometric == 0 && page.Format == 1;
        var codes = new ushort[(long)width * height];
        var values = new ulong[width];

        // 16bit以下の符号なし整数はビット深度を保ったまま内部表現へ
        if (page.Format == 1 && page.Bits <= 16)
        {
            int shift = 16 - page.Bits;
            ulong max = (1UL << page.Bits) - 1;
            ForEachRow(data, page, (y, x0, count, bytes) =>
            {
                UnpackRow(bytes, count, page.Bits, bigEndian, values);
                long index = ((long)y * width) + x0;
                for (int i = 0; i < count; i++)
                {
                    ulong v = invert ? max - values[i] : values[i];
                    codes[index + i] = (ushort)(v << shift);
                }
            }, ct, progress, 0, 1);

            scaling = null;
            return RawImage.FromPixels(
                new RawFormat { Width = width, Height = height, BitDepth = page.Bits, Bayer = page.Bayer },
                codes);
        }

        // それ以外は値域を調べてから16bitへ写す(2パス。MMFなので再読は安価)
        var accumulator = new SampleRangeAccumulator();
        ulong maxUnsigned = page.Bits == 64 ? ulong.MaxValue : (1UL << page.Bits) - 1;
        ForEachRow(data, page, (y, x0, count, bytes) =>
        {
            UnpackRow(bytes, count, page.Bits, bigEndian, values);
            for (int i = 0; i < count; i++)
            {
                ulong raw = invert ? maxUnsigned - values[i] : values[i];
                accumulator.Add(ToValue(raw, page.Bits, page.Format));
            }
        }, ct, progress, 0, 0.5);

        SampleScaling result = SampleScaling.FromRange(accumulator.ToRange());
        ForEachRow(data, page, (y, x0, count, bytes) =>
        {
            UnpackRow(bytes, count, page.Bits, bigEndian, values);
            long index = ((long)y * width) + x0;
            for (int i = 0; i < count; i++)
            {
                ulong raw = invert ? maxUnsigned - values[i] : values[i];
                codes[index + i] = result.ToCode(ToValue(raw, page.Bits, page.Format));
            }
        }, ct, progress, 0.5, 0.5);

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

        // 1パス目: RGBの3成分をまとめた値域(アルファは入れない)
        var accumulator = new SampleRangeAccumulator();
        for (int plane = 0; plane < planes; plane++)
        {
            ForEachRow(data, page, (y, x0, count, bytes) =>
            {
                UnpackRow(bytes, count * samplesPerPixel, page.Bits, bigEndian, values);
                for (int i = 0; i < count; i++)
                {
                    for (int c = 0; c < channelsPerPass; c++)
                    {
                        accumulator.Add(ToValue(values[(i * samplesPerPixel) + c], page.Bits, page.Format));
                    }
                }
            }, ct, progress, 0.5 * plane / planes, 0.5 / planes, samplesPerPixel, plane);
        }

        // 2パス目: 16bitコードへ写してRGBのインターリーブへ置く
        SampleScaling result = SampleScaling.FromRange(accumulator.ToRange());
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

    // ------------------------------------------------------------------ メモリ上の読み込み(先頭ページ)

    private static RawImage LoadCore(TiffBytes data)
    {
        if (!TryParseHeader(data, out TiffHeader header, out string reason))
        {
            throw new InvalidDataException(reason);
        }

        long ifdOffset = FirstIfdOffset(data, header);
        var entries = ReadIfd(data, ifdOffset, header);

        long width = GetScalar(entries, data, TagImageWidth, header)
            ?? throw new InvalidDataException("ImageWidthタグがありません。");
        long height = GetScalar(entries, data, TagImageLength, header)
            ?? throw new InvalidDataException("ImageLengthタグがありません。");
        long bits = GetScalar(entries, data, TagBitsPerSample, header)
            ?? throw new InvalidDataException("BitsPerSampleタグがありません。");
        long compression = GetScalar(entries, data, TagCompression, header) ?? 1;
        long samplesPerPixel = GetScalar(entries, data, TagSamplesPerPixel, header) ?? 1;
        long rowsPerStrip = GetScalar(entries, data, TagRowsPerStrip, header) ?? height;
        long photometric = GetScalar(entries, data, TagPhotometric, header) ?? 1;
        long sampleFormat = GetScalar(entries, data, TagSampleFormat, header) ?? 1;

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

        // WhiteIsZero(値が反転)や符号付き/浮動小数点をそのまま読むと、例外にならない
        // まま誤った測定値になる。プローブ(TryProbeCore)と同じ基準で弾く
        if (photometric != 1)
        {
            throw new InvalidDataException(
                $"BlackIsZeroのTIFFのみサポートします(Photometric={photometric})。");
        }

        if (sampleFormat != 1)
        {
            throw new InvalidDataException(
                $"符号なし整数のTIFFのみサポートします(SampleFormat={sampleFormat})。");
        }

        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw new InvalidDataException("画像サイズが不正です。");
        }

        // 配列を確保する前に総画素数を検証する。ヘッダが巨大値を主張していると
        // OutOfMemoryException になり、不正データの報告として役に立たない
        long declaredPixels = width * height;
        if (declaredPixels > MaxPixels)
        {
            throw new InvalidDataException(
                $"画像が大きすぎます({width}×{height})。上限は {MaxPixels / 1_000_000} M画素です。");
        }

        long[] stripOffsets = GetArray(entries, data, TagStripOffsets, header)
            ?? throw new InvalidDataException("StripOffsetsタグがありません。");
        long[] stripByteCounts = GetArray(entries, data, TagStripByteCounts, header)
            ?? throw new InvalidDataException("StripByteCountsタグがありません。");
        if (stripOffsets.Length != stripByteCounts.Length)
        {
            throw new InvalidDataException("StripOffsetsとStripByteCountsの個数が一致しません。");
        }

        int bytesPerSample = bits == 8 ? 1 : 2;
        var pixels = new ushort[width * height];
        long pixelIndex = 0;
        long remainingRows = height;
        if (rowsPerStrip <= 0)
        {
            rowsPerStrip = height;
        }

        for (int strip = 0; strip < stripOffsets.Length; strip++)
        {
            long stripRows = Math.Min(rowsPerStrip, remainingRows);
            long expectedBytes = stripRows * width * bytesPerSample;
            if (stripByteCounts[strip] != expectedBytes)
            {
                throw new InvalidDataException(
                    $"ストリップ{strip}のバイト数が不正です(期待 {expectedBytes}, 実際 {stripByteCounts[strip]})。");
            }

            if (expectedBytes > int.MaxValue)
            {
                throw new InvalidDataException($"ストリップ{strip}が大きすぎます。");
            }

            long offset = stripOffsets[strip];
            if (offset < 0 || offset + expectedBytes > data.Length)
            {
                throw new InvalidDataException($"ストリップ{strip}がファイル範囲外を指しています。");
            }

            ReadOnlySpan<byte> stripData = data.Slice(offset, (int)expectedBytes);
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
                    pixels[pixelIndex + i] = header.BigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(stripData.Slice(i * 2, 2))
                        : BinaryPrimitives.ReadUInt16LittleEndian(stripData.Slice(i * 2, 2));
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
            Endianness = header.BigEndian ? Endianness.Big : Endianness.Little,
        };
        return new RawImage(format, pixels);
    }

    // ------------------------------------------------------------------ バイト列アクセス

    /// <summary>
    /// ファイル全体へのlongオフセットアクセス。クラシックTIFFのオフセット上限は
    /// uint32(4GB-1)なので、Spanのint長では2GB超のファイル後方に届かない。
    /// 範囲外参照はすべてInvalidDataException(不正TIFFの報告)にする。
    /// </summary>
    private readonly struct TiffBytes
    {
        private readonly byte* _data;

        public TiffBytes(byte* data, long length)
        {
            _data = data;
            Length = length;
        }

        public long Length { get; }

        public byte this[long offset]
        {
            get
            {
                CheckRange(offset, 1);
                return _data[offset];
            }
        }

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
            return new ReadOnlySpan<byte>(_data + offset, length);
        }

        private void CheckRange(long offset, int length)
        {
            if (offset < 0 || length < 0 || offset + length > Length)
            {
                throw new InvalidDataException("参照がファイル範囲外を指しています。");
            }
        }
    }
}
