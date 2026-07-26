using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawViewer.Core;

namespace RawViewer.App.Services;

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

    /// <summary>
    /// 画像ファイルを読み込む。カラー画像は輝度画像とカラー画像の両方を返す。
    /// </summary>
    /// <param name="path">ファイルパス。</param>
    /// <returns>読込結果。</returns>
    /// <exception cref="InvalidDataException">デコードできない場合。</exception>
    public static DecodedImage Load(string path)
    {
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
            var source = new ushort[(long)width * height * channels];
            frame.CopyPixels(source, width * channels * 2, 0);
            var rgb = new ushort[(long)width * height * 3];
            for (long i = 0; i < (long)width * height; i++)
            {
                rgb[i * 3] = source[i * channels];
                rgb[i * 3 + 1] = source[i * channels + 1];
                rgb[i * 3 + 2] = source[i * channels + 2];
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
