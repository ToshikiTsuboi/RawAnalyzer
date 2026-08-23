using System.Buffers;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>デコード済み画像ファイルの読込結果。</summary>
/// <param name="Luminance">解析・グレー表示に使う輝度画像。</param>
/// <param name="Color">カラー画像(グレースケール画像ならnull)。</param>
public sealed record DecodedImage(RawImage Luminance, ColorImage? Color);

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
    /// 展開後の画素形式から、1画素あたりに確保する中間バッファのバイト数を見積もる。
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

    /// <summary>
    /// 画像ファイルを読み込む。カラー画像は輝度画像とカラー画像の両方を返す。
    /// </summary>
    /// <param name="path">ファイルパス。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <param name="progress">読み込みの進捗(0〜1)。低速なストレージ向けの表示用。</param>
    /// <returns>読込結果。</returns>
    /// <exception cref="InvalidDataException">デコードできない場合。</exception>
    /// <exception cref="NotSupportedException">画素数が上限を超える場合。</exception>
    public static DecodedImage Load(
        string path,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        // 非圧縮グレースケール16bit TIFFは画素データが連続しているので、WICでデコード
        // せずrawと同じ経路(必要ならMemoryMappedFile)で開く。画素数の上限に縛られない。
        // 8bitはWIC経路(×257で16bitフルスケールへ展開)の方が表示が正確なため対象外
        if (TiffLoader.TryProbePixelLayout(path, out TiffPixelLayout? layout, out string reason)
            && layout!.BitDepth == 16)
        {
            RawFormat tiffFormat = TiffLoader.ToRawFormat(layout);
            AppLog.Info(
                $"TIFFを直接読み出し: {tiffFormat.Width}×{tiffFormat.Height} " +
                $"{tiffFormat.BitDepth}bit (オフセット {tiffFormat.HeaderOffset})");
            return new DecodedImage(
                RawLoader.Load(path, tiffFormat, cancellationToken, progress), null);
        }

        if (IsTiff(path) && reason.Length > 0)
        {
            AppLog.Info($"TIFFの直接読み出しは不可のためWICで開きます: {reason}");
        }

        BitmapFrame frame;
        try
        {
            // WICにURIを渡すと転送の進捗が取れないため、自前でメモリへ読んでから
            // デコードする。NASなどの低速ストレージでは転送が時間の大半を占める
            using Stream source = ReadToMemory(path, cancellationToken, progress);

            // まずヘッダだけ読んで(BitmapCacheOption.None)画素数を検証する。
            // OnLoadでデコードしてから拒否したのでは、上限の目的
            // (巨大画像でメモリを使い切らない)を果たせない
            var probe = BitmapDecoder.Create(
                source,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            BitmapFrame probeFrame = probe.Frames[0];
            EnsureDecodable(
                probeFrame.PixelWidth, probeFrame.PixelHeight, probeFrame.Format);

            source.Position = 0;
            var decoder = BitmapDecoder.Create(
                source,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }
        catch (Exception ex) when (ex is FileFormatException or ArgumentException)
        {
            throw new InvalidDataException($"画像をデコードできません: {ex.Message}", ex);
        }

        // 残りはメモリ上の変換のみで、ファイル転送に比べれば短い
        progress?.Report(1.0);

        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        long pixelCount = (long)width * height;
        PixelFormat format = frame.Format;

        if (format == PixelFormats.Gray16)
        {
            var pixels = new ushort[(long)width * height];
            frame.CopyPixels(pixels, width * 2, 0);
            var rawFormat = new RawFormat { Width = width, Height = height, BitDepth = 16 };
            return new DecodedImage(RawImage.FromPixels(rawFormat, pixels), null);
        }

        if (format == PixelFormats.Gray8)
        {
            var bytes = new byte[(long)width * height];
            frame.CopyPixels(bytes, width, 0);
            var pixels = new ushort[(long)width * height];
            for (long i = 0; i < pixels.LongLength; i++)
            {
                // ×257 で 0..255 → 0..65535(<<8 だと白が65280止まりで表示が1コード暗い)。
                // 257=0x101 なので value>>8 によるcode復元は全値で厳密に保たれる
                pixels[i] = (ushort)(bytes[i] * 257);
            }

            var rawFormat = new RawFormat { Width = width, Height = height, BitDepth = 8 };
            return new DecodedImage(RawImage.FromPixels(rawFormat, pixels), null);
        }

        if (format == PixelFormats.Rgb48 || format == PixelFormats.Rgba64)
        {
            int channels = format == PixelFormats.Rgb48 ? 3 : 4;
            var source = new ushort[pixelCount * channels];
            frame.CopyPixels(source, width * channels * 2, 0);

            ushort[] rgb;
            if (channels == 3)
            {
                // Rgb48 は長さもレイアウトも出力と同一。ColorImage は配列を
                // 参照保持するので、もう1本確保してコピーする必要はない
                rgb = source;
            }
            else
            {
                rgb = new ushort[pixelCount * 3];
                for (long i = 0; i < pixelCount; i++)
                {
                    rgb[i * 3] = source[i * channels];
                    rgb[i * 3 + 1] = source[i * channels + 1];
                    rgb[i * 3 + 2] = source[i * channels + 2];
                }
            }

            ColorImage color16 = ColorImage.FromInterleaved(width, height, 16, rgb);
            return new DecodedImage(color16.ToLuminance(), color16);
        }

        // その他(Bgr24/Bgra32/Indexed/CMYK等)はBgra32へ変換して取り出す
        BitmapSource converted = format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var bgra = new byte[(long)width * height * 4];
        converted.CopyPixels(bgra, width * 4, 0);
        var rgb8 = new ushort[(long)width * height * 3];
        for (long i = 0; i < (long)width * height; i++)
        {
            // ×257: 0..255 → 0..65535(<<8 だと白が65280止まりで表示が1コード暗い)
            rgb8[i * 3] = (ushort)(bgra[i * 4 + 2] * 257);
            rgb8[i * 3 + 1] = (ushort)(bgra[i * 4 + 1] * 257);
            rgb8[i * 3 + 2] = (ushort)(bgra[i * 4] * 257);
        }

        ColorImage color = ColorImage.FromInterleaved(width, height, 8, rgb8);
        return new DecodedImage(color.ToLuminance(), color);
    }

    /// <summary>
    /// ファイル全体をメモリへ読み込み、転送量に応じた進捗(0〜0.9)を報告する。
    /// 2GB以上はMemoryStreamに載らないため、進捗なしのFileStreamをそのまま返す。
    /// </summary>
    private static Stream ReadToMemory(
        string path, CancellationToken cancellationToken, IProgress<double>? progress)
    {
        const double ReadShare = 0.9;  // 残り0.1はデコードの分
        var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
        if (file.Length >= int.MaxValue)
        {
            return file;
        }

        using (file)
        {
            var memory = new MemoryStream((int)file.Length);

            // 転送バッファは連番再生でフレームごとに使い捨てられるためプールから借りる
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                long total = file.Length;
                long done = 0;
                double lastReported = -1;
                int read;
                while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    memory.Write(buffer, 0, read);
                    done += read;

                    // 高速なストレージでUIスレッドへの通知が集中しないよう1%刻みに間引く
                    double p = ReadShare * done / Math.Max(1, total);
                    if (p - lastReported >= 0.01 || done == total)
                    {
                        lastReported = p;
                        progress?.Report(p);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            memory.Position = 0;
            return memory;
        }
    }
}
