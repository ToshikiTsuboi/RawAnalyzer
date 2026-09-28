using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 保存(TIFF/PNG/JPEG)の書き出し経路の検証。
/// </summary>
public class ImageFileSaverTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(8)]
    public void Tiff16_ColorImage_StreamingWriterReloadsSameRgbAsWic(int bitDepth)
    {
        // Codexレビュー(2026-09-29)#7。1億画素を超える画像の TIFF は WIC を使わず自前ライタで行単位に書くが、
        // その経路は輝度画像を Gray16 で書いており、読み込んだ RGB 画像(JPEG/PNG/カラーTIFF)の色成分が
        // 画像の大きさだけで失われていた。巨大画像を作らずに境目を下げて自前ライタの経路を通し、1億画素以下の
        // WIC 経路と同じ RGB48(8bit 由来の画像も読込時の v×257 の16bit値のまま)で読み戻せることを確かめる
        const int width = 7;
        const int height = 5;
        ColorImage color = MakeColorImage(width, height, bitDepth);
        using RawImage luminance = color.ToLuminance();

        string wicPath = TempPath();
        string streamingPath = TempPath();
        try
        {
            SaveTiff16(luminance, color, wicPath, ImageFileSaver.StreamingPixelThreshold);
            SaveTiff16(luminance, color, streamingPath, streamingPixelThreshold: 0);

            DecodedImage wic = ImageFileLoader.Load(wicPath);
            DecodedImage streaming = ImageFileLoader.Load(streamingPath);
            using RawImage wicLuminance = wic.Luminance;
            using RawImage streamingLuminance = streaming.Luminance;
            Assert.NotNull(wic.Color);
            Assert.NotNull(streaming.Color);
            Assert.Equal(16, wic.Color.BitDepth);
            Assert.Equal(16, streaming.Color.BitDepth);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    color.GetPixel(x, y, out ushort r, out ushort g, out ushort b);
                    wic.Color.GetPixel(x, y, out ushort wicR, out ushort wicG, out ushort wicB);
                    streaming.Color.GetPixel(x, y, out ushort streamR, out ushort streamG, out ushort streamB);
                    Assert.Equal((r, g, b), (wicR, wicG, wicB));
                    Assert.Equal((r, g, b), (streamR, streamG, streamB));
                    Assert.Equal(wicLuminance.GetPixel(x, y), streamingLuminance.GetPixel(x, y));
                }
            }
        }
        finally
        {
            File.Delete(wicPath);
            File.Delete(streamingPath);
        }
    }

    private static void SaveTiff16(
        RawImage luminance, ColorImage color, string path, long streamingPixelThreshold)
    {
        ImageFileSaver.Save(
            luminance, 0, path, SaveFormat.Tiff16, ViewportDisplayMode.Raw, BayerPattern.None,
            DisplayLut.Create(new DisplayParameters()), DevelopLuts.Create(new DevelopParameters()),
            color, new Progress<double>(), CancellationToken.None, streamingPixelThreshold);
    }

    /// <summary>
    /// チャネルごとに値の異なるカラー画像。8bit は WIC の読込と同じく v×257 の16bit値で持つ。
    /// </summary>
    private static ColorImage MakeColorImage(int width, int height, int bitDepth)
    {
        var rgb = new ushort[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            if (bitDepth == 8)
            {
                rgb[i * 3] = (ushort)(((i * 37) & 0xFF) * 257);
                rgb[(i * 3) + 1] = (ushort)((255 - ((i * 11) & 0xFF)) * 257);
                rgb[(i * 3) + 2] = (ushort)((((i * 101) + 7) & 0xFF) * 257);
            }
            else
            {
                rgb[i * 3] = (ushort)(1000 + (i * 997));
                rgb[(i * 3) + 1] = (ushort)(60000 - (i * 1409));
                rgb[(i * 3) + 2] = (ushort)((i * 7919) & 0xFFFF);
            }
        }

        return ColorImage.FromInterleaved(width, height, bitDepth, rgb);
    }

    private static string TempPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tif");
    }
}
