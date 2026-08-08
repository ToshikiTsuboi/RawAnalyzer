using System.Windows.Media;
using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

public class ImageFileLoaderLimitTests
{
    [Fact]
    public void EstimateBytesPerPixel_ColorFormatsCostMoreThanGray()
    {
        // 画素数だけで制限すると形式差(最大16倍)を見落とす
        Assert.Equal(2, ImageFileLoader.EstimateBytesPerPixel(PixelFormats.Gray16));
        Assert.Equal(3, ImageFileLoader.EstimateBytesPerPixel(PixelFormats.Gray8));
        Assert.Equal(8, ImageFileLoader.EstimateBytesPerPixel(PixelFormats.Rgb48));
        Assert.Equal(16, ImageFileLoader.EstimateBytesPerPixel(PixelFormats.Rgba64));
        Assert.Equal(12, ImageFileLoader.EstimateBytesPerPixel(PixelFormats.Bgra32));
    }

    [Fact]
    public void EnsureDecodable_LargeGrayscale_IsAllowed()
    {
        // 2億画素のGray16は約400MB。画素数上限内かつバイト数上限内
        ImageFileLoader.EnsureDecodable(20000, 10000, PixelFormats.Gray16);
    }

    [Fact]
    public void EnsureDecodable_ColorWithinPixelLimit_IsRejectedByByteLimit()
    {
        // 同じ2億画素でもRGBA64は約3.2GB。画素数だけの判定では素通りしていた
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => ImageFileLoader.EnsureDecodable(20000, 10000, PixelFormats.Rgba64));

        Assert.Contains("GB", ex.Message);
    }

    [Fact]
    public void EnsureDecodable_OverPixelLimit_IsRejected()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => ImageFileLoader.EnsureDecodable(24800, 16896, PixelFormats.Gray16));

        Assert.Contains("M画素", ex.Message);
    }

    [Fact]
    public void EnsureDecodable_ByteLimitMatchesEstimate()
    {
        // 上限のちょうど内側/外側で判定が切り替わること
        const int width = 10000;
        int bytesPerPixel = ImageFileLoader.EstimateBytesPerPixel(PixelFormats.Bgra32);
        int heightInside = (int)(ImageFileLoader.MaxDecodedBytes / bytesPerPixel / width);

        ImageFileLoader.EnsureDecodable(width, heightInside, PixelFormats.Bgra32);
        Assert.Throws<NotSupportedException>(
            () => ImageFileLoader.EnsureDecodable(width, heightInside + 1, PixelFormats.Bgra32));
    }
}
