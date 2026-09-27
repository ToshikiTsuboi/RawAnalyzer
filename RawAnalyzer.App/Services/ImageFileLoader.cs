using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>デコード済み画像ファイルの読込結果。</summary>
/// <param name="Luminance">解析・グレー表示に使う輝度画像。</param>
/// <param name="Color">カラー画像(グレースケール画像ならnull)。</param>
public sealed record DecodedImage(RawImage Luminance, ColorImage? Color)
{
    /// <summary>ファイル内のページ数。画素は1ページだけを保持する。</summary>
    public int PageCount { get; init; } = 1;

    /// <summary>読み込んだページ(0起点)。Luminance内のフレーム番号は常に0。</summary>
    public int PageIndex { get; init; }

    /// <summary>
    /// 32bit実数などを16bitへ写した際の対応関係(表示用)。等倍で読めた場合はnull。
    /// </summary>
    public string? ValueNote { get; init; }
}

/// <summary>
/// WIC(BitmapDecoder)によるJPEG/PNG/TIFF/BMPの読込。
/// Coreはウィンドウ系アセンブリを参照できないためApp層に置く。
/// 画素値はガンマ変換を避けるためネイティブフォーマットのまま取り出す。
/// </summary>
internal static class ImageFileLoader
{
    /// <summary>
    /// デコード可能な最大画素数。WICが全画素をメモリ上に展開するため、
    /// これを超えるものは読み込まずに拒否する。
    /// </summary>
    public const long MaxPixels = 200_000_000;

    /// <summary>
    /// デコードに使う中間バッファの合計上限(バイト)。
    /// </summary>
    /// <remarks>
    /// 画素数だけで制限すると、1画素あたりの必要バイト数が形式で最大16倍違うため
    /// 上限内でも数GBを同時に確保してしまう(RGBA64の2億画素で約3.2GB)。
    /// 形式ごとの所要バイトで判定する。
    /// </remarks>
    public const long MaxDecodedBytes = 1_500_000_000;

    /// <summary>WICで読み込む拡張子。</summary>
    public static readonly string[] SupportedExtensions =
    {
        ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".dng", ".bmp",
    };

