using System.Windows.Media;
using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

public class ImageFileLoaderLimitTests
{
    private static PixelFormat ToPixelFormat(string name) => name switch
    {
        nameof(PixelFormats.Gray16) => PixelFormats.Gray16,
        nameof(PixelFormats.Gray8) => PixelFormats.Gray8,
        nameof(PixelFormats.Rgb48) => PixelFormats.Rgb48,
        nameof(PixelFormats.Rgba64) => PixelFormats.Rgba64,
        nameof(PixelFormats.Bgra32) => PixelFormats.Bgra32,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    [Theory]
    [InlineData(nameof(PixelFormats.Gray16), true)]  // 約400MB
    [InlineData(nameof(PixelFormats.Gray8), true)]   // byte[] + ushort[] で約600MB
    [InlineData(nameof(PixelFormats.Rgb48), false)]  // 約1.6GB
    [InlineData(nameof(PixelFormats.Rgba64), false)] // 約3.2GB
    [InlineData(nameof(PixelFormats.Bgra32), false)] // 変換バッファ込みで約2.4GB
    public void EnsureDecodable_AtPixelLimit_DependsOnBytesPerPixel(string formatName, bool allowed)
    {
        // 同じ2億画素(画素数上限内)でも、形式ごとの所要バイトで許可/拒否が分かれる。
        // 画素数だけの判定では RGBA64 が素通りしていた
        PixelFormat format = ToPixelFormat(formatName);
        if (allowed)
        {
            ImageFileLoader.EnsureDecodable(20000, 10000, format);
            return;
        }

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => ImageFileLoader.EnsureDecodable(20000, 10000, format));

        Assert.Contains("GB", ex.Message);
    }

    [Fact]
    public void EnsureDecodable_OverPixelLimit_IsRejected()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => ImageFileLoader.EnsureDecodable(24800, 16896, PixelFormats.Gray16));

        Assert.Contains("M画素", ex.Message);
    }
}
