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
/// TIFFのSampleFormat(1=符号なし整数、2=符号あり整数、3=IEEE実数)。4(未定義)はTIFF 6.0の勧めどおり
/// タグがないときと同じ符号なし整数として扱い、1を返す。
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

    /// <summary>
    /// ExtraSamplesタグの先頭の値(0=未指定、1=関連アルファ、2=非関連アルファ)。タグがなければ-1。
    /// </summary>
    /// <remarks>
    /// WICは多チャネルのグレーをこの値で読み分け、組み合わせによっては復号に失敗する
    /// (32bitの2サンプルで0=未指定、8bitの3サンプルで指定あり)。
    /// </remarks>
    public int ExtraSampleType { get; init; } = -1;

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
/// ImageJ仮想スタック、16bit実数・16bit符号あり・24/64bitのRGB、32bitなどの多チャネルのグレー)を
/// 自前で復号する、(3) WICへ渡す前にヘッダを検査して
/// 「黙って壊れる」形式(未知の圧縮など)を弾く材料を返す。
/// </remarks>
public static unsafe partial class TiffLoader
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
    private const ushort TagExtraSamples = 338;
    private const ushort TagSampleFormat = 339;
    private const ushort TagCfaRepeatPatternDim = 33421;
    private const ushort TagCfaPattern = 33422;
    private const ushort TagActiveArea = 50829;

    /// <summary>SampleFormat = 未定義。</summary>
    private const int SampleFormatUndefined = 4;

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

    /// <summary>扱う1画素あたりのサンプル数の上限。</summary>
    private const int MaxSamplesPerPixel = 8;

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

            IReadOnlyList<PageRef> pages = ReadPageTable(data, header, cancellationToken);
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

            IReadOnlyList<PageRef> pages = ReadPageTable(data, header, cancellationToken);
            if ((uint)pageIndex >= (uint)pages.Count)
            {
                why = $"ページ{pageIndex}はありません(全{pages.Count}ページ)。";
                return false;
            }

            PageLayout page = ReadPageLayout(data, header, pages[pageIndex]);
            if (page.Bits == 0 || page.Bits > 64 || page.Spp == 0 || page.Spp > MaxSamplesPerPixel)
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
                ExtraSampleType = page.ExtraSample,
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

    /// <summary>ヘッダとページ表から全ページの寸法を取得する。画素データは展開しない。</summary>
    /// <param name="path">調べる TIFF ファイルのパス。</param>
    /// <param name="cancellationToken">読み取りを取り消すトークン。</param>
    /// <returns>
    /// ページ順の幅と高さ(画素単位)。ファイルを開けない、ヘッダが無効、ページがない、または幅・高さが正でない場合は null。
    /// </returns>
    public static IReadOnlyList<(int Width, int Height)>? ReadPageDimensions(
        string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (int, int)[]? dimensions = null;
        string reason = "";
        WithMappedFile(path, ref reason, data =>
        {
            if (!TryParseHeader(data, out TiffHeader header, out _))
                return false;
            IReadOnlyList<PageRef> pages = ReadPageTable(data, header, cancellationToken);
            var sizes = new (int, int)[pages.Count];
            for (int i = 0; i < pages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PageLayout page = ReadPageLayout(data, header, pages[i]);
                if (page.Width <= 0 || page.Height <= 0)
                    return false;
                sizes[i] = (page.Width, page.Height);
            }
            dimensions = sizes;
            return sizes.Length > 0;
        });
        return dimensions is { Length: > 0 } ? dimensions : null;
    }

    /// <summary>
    /// 非圧縮の1サンプル/画素ページ(と多チャネルのグレーの先頭チャネル)を自前で復号する。
    /// </summary>
    /// <remarks>
    /// 対象は BitsPerSample 8/10/12/14/16/24/32/64、SampleFormat 1〜3、
    /// Photometric 0/1/CFA/LinearRaw、ストリップ(順不同可)またはタイル、BigTIFF、
    /// ImageJの仮想ページ。グレー(Photometric 0/1)は多チャネル(グレー+アルファなど、
    /// チャンキー/プレーン分離)でもよく、先頭チャネルだけを読む(残りは値域にも出力にも入れない)。
    /// 16bit以下の符号なし整数はそのビット深度のまま
    /// (value &lt;&lt; (16-N))、それ以外は値域を調べて16bitコードへ写し、
    /// 対応関係を <paramref name="scaling"/> で返す。
    /// WhiteIsZero(Photometric=0)は、整数を全ビット反転(符号なしは 最大値−v、符号ありは −1−v)、
    /// 実数を 1−v として反転してから写す(WICが8/16bit整数・32bit実数で返す向きと同じ)。
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
    /// <exception cref="IOException">
    /// 読み出しに失敗した場合(ネットワーク上のファイルの切断、消えたファイルなど)。
    /// </exception>
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

                IReadOnlyList<PageRef> pages = ReadPageTable(data, header, cancellationToken);
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
            }, decoding: true);
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
    /// (Rgb48/Rgba64)として返すため、1を超える値・負値・線形性が失われる。16bit符号あり・
    /// 24/64bitのRGBは復号自体に失敗する。元のサンプルを直接読んで、32bit実数・整数のRGBと同じく
    /// RGBの3成分をまとめた値域で写す(チャネル間の比を保つ)。
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
    /// <exception cref="IOException">
    /// 読み出しに失敗した場合(ネットワーク上のファイルの切断、消えたファイルなど)。
    /// </exception>
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

            IReadOnlyList<PageRef> pages = ReadPageTable(data, header, cancellationToken);
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
        }, decoding: true);

        image = decoded;
        scaling = decodedScaling;
        reason = why;
        return ok;
    }

    /// <summary>
    /// このサンプル構成のページを、非圧縮であれば自前で復号できるかを返す。
    /// </summary>
    /// <remarks>
    /// <see cref="TryDecodeUncompressed"/>(グレー/CFA)と <see cref="TryDecodeUncompressedRgb"/>(RGB)が
    /// 受け付ける Photometric・SamplesPerPixel・BitsPerSample・SampleFormat・画像サイズの組み合わせかを、
    /// 両者と同じ判定で調べる。圧縮方式・FillOrder・ストリップ/タイルの配置など、個々のページの
    /// 条件は見ない。圧縮ページを拒否するとき「非圧縮であれば読める」と案内してよいかの判断に使う。
    /// </remarks>
    /// <param name="info">ページのサンプル形式。</param>
    /// <returns>非圧縮であれば自前で復号できる構成ならtrue。</returns>
    public static bool CanDecodeUncompressed(TiffSampleInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var page = new PageLayout(
            Width: info.Width, Height: info.Height, Bits: info.BitsPerSample, Spp: info.SamplesPerPixel,
            Format: info.SampleFormat, Compression: 1, Photometric: info.Photometric, Planar: 1, FillOrder: 1,
            Tiled: false, TileWidth: 0, TileHeight: 0, RowsPerStrip: info.Height,
            Offsets: Array.Empty<long>(), Counts: Array.Empty<long>(), Bayer: info.Bayer, Virtual: false,
            Predictor: 1, ExtraSample: info.ExtraSampleType);
        return info.Photometric == 2
            ? IsNativelyDecodableRgb(page, out _)
            : IsNativelyDecodable(page, out _);
    }

    // ------------------------------------------------------------------ ファイルアクセス

    /// <summary>
    /// テスト用: 真にすると、このフローの読み込みはローカルのファイルでもネットワーク上のファイルと同じく
    /// マップせずに読む(両方の経路で同じ結果になることを確かめる)。
    /// </summary>
    internal static readonly AsyncLocal<bool> ForceStreamAccess = new();

    /// <summary>
    /// テスト用: マップせずに読む経路で、ファイルから読み出す直前に(読む位置, バイト数)で呼ぶ。例外を投げると
    /// その読み出しの失敗(ネットワーク上のファイルの切断など。実際の切断はテストで起こせない)として扱われる。
    /// </summary>
    internal static readonly AsyncLocal<Action<long, int>?> BeforeStreamRead = new();

    /// <summary>ファイルをマップせずにストリームから読むか(ネットワーク上のファイル)。</summary>
    /// <param name="path">TIFFファイルのパス。</param>
    /// <returns>マップせずに読むならtrue。</returns>
    internal static bool ReadsWithoutMapping(string path)
    {
        return ForceStreamAccess.Value || RawLoader.IsNetworkPath(path);
    }

    /// <summary>
    /// 読み出しの途中の例外を、理由付きの失敗(false)にせず呼び出し側へ投げ直すか。
    /// </summary>
    /// <remarks>
    /// 復号(<paramref name="decoding"/>)では、壊れたデータ(<see cref="InvalidDataException"/>)と読み出しの失敗
    /// (<see cref="IOException"/>・<see cref="UnauthorizedAccessException"/>。ネットワーク上のファイルの切断、
    /// 消えたファイルなど)を投げ直す。false を返すと呼び出し側は WIC へ回り、「BigTIFFのページ…は未対応です」の
    /// ような読み出しの失敗と関係のない理由で失敗する。ヘッダを調べるだけのときは従来どおり理由にする
    /// (呼び出し側は WIC に任せ、WIC 自身の読み出しが同じ I/O のエラーで失敗する)。
    /// </remarks>
    /// <param name="exception">読み出しの途中の例外。</param>
    /// <param name="decoding">画素を復号しているか(ヘッダを調べるだけなら false)。</param>
    /// <returns>投げ直すならtrue。</returns>
    internal static bool RethrowsReadException(Exception exception, bool decoding)
    {
        return decoding && exception is InvalidDataException or IOException or UnauthorizedAccessException;
    }

    private static bool WithMappedFile(
        string path, ref string reason, Func<TiffBytes, bool> action, bool decoding = false)
    {
        long fileLength;
        PageTableKey key;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                // FileInfo.Exists は接続できないネットワーク上のパスでも例外でなく false を返す
                if (decoding)
                {
                    throw new FileNotFoundException("ファイルがありません。", path);
                }

                reason = "ファイルがありません。";
                return false;
            }

            fileLength = info.Length;
            key = new PageTableKey(info.FullName, fileLength, info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException && !RethrowsReadException(ex, decoding))
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
            if (ReadsWithoutMapping(path))
            {
                // ネットワーク上のファイルはマップしない(RawLoader と同じ理由)。NAS の切断・SMB の再接続で
                // ページインが EXCEPTION_IN_PAGE_ERROR になると .NET では捕捉できずプロセスごと落ちる。
                // ストリームから読めば捕捉できる IOException で済む(復号中は呼び出し側へ投げ直す)
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.RandomAccess);
                return action(new TiffBytes(new StreamWindow(stream, fileLength)) { CacheKey = key });
            }

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
                return action(new TiffBytes(pointer + accessor.PointerOffset, fileLength) { CacheKey = key });
            }
            finally
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException
                or ArgumentException or NotSupportedException
            && !RethrowsReadException(ex, decoding))
        {
            reason = ex.Message;
            return false;
        }
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
        long fillOrder = GetScalar(entries, data, TagFillOrder, header) ?? 1;

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

        if (sampleFormat is not (1 or SampleFormatUndefined))
        {
            throw new InvalidDataException(
                $"符号なし整数のTIFFのみサポートします(SampleFormat={sampleFormat})。");
        }

        // FillOrder=2 はバイト内のビット順が逆なので、そのまま読むとビット反転前の値を黙って返してしまう
        if (fillOrder != 1)
        {
            throw new InvalidDataException($"FillOrder={fillOrder} は未対応です。");
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
            if (offset < 0 || offset > data.Length - expectedBytes)
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
}
