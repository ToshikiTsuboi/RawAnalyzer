using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 読み込んだRGB画像を保存したときに色が残ることを、実ファイルで確認する。
/// </summary>
/// <remarks>
/// MainWindow.SaveWithWic と同じ組み立て(BitmapSource + エンコーダ)を再現する。
/// 以前は輝度化した画像しか渡しておらず、カラーで開いてもグレーで保存されていた。
/// </remarks>
public class ColorSaveRoundTripTests
{
    private const int Width = 8;
    private const int Height = 6;

    private static ColorImage MakeColorImage()
    {
        var interleaved = new ushort[Width * Height * 3];
        for (int i = 0; i < Width * Height; i++)
        {
            interleaved[i * 3] = 10000;
            interleaved[(i * 3) + 1] = 30000;
            interleaved[(i * 3) + 2] = 60000;
        }

        return ColorImage.FromInterleaved(Width, Height, 16, interleaved);
    }

    private static string TempPath(string extension)
    {
        string dir = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + extension);
    }

    [Fact]
    public void Png16_ColorImage_RoundTripsAsRgb48()
    {
        ColorImage color = MakeColorImage();
        ushort[] rgb48 = ImageExport.RenderColorRgb48(color);
        BitmapSource source = BitmapSource.Create(
            Width, Height, 96, 96, PixelFormats.Rgb48, null, rgb48, Width * 6);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        string path = TempPath(".png");
        try
        {
            AtomicFileWriter.Write(path, encoder.Save);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            BitmapFrame decoded = BitmapDecoder.Create(
                stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];

            Assert.Equal(Width, decoded.PixelWidth);
            Assert.Equal(PixelFormats.Rgb48, decoded.Format);

            var readBack = new ushort[Width * Height * 3];
            decoded.CopyPixels(readBack, Width * 6, 0);
            Assert.Equal(10000, readBack[0]);
            Assert.Equal(30000, readBack[1]);
            Assert.Equal(60000, readBack[2]);
        }
        finally
        {
            AtomicFileWriter.TryDelete(path);
        }
    }

    [Fact]
    public void Png8_ColorImage_RoundTripsAsRgb24()
    {
        ColorImage color = MakeColorImage();
        var lut = DisplayLut.Create(new DisplayParameters());
        byte[] rgb = ImageExport.RenderColorRgb24(color, lut);
        BitmapSource source = BitmapSource.Create(
            Width, Height, 96, 96, PixelFormats.Rgb24, null, rgb, Width * 3);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        string path = TempPath(".png");
        try
        {
            AtomicFileWriter.Write(path, encoder.Save);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            BitmapFrame decoded = BitmapDecoder.Create(
                stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];

            var readBack = new byte[Width * Height * 3];
            decoded.CopyPixels(readBack, Width * 3, 0);

            // デコーダがRGB順で返すかBGR順で返すかは環境依存なので、
            // 「3成分の値が書き出した通りに揃っているか」で判定する。
            // グレーで保存されていれば3成分が同値になり、この判定で落ちる
            byte[] expected = { rgb[0], rgb[1], rgb[2] };
            byte[] actual = { readBack[0], readBack[1], readBack[2] };
            Array.Sort(expected);
            Array.Sort(actual);
            Assert.Equal(expected, actual);
            Assert.True(
                actual[0] != actual[2],
                $"色が失われている(3成分が同値): {actual[0]},{actual[1]},{actual[2]}");
        }
        finally
        {
            AtomicFileWriter.TryDelete(path);
        }
    }

    [Fact]
    public void AtomicWrite_FailureKeepsExistingFile()
    {
        // 保存が途中で失敗しても、上書き対象だった既存ファイルは残る
        string path = TempPath(".png");
        File.WriteAllText(path, "existing");
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => AtomicFileWriter.Write(
                    path, _ => throw new InvalidOperationException("エンコード失敗")));

            Assert.Equal("existing", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".part" + Environment.CurrentManagedThreadId));
        }
        finally
        {
            AtomicFileWriter.TryDelete(path);
        }
    }
}
