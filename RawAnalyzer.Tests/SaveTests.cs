using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class SaveTests
{
    private static RawImage LoadImage(ushort[] codes, int width, int height, int bitDepth = 12)
    {
        // ヒープ展開で読むので、読み込み後に元ファイルを消せる
        var format = new RawFormat { Width = width, Height = height, BitDepth = bitDepth };
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(codes, format));
        try
        {
            return RawLoader.Load(path, format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempPath(string extension)
    {
        string dir = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + extension);
    }

    private static ColorImage MakeColorImage(int width, int height)
    {
        var interleaved = new ushort[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            interleaved[i * 3] = (ushort)(1000 + i);       // R
            interleaved[(i * 3) + 1] = (ushort)(20000 + i); // G
            interleaved[(i * 3) + 2] = (ushort)(60000 - i); // B
        }

        return ColorImage.FromInterleaved(width, height, 16, interleaved);
    }

    [Theory]
    [InlineData(BitPacking.Lsb, Endianness.Little)]
    [InlineData(BitPacking.Lsb, Endianness.Big)]
    [InlineData(BitPacking.Msb, Endianness.Little)]
    [InlineData(BitPacking.Msb, Endianness.Big)]
    public void RawSaver_SaveAndReload_RoundTripsAllPixels(BitPacking packing, Endianness endianness)
    {
        // RawSaver.EncodeRow の分岐は packing×endianness の 4 通り。
        // 元画像が MMF かヒープかは CopyRegion の裏側の話なので RawLoaderTests に任せる
        const int width = 16;
        const int height = 8;
        ushort[] codes = TestData.MakePattern(width * height, 12);
        using RawImage image = LoadImage(codes, width, height);

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
            double lastProgress = 0;
            var progress = new SynchronousProgress(p => lastProgress = p);
            RawSaver.Save(image, savedPath, BitPacking.Lsb, Endianness.Little, progress);

            // 256 行の報告周期に乗らない短い画像でも、最終行で 1.0 を報告し終えること
            Assert.Equal(1.0, lastProgress, 10);

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

    [Theory]
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

            // 本番で1億画素超の保存物を開き直す直接経路(ストリップの連続性の判定 → RawLoader)で読み戻す。
            // 小さな画像は判定が不成立でもWICで読めてしまうので、判定が成り立つことを明示する
            Assert.True(TiffLoader.TryProbePixelLayout(path, out TiffPixelLayout? layout, out string reason), reason);
            using RawImage reloaded = RawLoader.Load(path, TiffLoader.ToRawFormat(layout!));
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

            Assert.True(TiffLoader.TryProbePixelLayout(path, out TiffPixelLayout? layout, out string reason), reason);
            using RawImage reloaded = RawLoader.Load(path, TiffLoader.ToRawFormat(layout!));
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
    public void TiffWriter_SaveRgb48_MultipleStrips_ReloadsSameRgb()
    {
        // 1億画素を超えるカラー画像のTIFF保存に使う自前ライタ(Codexレビュー 2026-09-29 #7)。
        // 実際の巨大画像は約1MBごとの多数のストリップになる。RGBは BitsPerSample(16,16,16)と
        // ストリップのオフセット・バイト数の配列をどちらもIFDの外に置くので、端数のある複数ストリップでも
        // 配置がずれずに読み戻せること(16bitのRGBはWIC経由で読む)
        const int width = 12;
        const int height = 7;
        ColorImage color = MakeColorImage(width, height);
        string path = TempPath(".tif");
        try
        {
            TiffWriter.SaveRgb48(color, path, rowsPerStripOverride: 2);

            Assert.True(TiffLoader.TryReadSampleInfo(path, out TiffSampleInfo? info));
            Assert.Equal(
                (16, 3, 2, 1),
                (info!.BitsPerSample, info.SamplesPerPixel, info.Photometric, info.Compression));
            DecodedImage decoded = ImageFileLoader.Load(path);
            using RawImage luminance = decoded.Luminance;
            Assert.NotNull(decoded.Color);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    color.GetPixel(x, y, out ushort r, out ushort g, out ushort b);
                    decoded.Color.GetPixel(x, y, out ushort readR, out ushort readG, out ushort readB);
                    Assert.Equal((r, g, b), (readR, readG, readB));
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ImageExport_RenderGray8_AppliesLut()
    {
        // 8bitグレー保存と一括書き出しが使う単一LUT版。渡したLUTと frame で焼くことを、
        // 既定でないLUTと、フレームごとに値の違う2フレーム画像のフレーム1で確かめる
        const int width = 8;
        const int height = 4;
        ushort[] codes = TestData.MakePattern(width * height * 2, 16);
        using RawImage image = RawImage.FromPixels(
            new RawFormat { Width = width, Height = height, BitDepth = 16, FrameCount = 2 }, codes);
        var lut = DisplayLut.Create(new DisplayParameters(Gain: 1.5, Gamma: 2.2));

        byte[] gray = ImageExport.RenderGray8(image, 1, lut);

        Assert.Equal(width * height, gray.Length);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Assert.Equal(lut.Map(codes[(width * height) + (y * width) + x]), gray[y * width + x]);
            }
        }
    }

    [Fact]
    public void ImageExport_RenderColorRgb24_AppliesLutPerChannel()
    {
        // TIFF/PNG等のカラー連番のバッチ焼き込み経路。
        // 現像は通らず、表示LUTだけが各チャネルへ効くこと
        const int width = 4;
        const int height = 2;
        var interleaved = new ushort[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            interleaved[i * 3] = (ushort)(1000 * i);
            interleaved[i * 3 + 1] = (ushort)(2000 * i);
            interleaved[i * 3 + 2] = (ushort)(3000 * i);
        }

        ColorImage color = ColorImage.FromInterleaved(width, height, 16, interleaved);
        var lut = DisplayLut.Create(new DisplayParameters(Gain: 1.5, Gamma: 2.2));

        byte[] rgb = ImageExport.RenderColorRgb24(color, lut);

        Assert.Equal(width * height * 3, rgb.Length);
        for (int i = 0; i < interleaved.Length; i++)
        {
            Assert.Equal(lut.Map(interleaved[i]), rgb[i]);
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

    [Fact]
    public void ImageExport_DevelopRgb24_WritesIntoProvidedBuffer()
    {
        // 現像経路のバッファ再利用オーバーロードも、確保版と同じ結果になること
        var format = new RawFormat
        {
            Width = 8,
            Height = 6,
            BitDepth = 16,
            Bayer = BayerPattern.Rggb,
        };
        var codes = new ushort[format.Width * format.Height];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)((i * 5003) % 65536);
        }

        using RawImage image = RawImage.FromPixels(format, codes);
        var luts = DevelopLuts.Create(new DevelopParameters(Gamma: 1.0));

        byte[] allocated = ImageExport.DevelopRgb24(image, 0, BayerPattern.Rggb, luts);
        var reused = new byte[allocated.Length];
        ImageExport.DevelopRgb24(image, 0, BayerPattern.Rggb, luts, null, default, reused);

        Assert.Equal(allocated, reused);
    }

    [Fact]
    public void ImageExport_DevelopRgb24_SameAsDemosaicOfWholeImage_AcrossBandSeams()
    {
        // HDR分割ビューの段ごとの現像(区画ごとのデモザイク)と処理を共通にしても、分割ビューでない保存の現像結果は
        // 変えない。256行ずつの帯の継ぎ目(帯の上下1行を重ねて補間する)を含めて、画像全体を一度にデモザイクして
        // 現像LUTを当てた結果と1バイトも違わないこと
        const int width = 6;
        const int height = 300;
        var format = new RawFormat { Width = width, Height = height, BitDepth = 16, Bayer = BayerPattern.Grbg };
        var pixels = new ushort[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (ushort)(((i * 7919) + 13) % 65536);
        }

        using RawImage image = RawImage.FromPixels(format, pixels);
        var luts = DevelopLuts.Create(new DevelopParameters(
            BlackLevel: 2048, GainR: 1.7, GainB: 1.3, WhitePoint: 60000, Gain: 1.2, Contrast: 1.1));

        var rgb16 = new ushort[width * height * 3];
        ColorPipeline.DemosaicBilinear(pixels, width, height, 0, 0, BayerPattern.Grbg, rgb16);
        var expected = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            luts.Convert(rgb16[i * 3], rgb16[(i * 3) + 1], rgb16[(i * 3) + 2],
                out expected[i * 3], out expected[(i * 3) + 1], out expected[(i * 3) + 2]);
        }

        Assert.Equal(expected, ImageExport.DevelopRgb24(image, 0, BayerPattern.Grbg, luts));
    }

    [Theory]
    [InlineData(1)] // 一括書き出し・保存の現像(画像全体を1区画)
    [InlineData(2)] // HDR分割ビューの段ごとの現像(区画ごとにデモザイク)
    public void ImageExport_DevelopRgb24_CanceledDuringLastBand_ThrowsInsteadOfReturningStaleRows(int segments)
    {
        // 回帰テスト: デモザイクは取り消されると例外を出さずに途中で戻る(呼び出し側で確かめる約束)が、
        // 現像は各バンドの先頭でしか確かめていなかった。最後のバンド(1バンドの画像では全体)のデモザイク中や
        // 区画の間に取り消すと、処理されなかった行を確保直後の0や前の区画の値のままLUT変換して正常に戻り、
        // 一括書き出しが壊れた画像を正式名で保存して「完了」と表示していた。
        // 1バンド(256行以下)の幅の広い画像で、参照の所要時間の半ばに取り消す。正常に戻ってよいのは
        // 取り消しが処理の終わった後に届いたときだけで、そのときは全行が正しい
        const int width = 24000;
        const int height = 200;
        var format = new RawFormat { Width = width, Height = height, BitDepth = 16, Bayer = BayerPattern.Rggb };
        var pixels = new ushort[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (ushort)((((long)i * 7919) + 13) % 65536 | 1);
        }

        using RawImage image = RawImage.FromPixels(format, pixels);
        var luts = DevelopLuts.Create(new DevelopParameters(Gamma: 1.0));
        DevelopLuts[] segmentLuts = Enumerable.Repeat(luts, segments).ToArray();
        byte[] Develop(CancellationToken ct) => ImageExport.DevelopRgb24(
            image, 0, BayerPattern.Rggb, segmentLuts, width / segments, null, ct);

        Develop(default); // JIT などの初回の遅れを除く
        var reference = System.Diagnostics.Stopwatch.StartNew();
        byte[] expected = Develop(default);
        long halfway = reference.ElapsedTicks / 2;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var cts = new CancellationTokenSource();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var canceler = new Thread(() =>
            {
                while (clock.ElapsedTicks < halfway)
                {
                    Thread.SpinWait(20);
                }

                cts.Cancel();
            });
            canceler.Start();
            try
            {
                byte[] developed = Develop(cts.Token);
                Assert.True(
                    developed.AsSpan().SequenceEqual(expected),
                    "取り消し後に正常に戻った現像結果が、取り消さないときと違う(処理されなかった行が残っている)");
            }
            catch (OperationCanceledException)
            {
                // 期待どおり取り消しとして扱われた
            }
            finally
            {
                canceler.Join();
            }
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
