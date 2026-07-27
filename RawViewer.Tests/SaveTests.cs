using RawViewer.Core;
using Xunit;

namespace RawViewer.Tests;

public class SaveTests
{
    private static RawImage LoadImage(
        ushort[] codes, int width, int height, int bitDepth = 12, bool memoryMapped = false)
    {
        var format = new RawFormat { Width = width, Height = height, BitDepth = bitDepth };
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(codes, format));
        try
        {
            long threshold = memoryMapped ? 0 : RawLoader.DefaultInMemoryPixelThreshold;
            return RawLoader.Load(path, format, threshold);
        }
        finally
        {
            // MMF読み出し中は元ファイルを削除できないため、その場合は残す
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string TempPath(string extension)
    {
        string dir = Path.Combine(Path.GetTempPath(), "RawViewerTests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + extension);
    }

    public static IEnumerable<object[]> SaveCombinations()
    {
        foreach (BitPacking packing in new[] { BitPacking.Lsb, BitPacking.Msb })
        {
            foreach (Endianness endian in new[] { Endianness.Little, Endianness.Big })
            {
                foreach (bool memoryMapped in new[] { false, true })
                {
                    yield return new object[] { packing, endian, memoryMapped };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(SaveCombinations))]
    public void RawSaver_SaveAndReload_RoundTripsAllPixels(
        BitPacking packing, Endianness endianness, bool memoryMapped)
    {
        const int width = 16;
        const int height = 8;
        ushort[] codes = TestData.MakePattern(width * height, 12);
        using RawImage image = LoadImage(codes, width, height, 12, memoryMapped);

        string path = TempPath(".raw");
        try
        {
            RawSaver.Save(image, path, packing, endianness);

            var reloadFormat = new RawFormat
            {
                Width = width,
                Height = height,
                BitDepth = 12,
                Packing = packing,
                Endianness = endianness,
            };
            using RawImage reloaded = RawLoader.Load(path, reloadFormat);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal(image.GetPixel(x, y), reloaded.GetPixel(x, y));
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RawSaver_MultiFrame_RoundTrips()
    {
        const int width = 6;
        const int height = 4;
        const int frames = 2;
        ushort[] codes = TestData.MakePattern(width * height * frames, 10);
        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 10, FrameCount = frames,
        };
        string sourcePath = TestData.WriteTempFile(TestData.EncodeRawFile(codes, format));
        string savedPath = TempPath(".raw");
        try
        {
            using RawImage image = RawLoader.Load(sourcePath, format);
            RawSaver.Save(image, savedPath, BitPacking.Lsb, Endianness.Little);

            using RawImage reloaded = RawLoader.Load(savedPath, format);
            for (int f = 0; f < frames; f++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        Assert.Equal(image.GetPixel(x, y, f), reloaded.GetPixel(x, y, f));
                    }
                }
            }
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(savedPath);
        }
    }

    [Fact]
    public void RawSaver_ReportsProgressAndCompletes()
    {
        ushort[] codes = TestData.MakePattern(8 * 300, 12);
        using RawImage image = LoadImage(codes, 8, 300);
        string path = TempPath(".raw");
        try
        {
            double last = 0;
            var progress = new SynchronousProgress(p => last = p);
            RawSaver.Save(image, path, BitPacking.Lsb, Endianness.Little, progress);
            Assert.Equal(1.0, last, 10);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RawSaver_Canceled_DeletesPartialFile()
    {
        ushort[] codes = TestData.MakePattern(8 * 8, 12);
        using RawImage image = LoadImage(codes, 8, 8);
        string path = TempPath(".raw");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            RawSaver.Save(image, path, BitPacking.Lsb, Endianness.Little,
                cancellationToken: cts.Token));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public void TiffWriter_SaveAndReload_RoundTrips(int? rowsPerStrip)
    {
        const int width = 12;
        const int height = 7;
        ushort[] codes = TestData.MakePattern(width * height, 16);
        using RawImage image = LoadImage(codes, width, height, 16);

        string path = TempPath(".tif");
        try
        {
            TiffWriter.SaveGray16(image, 0, path, rowsPerStripOverride: rowsPerStrip);

            using RawImage reloaded = TiffLoader.Load(path);
            Assert.Equal(width, reloaded.Width);
            Assert.Equal(height, reloaded.Height);
            Assert.Equal(16, reloaded.Format.BitDepth);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal(image.GetPixel(x, y), reloaded.GetPixel(x, y));
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TiffWriter_SaveSecondFrame_WritesThatFrame()
    {
        // App の保存経路が表示中フレームを渡せるようにした際の土台。
        // 従来はフレーム0固定で、フレーム間差分が小さい素材では誤りに気付けなかった。
        const int width = 6;
        const int height = 4;
        const int frames = 3;
        var codes = new ushort[width * height * frames];
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < width * height; i++)
            {
                codes[f * width * height + i] = (ushort)(1000 * (f + 1) + i);
            }
        }

        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16, FrameCount = frames,
        };
        string source = TestData.WriteTempFile(TestData.EncodeRawFile(codes, format));
        string path = TempPath(".tif");
        try
        {
            using RawImage image = RawLoader.Load(source, format);
            TiffWriter.SaveGray16(image, 1, path);

            using RawImage reloaded = TiffLoader.Load(path);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal((ushort)(2000 + y * width + x), reloaded.GetPixel(x, y));
                }
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(source);
        }
    }

    [Fact]
    public void ImageExport_RenderGray8_AppliesLut()
    {
        ushort[] codes = TestData.MakePattern(8 * 4, 16);
        using RawImage image = LoadImage(codes, 8, 4, 16);
        var lut = DisplayLut.Create(new DisplayParameters());

        byte[] gray = ImageExport.RenderGray8(image, 0, lut);

        Assert.Equal(8 * 4, gray.Length);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                Assert.Equal(lut.Map(image.GetPixel(x, y)), gray[y * 8 + x]);
            }
        }
    }

    [Fact]
    public void ImageExport_DevelopRgb24_ConstantMosaic_ProducesUniformColor()
    {
        const int size = 16;
        ushort[] mosaic = ColorPipelineTests.BuildConstantMosaic(
            size, size, BayerPattern.Rggb, 10000, 20000, 30000);
        using RawImage image = LoadImage(mosaic, size, size, 16);
        var luts = DevelopLuts.Create(new DevelopParameters(Gamma: 1.0));

        byte[] rgb = ImageExport.DevelopRgb24(image, 0, BayerPattern.Rggb, luts);

        byte expectedR = luts.R[10000];
        byte expectedG = luts.G[20000];
        byte expectedB = luts.B[30000];
        for (int i = 0; i < size * size; i++)
        {
            Assert.Equal(expectedR, rgb[i * 3]);
            Assert.Equal(expectedG, rgb[i * 3 + 1]);
            Assert.Equal(expectedB, rgb[i * 3 + 2]);
        }
    }

    /// <summary>テスト用: コールバックを同期実行するIProgress。</summary>
    private sealed class SynchronousProgress : IProgress<double>
    {
        private readonly Action<double> _handler;

        public SynchronousProgress(Action<double> handler)
        {
            _handler = handler;
        }

        public void Report(double value)
        {
            _handler(value);
        }
    }
}