    /// <summary>指定拡張子がWIC読込対象か判定する。</summary>
    /// <param name="path">ファイルパス。</param>
    /// <returns>対象ならtrue。</returns>
    public static bool IsSupported(string path)
    {
        return SupportedExtensions.Contains(
            Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsTiff(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".tif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tiff", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".dng", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 展開後の画素形式から、出力とWIC内部バッファの保守的な合計バイト数を見積もる。
    /// </summary>
    /// <param name="format">デコード後の画素形式。</param>
    /// <returns>1画素あたりのバイト数。</returns>
    internal static int EstimateBytesPerPixel(PixelFormat format)
    {
        if (format == PixelFormats.Gray16)
        {
            return 2; // ushort[]
        }

        if (format == PixelFormats.Gray8)
        {
            return 1 + 2; // byte[] + ushort[]
        }

        if (format == PixelFormats.Rgb48)
        {
            return 6 + 2; // ushort[px*3](出力と共用) + 輝度
        }

        if (format == PixelFormats.Rgba64)
        {
            return 8 + 6 + 2; // 読み出し + RGB詰め直し + 輝度
        }

        // その他はBgra32へ変換して取り出す: byte[px*4] + ushort[px*3] + 輝度
        if (format == PixelFormats.Gray32Float)
        {
            return 4 + 2; // int[](32bitサンプル) + 16bitコード
        }

        if (format.BitsPerPixel is 96 or 128)
        {
            // 32bit×3/4サンプル + RGBコード + 輝度
            return (format.BitsPerPixel / 8) + 6 + 2;
        }

        return 4 + 6 + 2;
    }

    /// <summary>
    /// デコードして問題ないサイズかを、確保する前に検証する。
    /// </summary>
    /// <param name="width">幅。</param>
    /// <param name="height">高さ。</param>
    /// <param name="format">画素形式。</param>
    /// <exception cref="NotSupportedException">上限を超える場合。</exception>
    internal static void EnsureDecodable(int width, int height, PixelFormat format)
    {
        long pixels = (long)width * height;
        if (pixels > MaxPixels)
        {
            throw new NotSupportedException(
                $"{width}×{height} ({pixels / 1_000_000.0:F0}M画素) は" +
                $"デコード画像の上限 {MaxPixels / 1_000_000} M画素を超えています。");
        }

        long bytes = pixels * EstimateBytesPerPixel(format);
        if (bytes > MaxDecodedBytes)
        {
            throw new NotSupportedException(
                $"{width}×{height} の{format}画像は展開に約 " +
                $"{bytes / (1024.0 * 1024 * 1024):F1} GB 必要で、上限 " +
                $"{MaxDecodedBytes / (1024.0 * 1024 * 1024):F1} GB を超えています。" +
                "非圧縮の16bitグレースケールTIFFかrawであれば、この制限なしに開けます。");
        }
    }

    /// <summary>指定ページだけを読み込む。全ファイルや全ページの画素を一括展開しない。</summary>
    /// <param name="path">ファイルパス。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <param name="progress">対象ページの読込進捗(0〜1)。</param>
    /// <param name="pageIndex">ページ番号(0起点)。通常の画像ファイルは0。</param>
    /// <returns>対象ページの画像とファイル全体のページ数。</returns>
    public static DecodedImage Load(
        string path, CancellationToken cancellationToken = default,
        IProgress<double>? progress = null, int pageIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0);

        // TIFFはまずヘッダを自前で読む。WICは32bit整数をGray32Floatとして返す、
        // 未知の圧縮を全画素0で返す、CFAを勝手に現像するなど「黙って壊れる」ため、
        // 形式が分かってから経路を決める
        TiffSampleInfo? sampleInfo = null;
        string infoReason = "";
        if (IsTiff(path))
        {
            TiffLoader.TryReadSampleInfo(path, out sampleInfo, out infoReason, pageIndex, cancellationToken);
        }

        // IFDごとに独立したオフセットを使い、ページ間の隙間を画素と誤解釈しない。
        if (TiffLoader.TryProbePixelLayout(path, out TiffPixelLayout? layout, out string reason,
                pageIndex, cancellationToken) && layout!.BitDepth == 16)
        {
            RawImage image = RawLoader.Load(path, TiffLoader.ToRawFormat(layout), cancellationToken, progress);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new DecodedImage(image, null) { PageCount = layout.PageCount, PageIndex = pageIndex };
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        if (sampleInfo is not null && NeedsNativeDecode(sampleInfo))
        {
            DecodedImage? native = sampleInfo.SamplesPerPixel == 1
                ? TryDecodeNativeGray(path, pageIndex, sampleInfo, cancellationToken, progress)
                : TryDecodeNativeRgb(path, pageIndex, sampleInfo, cancellationToken, progress);
            if (native is not null)
            {
                return native;
            }
        }

        if (IsTiff(path) && reason.Length > 0)
        {
            AppLog.Info($"TIFFの直接読み出しは不可のためWICで開きます: {reason}");
        }

        if (sampleInfo is not null)
        {
            EnsureWicCapable(sampleInfo);
        }
        else if (IsTiff(path) && infoReason.Length > 0)
        {
            AppLog.Info($"TIFFヘッダを解釈できないためWICに任せます: {infoReason}");
        }

        // ストリームは選択ページのCopyPixels完了まで保持する。戻り値にWIC資源は含めない。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1 << 16);
        var (decoder, count) = DecodeWithWicErrorHandling(() =>
        {
            var created = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            return (created, created.Frames.Count);
        });
        if (count is < 1 or > TiffLoader.MaxPages)
        {
            throw new NotSupportedException($"画像のページ数が対応範囲外です: {count}");
        }

        // 呼び出し側の引数エラーはWICのデコード失敗へ変換しない。
        if (sampleInfo is null && (uint)pageIndex >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"ページは0〜{count - 1}です。");
        }

        // ページ数はTIFFのページ表を正とする。WICは縮小IFDをフレームに数えないので、
        // ページ表が持つWICフレーム番号で引く
        int frameIndex = sampleInfo?.WicFrameIndex ?? pageIndex;
        int pageCount = sampleInfo?.PageCount ?? count;
        if ((uint)frameIndex >= (uint)count)
        {
            throw new InvalidDataException(
                $"WICが認識したページ数({count})がTIFFのページ構成と一致しません。");
        }

        return DecodeWithWicErrorHandling(() =>
        {
            BitmapFrame frame = decoder.Frames[frameIndex];
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDecodable(frame.PixelWidth, frame.PixelHeight, frame.Format);
            DecodedImage decoded =
                TryDecodeWideSamples(frame, sampleInfo, cancellationToken, progress)
                ?? DecodeFrame(frame, sampleInfo, cancellationToken, progress);
            return decoded with { PageCount = pageCount, PageIndex = pageIndex };
        });
    }

    /// <summary>
    /// WICでは正しく読めない非圧縮ページか。該当すれば 1サンプル/画素は
    /// <see cref="TiffLoader.TryDecodeUncompressed"/>、RGBは <see cref="TiffLoader.TryDecodeUncompressedRgb"/> で読む。
    /// </summary>
    /// <param name="info">ページのサンプル形式。</param>
    /// <returns>自前で復号すべきならtrue。</returns>
    internal static bool NeedsNativeDecode(TiffSampleInfo info)
    {
        if (info.Compression != 1)
        {
            return false;
        }

        if (info.SamplesPerPixel != 1)
        {
            // WICは半精度のRGBを0〜1へ切り詰めてガンマを掛けた整数で返し(元の値に戻せない)、
            // 16bit符号ありのRGBは復号自体に失敗する
            return info.Photometric == 2 && info.SamplesPerPixel is 3 or 4
                && (IsHalfFloatColor(info) || IsSignedInt16Color(info));
        }

        return info.IsBigTiff                       // WICはBigTIFFを開けない
            || info.IsVirtualPage                   // ImageJの仮想ページはWICに存在しない
            || info.WicFrameIndex < 0               // SubIFD(DNG本体など)
            || info.IsRawPhotometric                // CFAはWICが現像してしまう
            || info.BitsPerSample is 10 or 12 or 14 or 24 or 64
            || (info.SampleFormat == 2 && info.BitsPerSample == 8)
            || (info.SampleFormat == 3 && info.BitsPerSample == 64)
            || WicBreaksWhiteIsZero(info);          // WICのWhiteIsZero反転で値が壊れる
    }

    /// <summary>
    /// WICのWhiteIsZero反転で値が壊れる形式か。WICは8/16bitをビット反転、32bitを実数の 1−v で
    /// 反転して返す。整数8/16bitと32bit実数ではこれが自前復号と同じ向きになるが、半精度はビット反転で
    /// 非数や別の値になり、32bit整数は実数として 1−v されて元に戻せない。
    /// </summary>
    /// <param name="info">ページのサンプル形式。</param>
    /// <returns>該当すればtrue。</returns>
    private static bool WicBreaksWhiteIsZero(TiffSampleInfo info)
    {
        return info.Photometric == 0
            && ((info.SampleFormat == 3 && info.BitsPerSample == 16)
                || (info.SampleFormat != 3 && info.BitsPerSample == 32));
    }

    /// <summary>
    /// 16bit実数(半精度)のカラーページか。WICはこれを0〜1へ切り詰めてsRGBのガンマを掛けた
    /// 16bit整数(Rgb48/Rgba64)として返すため、1を超える値・負値・線形性が失われる
    /// (グレーは生のビット列を返すので対象外)。
    /// </summary>
    /// <param name="info">ページのサンプル形式。</param>
    /// <returns>該当すればtrue。</returns>
    private static bool IsHalfFloatColor(TiffSampleInfo info)
    {
        return info.SampleFormat == 3 && info.BitsPerSample == 16 && info.Photometric is not (0 or 1);
    }

    /// <summary>
    /// 16bit符号あり整数のカラーページか。WICはこれを復号できない(RGB/RGBAとも読込エラーになる。
    /// グレーは生のビット列をGray16で返すので対象外)。
    /// </summary>
    /// <param name="info">ページのサンプル形式。</param>
    /// <returns>該当すればtrue。</returns>
    private static bool IsSignedInt16Color(TiffSampleInfo info)
    {
        return info.SampleFormat == 2 && info.BitsPerSample == 16 && info.Photometric is not (0 or 1);
    }

    private static DecodedImage? TryDecodeNativeGray(
        string path, int pageIndex, TiffSampleInfo info, CancellationToken ct, IProgress<double>? progress)
    {
        EnsureDecodable(info.Width, info.Height, PixelFormats.Gray16);
        if (!TiffLoader.TryDecodeUncompressed(path, pageIndex, out RawImage? native,
                out SampleScaling? scaling, out string reason, ct, progress))
        {
            AppLog.Info($"TIFFの自前復号は不可のためWICで開きます: {reason}");
            return null;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            return new DecodedImage(native!, null)
            {
                PageCount = info.PageCount,
                PageIndex = pageIndex,
                ValueNote = scaling?.Describe(info.BitsPerSample),
            };
        }
        catch
        {
            native!.Dispose();
            throw;
        }
    }

    private static DecodedImage? TryDecodeNativeRgb(
        string path, int pageIndex, TiffSampleInfo info, CancellationToken ct, IProgress<double>? progress)
    {
        EnsureDecodable(info.Width, info.Height, PixelFormats.Rgb48);
        if (!TiffLoader.TryDecodeUncompressedRgb(path, pageIndex, out ColorImage? color,
                out SampleScaling? scaling, out string reason, ct, progress))
        {
            AppLog.Info($"TIFFの自前復号は不可です: {reason}");
            return null;
        }

        RawImage luminance = color!.ToLuminance(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return new DecodedImage(luminance, color)
            {
                PageCount = info.PageCount,
                PageIndex = pageIndex,
                ValueNote = scaling!.Describe(info.BitsPerSample),
            };
        }
        catch
        {
            luminance.Dispose();
            throw;
        }
    }

    /// <summary>
    /// WICに渡して安全な形式かを検査し、そうでなければ理由付きで拒否する。
    /// </summary>
    /// <remarks>
    /// WICは未知の圧縮方式をエラーにせず全画素0の画像として返す。センサ評価では
    /// 「真っ黒な正常画像」が最も危険なので、ヘッダで分かる範囲は先に弾く。
    /// </remarks>
    /// <param name="info">ページのサンプル形式。</param>
    /// <exception cref="InvalidDataException">WICでは正しく読めない形式の場合。</exception>
    internal static void EnsureWicCapable(TiffSampleInfo info)
    {
        if (info.IsBigTiff)
        {
            throw new InvalidDataException(
                "BigTIFFは非圧縮のグレースケール/CFAページのみ対応しています。");
        }

        if (!TiffLoader.IsWicCompression(info.Compression))
        {
            throw new InvalidDataException(
                $"圧縮方式 {TiffLoader.DescribeCompression(info.Compression)} は未対応です。");
        }

        if (info.IsRawPhotometric)
        {
            throw new InvalidDataException(
                "CFA/LinearRaw(DNG)は非圧縮のみ対応しています(可逆JPEG圧縮のDNGは未対応)。");
        }

        if (info.WicFrameIndex < 0)
        {
            throw new InvalidDataException(
                "このページ(SubIFD、またはImageJの連続スタック)は非圧縮の1サンプル/画素のみ対応しています。");
        }

        if (info.SampleFormat is 5 or 6)
        {
            throw new InvalidDataException($"複素数(SampleFormat={info.SampleFormat})のTIFFは未対応です。");
        }

        if (info.Photometric == 6 && info.Compression != 7)
        {
            throw new InvalidDataException("非圧縮のYCbCr TIFFは未対応です(JPEG圧縮のYCbCrは読めます)。");
        }

        if (info.Predictor == 2 && info.BitsPerSample == 32)
        {
            throw new InvalidDataException("32bitサンプルの水平差分予測(Predictor=2)は未対応です。");
        }

        if (IsHalfFloatColor(info))
        {
            throw new InvalidDataException(
                "16bit実数(半精度)のカラーTIFFは非圧縮のRGBのみ対応しています" +
                "(WICは値を0〜1に切り詰め、ガンマ変換した整数で返すため元の値に戻せません)。");
        }

        if (IsSignedInt16Color(info))
        {
            throw new InvalidDataException(
                "16bit符号あり整数のカラーTIFFは非圧縮のRGB(3〜4サンプル/画素)のみ対応しています" +
                "(WICはこの形式を復号できません)。");
        }

        if (WicBreaksWhiteIsZero(info))
        {
            throw new InvalidDataException(
                "WhiteIsZero(Photometric=0)の16bit実数・32bit整数TIFFは、非圧縮の1サンプル/画素のみ" +
                "対応しています(WICの白黒反転で値が壊れるため)。");
        }

        bool bitsOk = info.SampleFormat switch
        {
            2 => info.BitsPerSample is 8 or 16 or 32,
            3 => info.BitsPerSample is 16 or 32,
            _ => info.BitsPerSample is 1 or 2 or 4 or 8 or 12 or 16 or 32,
        };
        if (!bitsOk)
        {
            string kind = info.SampleFormat switch { 2 => "符号あり整数", 3 => "実数", _ => "整数" };
            throw new InvalidDataException(
                $"{info.BitsPerSample}bit {kind}の{TiffLoader.DescribeCompression(info.Compression)}TIFFは未対応です" +
                "(非圧縮であれば読めます)。");
        }
    }

    internal static T DecodeWithWicErrorHandling<T>(Func<T> decode)
    {
        try
        {
            return decode();
        }
        catch (Exception ex) when (ex is FileFormatException or ArgumentException
            or NotSupportedException or COMException)
        {
            throw new InvalidDataException($"画像をデコードできません: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// WICがサンプル形式を無視して返すページを読み、16bitコードへ写す。
    /// </summary>
    /// <remarks>
    /// WICは32bitのページをサンプル形式によらずGray32Floatとして、16bitのページを
    /// Gray16として返し、いずれもビット列をそのまま渡してくる。整数を実数として
    /// 読むと1,000,000が1.4e-39になり、半精度の1.0は15360という別の整数になる。
    /// TIFFのSampleFormatで解釈を決め、値域から16bitへの対応関係を作る。
    /// </remarks>
    /// <param name="frame">対象ページ。</param>
    /// <param name="info">TIFFのサンプル形式。非TIFFではnull。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <param name="progress">進捗(0〜1)。</param>
    /// <returns>読み込んだ画像。通常経路で読むべき形式ならnull。</returns>
    private static DecodedImage? TryDecodeWideSamples(
        BitmapSource frame, TiffSampleInfo? info, CancellationToken ct, IProgress<double>? progress)
    {
        PixelFormat format = frame.Format;

        // 非TIFFでWICが実数と明示した場合だけ、メタデータなしでも実数として扱う
        info ??= format == PixelFormats.Gray32Float
            ? new TiffSampleInfo(32, 1, 3, 1)
            : null;
        if (info is null)
        {
            return null;
        }

        // 32bitの全形式と、16bitの実数・符号あり整数が対象。
        // 16bitの符号なし整数だけがWICの返す値をそのまま使える
        bool wide = info.BitsPerSample == 32
            || (info.BitsPerSample == 16 && info.SampleFormat != 1)
            || (info.BitsPerSample == 8 && info.SampleFormat == 2);
        if (!wide)
        {
            return null;
        }

        int channels = info.SamplesPerPixel;
        if (channels is < 1 or > 4)
        {
            throw new NotSupportedException(
                $"1画素あたり{channels}サンプルのTIFFには対応していません。");
        }

        // グレー+アルファなど多チャネルのグレーは、先頭チャネルだけをグレーとして開く(DecodeFrameと同じ)。
        // WICはこれをRGBA(R=G=B=グレー)や先頭チャネルだけのグレーに展開して返すため、
        // 1画素のサンプル数は元のSamplesPerPixelではなくWICの画素幅から求める
        bool gray = channels == 1 || info.Photometric is 0 or 1;
        int decodedChannels = format.BitsPerPixel / info.BitsPerSample;
        bool layoutOk = format.BitsPerPixel % info.BitsPerSample == 0
            && (gray
                ? decodedChannels == channels || (channels > 1 && decodedChannels is >= 1 and <= 4)
                : decodedChannels == channels && channels >= 3);
        if (!layoutOk)
        {
            throw new NotSupportedException(
                $"{info.BitsPerSample}bit×{channels}サンプルのTIFFですが、" +
                $"デコード結果は{format.BitsPerPixel}bit/画素でした。");
        }

        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        long pixels = (long)width * height;
        int outputChannels = gray ? 1 : 3;

        // 32bitサンプルは中間のint[]を持つぶん、通常経路より1画素あたりの所要が大きい
        long bytes = pixels * ((4L * decodedChannels) + (2L * outputChannels)
            + (outputChannels == 1 ? 0 : 2));
        if (bytes > MaxDecodedBytes)
        {
            throw new NotSupportedException(
                $"{width}×{height} の{info.BitsPerSample}bitサンプル画像は展開に約 " +
                $"{bytes / (1024.0 * 1024 * 1024):F1} GB 必要で、上限 " +
                $"{MaxDecodedBytes / (1024.0 * 1024 * 1024):F1} GB を超えています。");
        }

        if (pixels * decodedChannels > int.MaxValue)
        {
            throw new NotSupportedException("サンプル数が多すぎます。");
        }

        SampleInterpretation interpretation = info.Interpretation;
        int[] bits = ReadWideSamples(
            frame, width, height, decodedChannels, info.BitsPerSample / 8,
            interpretation == SampleInterpretation.SignedInteger, ct, progress);

        // 出力のチャネルへ詰め直す。グレーは先頭チャネル、カラーはRGB。WICは8bitのRGBを
        // B,G,Rの順(Bgr24/Bgra32/Pbgra32)で返すので、DecodeFrameと同じくRGBの順へ並べ直す。
        // アルファなど残りのチャネルは値域にも出力にも入れない
        bool bgr = !gray && IsBgrOrder(format);
        if (decodedChannels != outputChannels || bgr)
        {
            int red = bgr ? 2 : 0;
            int blue = bgr ? 0 : 2;
            for (long i = 0; i < pixels; i++)
            {
                if ((i & 0xFFFFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                long source = i * decodedChannels;
                if (gray)
                {
                    bits[i] = bits[source];
                    continue;
                }

                int r = bits[source + red];
                int g = bits[source + 1];
                int b = bits[source + blue];
                bits[(i * 3) + 0] = r;
                bits[(i * 3) + 1] = g;
                bits[(i * 3) + 2] = b;
            }
        }

        long samples = pixels * outputChannels;
        SampleRange range = SampleScaling.Scan(bits, samples, interpretation, ct);
        SampleScaling scaling = SampleScaling.FromRange(range);
        progress?.Report(0.85);

        var codes = new ushort[samples];
        scaling.Convert(bits, samples, interpretation, codes, ct);
        progress?.Report(1);
        ct.ThrowIfCancellationRequested();

        string note = scaling.Describe(info.BitsPerSample);
        if (gray)
        {
            var rawFormat = new RawFormat { Width = width, Height = height, BitDepth = 16 };
            return new DecodedImage(RawImage.FromPixels(rawFormat, codes), null)
            {
                ValueNote = note,
            };
        }

        ColorImage color = ColorImage.FromInterleaved(width, height, 16, codes);
        RawImage luminance = color.ToLuminance(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return new DecodedImage(luminance, color) { ValueNote = note };
        }
        catch
        {
            luminance.Dispose();
            throw;
        }
    }

    /// <summary>WICの画素形式がB,G,Rの順に並ぶ(8bitのカラー形式)か。</summary>
    private static bool IsBgrOrder(PixelFormat format)
    {
        return format == PixelFormats.Bgr24 || format == PixelFormats.Bgr32
            || format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32;
    }

    /// <summary>サンプルのビット列を32bit語の配列として読み出す。</summary>
    private static int[] ReadWideSamples(
        BitmapSource frame, int width, int height, int channels, int bytesPerSample,
        bool signExtend, CancellationToken ct, IProgress<double>? progress)
    {
        int stride = checked(width * channels * bytesPerSample);
        int bandRows = Math.Min(height, Math.Max(1, (1 << 20) / stride));
        var bits = new int[checked((long)width * height * channels)];
        Array? narrow = bytesPerSample switch
        {
            1 => new byte[checked(bandRows * width * channels)],
            2 => new ushort[checked(bandRows * width * channels)],
            _ => null,
        };
        for (int y = 0; y < height; y += bandRows)
        {
            ct.ThrowIfCancellationRequested();
            int rows = Math.Min(bandRows, height - y);
            int offset = y * width * channels;
            var rect = new Int32Rect(0, y, width, rows);
            if (narrow is null)
            {
                frame.CopyPixels(rect, bits, stride, offset);
            }
            else
            {
                frame.CopyPixels(rect, narrow, stride, 0);
                int count = rows * width * channels;
                if (narrow is ushort[] words)
                {
                    for (int i = 0; i < count; i++)
                    {
                        bits[offset + i] = signExtend ? (short)words[i] : words[i];
                    }
                }
                else
                {
                    var bytes8 = (byte[])narrow;
                    for (int i = 0; i < count; i++)
                    {
                        bits[offset + i] = signExtend ? (sbyte)bytes8[i] : bytes8[i];
                    }
                }
            }

            progress?.Report(0.7 * (y + rows) / height);
        }

        ct.ThrowIfCancellationRequested();
        return bits;
    }

    private static DecodedImage DecodeFrame(
        BitmapSource frame, TiffSampleInfo? info, CancellationToken ct, IProgress<double>? progress)
    {
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        PixelFormat format = frame.Format;
        if (format == PixelFormats.Gray16 || format == PixelFormats.Gray8)
        {
            ushort[] pixels = ReadBands(frame, 1, 1, format == PixelFormats.Gray16, false, ct, progress);
            ct.ThrowIfCancellationRequested();
            progress?.Report(1);
            ct.ThrowIfCancellationRequested();

            // WICは12bit詰め込みを v<<4 のGray16で返す。内部表現と同じなので深度だけ12bitにする
            int bitDepth = format == PixelFormats.Gray8 ? 8
                : info is { BitsPerSample: 12, SampleFormat: 1 } ? 12 : 16;
            return new DecodedImage(RawImage.FromPixels(
                new RawFormat { Width = width, Height = height, BitDepth = bitDepth },
                pixels), null);
        }

        // 1/2/4bitのグレーはBgra32へ広げるとカラー画像扱いになる。Gray8へ変換してグレーのまま取り込む
        if (format == PixelFormats.BlackWhite || format == PixelFormats.Gray2 || format == PixelFormats.Gray4)
        {
            var gray8 = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
            ushort[] pixels = ReadBands(gray8, 1, 1, false, false, ct, progress);
            ct.ThrowIfCancellationRequested();
            progress?.Report(1);
            ct.ThrowIfCancellationRequested();
            return new DecodedImage(RawImage.FromPixels(
                new RawFormat { Width = width, Height = height, BitDepth = 8 }, pixels), null);
        }

        bool sixteenBit = format == PixelFormats.Rgb48 || format == PixelFormats.Rgba64;
        BitmapSource source = sixteenBit || format == PixelFormats.Bgra32 ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int inputChannels = format == PixelFormats.Rgb48 ? 3 : 4;

        // グレー+アルファや多チャネルのグレーページをWICはRGBAで返す。
        // 先頭チャネルだけをグレーとして取り込み、カラー画像扱いにしない
        if (info is { Photometric: 0 or 1, SamplesPerPixel: >= 2 })
        {
            ushort[] gray = ReadBands(source, inputChannels, 1, sixteenBit, !sixteenBit, ct, progress);
            ct.ThrowIfCancellationRequested();
            progress?.Report(1);
            ct.ThrowIfCancellationRequested();
            return new DecodedImage(RawImage.FromPixels(
                new RawFormat { Width = width, Height = height, BitDepth = sixteenBit ? 16 : 8 },
                gray), null);
        }

        ushort[] rgb = ReadBands(source, inputChannels, 3, sixteenBit, !sixteenBit, ct, progress);
        ColorImage color = ColorImage.FromInterleaved(width, height, sixteenBit ? 16 : 8, rgb);
        RawImage luminance = color.ToLuminance(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(1);
            ct.ThrowIfCancellationRequested();
            return new DecodedImage(luminance, color);
        }
        catch
        {
            luminance.Dispose();
            throw;
        }
    }

    private static ushort[] ReadBands(
        BitmapSource source, int inputChannels, int outputChannels, bool sixteenBit, bool bgr,
        CancellationToken ct, IProgress<double>? progress)
    {
        ct.ThrowIfCancellationRequested();
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = checked(width * inputChannels * (sixteenBit ? 2 : 1));
        int bandRows = Math.Min(height, Math.Max(1, (1 << 20) / stride));
        int bandSamples = checked(width * bandRows * inputChannels);
        Array buffer = sixteenBit ? new ushort[bandSamples] : new byte[bandSamples];
        var output = new ushort[checked(width * height * outputChannels)];
        for (int y = 0; y < height; y += bandRows)
        {
            ct.ThrowIfCancellationRequested();
            int rows = Math.Min(bandRows, height - y);
            source.CopyPixels(new Int32Rect(0, y, width, rows), buffer, stride, 0);
            int pixels = rows * width;
            int offset = y * width * outputChannels;
            if (buffer is ushort[] words && inputChannels == outputChannels)
            {
                words.AsSpan(0, pixels * outputChannels).CopyTo(output.AsSpan(offset));
            }
            else
            {
                for (int i = 0; i < pixels; i++)
                {
                    if ((i & 4095) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    for (int c = 0; c < outputChannels; c++)
                    {
                        int input = i * inputChannels + (bgr ? 2 - c : c);
                        output[offset + i * outputChannels + c] = buffer is ushort[] data
                            ? data[input] : (ushort)(((byte[])buffer)[input] * 257);
                    }
                }
            }

            progress?.Report(0.9 * (y + rows) / height);
        }

        ct.ThrowIfCancellationRequested();
        return output;
    }
}
