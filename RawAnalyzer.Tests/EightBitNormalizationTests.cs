using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// WIC で読む 8bit 画像(PNG・JPEG・クラシック TIFF・1/2/4bit グレー)も、raw・自前復号の TIFF と同じく
/// code&lt;&lt;8 で 16bit へ置くこと(検証の指摘 r2conv-wic8-1)。
/// </summary>
public class EightBitNormalizationTests
{
    private const int Width = 16;
    private const int Height = 16;

    [Fact]
    public void WicGrayPng_IsNormalizedLikeRaw()
    {
        byte[] values = Ramp(seed: 0);
        string path = WritePng(values, PixelFormats.Gray8, Width);
        try
        {
            using RawImage image = ImageFileLoader.Load(path).Luminance;
            Assert.Equal(8, image.Format.BitDepth);
            for (int i = 0; i < values.Length; i++)
            {
                Assert.Equal(values[i] << 8, image.GetPixel(i % Width, i / Width));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WicColorPng_IsNormalizedLikeRaw()
    {
        var bgr = new byte[Width * Height * 3];
        for (int i = 0; i < bgr.Length; i++)
        {
            bgr[i] = (byte)((i * 37) & 0xFF);
        }

        string path = WritePng(bgr, PixelFormats.Bgr24, Width * 3);
        try
        {
            DecodedImage decoded = ImageFileLoader.Load(path);
            using RawImage luminance = decoded.Luminance;
            ColorImage color = decoded.Color!;
            Assert.Equal(8, color.BitDepth);
            color.GetPixel(3, 2, out ushort r, out ushort g, out ushort b);
            int index = ((2 * Width) + 3) * 3;
            Assert.Equal((bgr[index + 2] << 8, bgr[index + 1] << 8, bgr[index] << 8), (r, g, b));

            // 輝度も同じ置き方(下位8bitは0)
            Assert.Equal(0, luminance.GetPixel(3, 2) & 0xFF);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SubtractWicDarkFromRaw_GivesCodeDifference_AndBinningMatchesRaw()
    {
        // 8bit raw(<<8)から 8bit PNG のダークを引くと、code の差 a−b になる(以前は PNG が v×257 で、
        // b≥1 の全画素で a−b−1 になっていた)。同じ 8bit データの平均ビニングも読み込み経路によらず同じ
        byte[] a = Ramp(seed: 100);
        byte[] b = Ramp(seed: 3);
        string path = WritePng(b, PixelFormats.Gray8, Width);
        try
        {
            using RawImage raw = TestImages.FromCodes(a.Select(v => (ushort)v).ToArray(), Width, Height, bitDepth: 8);
            using RawImage dark = ImageFileLoader.Load(path).Luminance;
            using RawImage darkRaw = TestImages.FromCodes(b.Select(v => (ushort)v).ToArray(), Width, Height, bitDepth: 8);

            using RawImage difference = ImageCalculator.Apply(raw, dark, ImageOperation.Subtract);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.Equal(Math.Max(0, a[i] - b[i]), difference.GetPixel(i % Width, i / Width) >> 8);
            }

            using RawImage binnedPng = ImageBinning.Apply(dark, 2);
            using RawImage binnedRaw = ImageBinning.Apply(darkRaw, 2);
            for (int y = 0; y < binnedRaw.Height; y++)
            {
                for (int x = 0; x < binnedRaw.Width; x++)
                {
                    Assert.Equal(binnedRaw.GetPixel(x, y), binnedPng.GetPixel(x, y));
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Ramp(int seed)
    {
        var values = new byte[Width * Height];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (byte)((i + seed) % 256);
        }

        values[^1] = 255;
        return values;
    }

    private static string WritePng(byte[] pixels, PixelFormat format, int stride)
    {
        string directory = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
        BitmapSource source = BitmapSource.Create(Width, Height, 96, 96, format, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (FileStream stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        return path;
    }
}
