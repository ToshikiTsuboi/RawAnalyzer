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
    /// 画像ファイルを読み込む。カラー画像は輝度画像とカラー画像の両方を返す。
    /// </summary>
    /// <param name="path">ファイルパス。</param>
    /// <returns>読込結果。</returns>
    /// <exception cref="InvalidDataException">デコードできない場合。</exception>
    /// <exception cref="NotSupportedException">画素数が上限を超える場合。</exception>
    public static DecodedImage Load(string path)
    {
        // 非圧縮グレースケールTIFFは画素データが連続しているので、WICでデコードせず
        // rawと同じ経路(必要ならMemoryMappedFile)で開く。画素数の上限に縛られない
        if (TiffLoader.TryProbePixelLayout(path, out TiffPixelLayout? layout, out string reason))
        {
            RawFormat tiffFormat = TiffLoader.ToRawFormat(layout!);
            AppLog.Info(
                $"TIFFを直接読み出し: {tiffFormat.Width}×{tiffFormat.Height} " +
                $"{tiffFormat.BitDepth}bit (オフセット {tiffFormat.HeaderOffset})");
            return new DecodedImage(RawLoader.Load(path, tiffFormat), null);
        }

        if (IsTiff(path) && reason.Length > 0)
        {
            AppLog.Info($"TIFFの直接読み出しは不可のためWICで開きます: {reason}");
        }

        BitmapFrame frame;
        try
        {
            var decoder = BitmapDecoder.Create(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException
            or ArgumentException)
        {
            throw new InvalidDataException($"画像をデコードできません: {ex.Message}", ex);
        }

        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        PixelFormat format = frame.Format;

        // BitmapCacheOption.OnLoad で WIC 側も全画素を展開するため、
        // rawのようなMMF退避ができない。上限を超えるものは明示的に拒否する
        long pixelCount = (long)width * height;
        if (pixelCount > MaxPixels)
        {
            throw new NotSupportedException(
                $"{width}×{height} ({pixelCount / 1_000_000.0:F0}M画素) は" +
                $"デコード画像の上限 {MaxPixels / 1_000_000} M画素を超えています。");
        }

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
                pixels[i] = (ushort)(bytes[i] << 8);
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
            rgb8[i * 3] = (ushort)(bgra[i * 4 + 2] << 8);
            rgb8[i * 3 + 1] = (ushort)(bgra[i * 4 + 1] << 8);
            rgb8[i * 3 + 2] = (ushort)(bgra[i * 4] << 8);
        }

        ColorImage color = ColorImage.FromInterleaved(width, height, 8, rgb8);
        return new DecodedImage(color.ToLuminance(), color);
    }
}
