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
        ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp",
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
            || string.Equals(extension, ".tiff", StringComparison.OrdinalIgnoreCase);
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

        if (IsTiff(path) && reason.Length > 0)
        {
            AppLog.Info($"TIFFの直接読み出しは不可のためWICで開きます: {reason}");
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
        if ((uint)pageIndex >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"ページは0〜{count - 1}です。");
        }

        // WICは32bit整数のページもGray32Floatとして返し、16bit実数はGray16として返す。
        // どちらもビット列がそのまま整数/実数に化けるため、実際の形式はファイル自身から読む
        TiffSampleInfo? sampleInfo = null;
        if (IsTiff(path))
        {
            TiffLoader.TryReadSampleInfo(path, out sampleInfo, pageIndex, cancellationToken);
        }

        return DecodeWithWicErrorHandling(() =>
        {
            BitmapFrame frame = decoder.Frames[pageIndex];
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDecodable(frame.PixelWidth, frame.PixelHeight, frame.Format);
            DecodedImage decoded =
                TryDecodeWideSamples(frame, sampleInfo, cancellationToken, progress)
                ?? DecodeFrame(frame, cancellationToken, progress);
            return decoded with { PageCount = count, PageIndex = pageIndex };
        });
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
            || (info.BitsPerSample == 16 && info.SampleFormat != 1);
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

        if (format.BitsPerPixel != channels * info.BitsPerSample)
        {
            throw new NotSupportedException(
                $"{info.BitsPerSample}bit×{channels}サンプルのTIFFですが、" +
                $"デコード結果は{format.BitsPerPixel}bit/画素でした。");
        }

        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        long pixels = (long)width * height;
        int outputChannels = channels == 1 ? 1 : 3;

        // 32bitサンプルは中間のint[]を持つぶん、通常経路より1画素あたりの所要が大きい
        long bytes = pixels * ((4L * channels) + (2L * outputChannels)
            + (outputChannels == 1 ? 0 : 2));
        if (bytes > MaxDecodedBytes)
        {
            throw new NotSupportedException(
                $"{width}×{height} の{info.BitsPerSample}bitサンプル画像は展開に約 " +
                $"{bytes / (1024.0 * 1024 * 1024):F1} GB 必要で、上限 " +
                $"{MaxDecodedBytes / (1024.0 * 1024 * 1024):F1} GB を超えています。");
        }

        if (pixels * channels > int.MaxValue)
        {
            throw new NotSupportedException("サンプル数が多すぎます。");
        }

        SampleInterpretation interpretation = info.Interpretation;
        int[] bits = ReadWideSamples(
            frame, width, height, channels, info.BitsPerSample / 8,
            interpretation == SampleInterpretation.SignedInteger, ct, progress);
        long samples = pixels * channels;
        if (channels == 4)
        {
            // アルファは値域にも出力にも入れない
            for (long i = 0; i < pixels; i++)
            {
                if ((i & 0xFFFFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                bits[(i * 3) + 0] = bits[i * 4];
                bits[(i * 3) + 1] = bits[(i * 4) + 1];
                bits[(i * 3) + 2] = bits[(i * 4) + 2];
            }

            channels = 3;
            samples = pixels * 3;
        }

        SampleRange range = SampleScaling.Scan(bits, samples, interpretation, ct);
        SampleScaling scaling = SampleScaling.FromRange(range);
        progress?.Report(0.85);

        var codes = new ushort[samples];
        scaling.Convert(bits, samples, interpretation, codes, ct);
        progress?.Report(1);
        ct.ThrowIfCancellationRequested();

        string note = scaling.Describe();
        if (channels == 1)
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

    /// <summary>サンプルのビット列を32bit語の配列として読み出す。</summary>
    private static int[] ReadWideSamples(
        BitmapSource frame, int width, int height, int channels, int bytesPerSample,
        bool signExtend, CancellationToken ct, IProgress<double>? progress)
    {
        int stride = checked(width * channels * bytesPerSample);
        int bandRows = Math.Min(height, Math.Max(1, (1 << 20) / stride));
        var bits = new int[checked((long)width * height * channels)];
        ushort[]? narrow = bytesPerSample == 2
            ? new ushort[checked(bandRows * width * channels)]
            : null;
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
                for (int i = 0; i < count; i++)
                {
                    bits[offset + i] = signExtend ? (short)narrow[i] : narrow[i];
                }
            }

            progress?.Report(0.7 * (y + rows) / height);
        }

        ct.ThrowIfCancellationRequested();
        return bits;
    }

    private static DecodedImage DecodeFrame(BitmapSource frame, CancellationToken ct, IProgress<double>? progress)
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
            return new DecodedImage(RawImage.FromPixels(
                new RawFormat { Width = width, Height = height, BitDepth = format == PixelFormats.Gray16 ? 16 : 8 },
                pixels), null);
        }

        bool sixteenBit = format == PixelFormats.Rgb48 || format == PixelFormats.Rgba64;
        BitmapSource source = sixteenBit || format == PixelFormats.Bgra32 ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int inputChannels = format == PixelFormats.Rgb48 ? 3 : 4;
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
