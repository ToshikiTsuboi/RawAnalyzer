using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class ImageProcessingTests
{
    // Bayer 4種は同じ2画素刻みの経路に潰れるので None と Rggb だけ、
    // 半径は最小窓(1)と全面クランプ(4)だけ、Sobel は半径固定なので1行。
    public static IEnumerable<object[]> FilterCases()
    {
        foreach (BayerPattern pattern in new[] { BayerPattern.None, BayerPattern.Rggb })
        {
            foreach (ImageFilterKind kind in Enum.GetValues<ImageFilterKind>())
            {
                foreach (int radius in kind == ImageFilterKind.Sobel ? new[] { 1 } : new[] { 1, 4 })
                {
                    yield return new object[] { pattern, kind, radius };
                }
            }
        }
    }

    private static RawImage Raw(int width, int height, Func<int, int, ushort> pixel,
        BayerPattern pattern = BayerPattern.None)
    {
        var pixels = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[y * width + x] = pixel(x, y);
            }
        }

        return RawImage.FromPixels(new RawFormat { Width = width, Height = height, Bayer = pattern }, pixels);
    }

    [Theory]
    [InlineData(BayerPattern.None, 2, BinningMode.Sum)]
    [InlineData(BayerPattern.Rggb, 3, BinningMode.Average)]
    [InlineData(BayerPattern.Bggr, 8, BinningMode.Average)]
    public void Binning_MatchesScalarReferenceAndKeepsCfa(BayerPattern pattern, int factor, BinningMode mode)
    {
        const int width = 37;
        const int height = 35;
        using RawImage source = Raw(width, height, (x, y) => (ushort)(1000 + x * 111 + y * 313), pattern);
        using RawImage result = ImageBinning.Apply(source, factor, mode);
        int step = pattern == BayerPattern.None ? 1 : 2;
        Assert.Equal(width / (factor * step) * step, result.Width);
        Assert.Equal(height / (factor * step) * step, result.Height);
        Assert.Equal(pattern, result.Format.Bayer);
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                long sum = 0;
                int firstX = x / step * step * factor + x % step;
                int firstY = y / step * step * factor + y % step;
                for (int dy = 0; dy < factor; dy++)
                {
                    for (int dx = 0; dx < factor; dx++)
                    {
                        sum += source.GetPixel(firstX + dx * step, firstY + dy * step);
                    }
                }

                double expected = mode == BinningMode.Average ? (double)sum / (factor * factor) : sum;
                Assert.Equal(Round(expected), result.GetPixel(x, y));
            }
        }

        Assert.Equal(1000, source.GetPixel(0, 0));
    }

    [Theory]
    [InlineData(2, BinningMode.Average)]
    [InlineData(8, BinningMode.Sum)]
    public void Binning_RgbDoesNotMixChannels(int factor, BinningMode mode)
    {
        ColorImage source = ColorImage.FromInterleaved(17, 19, 16,
            Enumerable.Range(0, 17 * 19).SelectMany(_ => new ushort[] { 101, 1001, 9001 }).ToArray());
        ColorImage result = ImageBinning.Apply(source, factor, mode);
        Assert.Equal(17 / factor, result.Width);
        Assert.Equal(19 / factor, result.Height);
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                result.GetPixel(x, y, out ushort r, out ushort g, out ushort b);
                int scale = mode == BinningMode.Sum ? factor * factor : 1;
                Assert.Equal(Round(101 * scale), r);
                Assert.Equal(Round(1001 * scale), g);
                Assert.Equal(Round(9001 * scale), b);
            }
        }
    }

    [Fact]
    public void Binning_SaturatesWithoutWrappingAndRetainsAveragePrecision()
    {
        using RawImage source = Raw(2, 2, (x, y) => (ushort)(60000 + x + y));
        using RawImage sum = ImageBinning.Apply(source, 2, BinningMode.Sum);
        using RawImage avg = ImageBinning.Apply(source, 2);
        Assert.Equal(65535, sum.GetPixel(0, 0));
        Assert.Equal(60001, avg.GetPixel(0, 0));
        using RawImage twelveBit = RawImage.FromPixels(new RawFormat
        {
            Width = 2, Height = 2, BitDepth = 12, HeaderOffset = 512,
            FrameCount = 2, Hdr = HdrMode.FrameSequential,
        }, new ushort[] { 0, 0, 0, 0, 0, 16, 0, 16 });
        using RawImage selectedFrame = ImageBinning.Apply(twelveBit, 2, frame: 1);
        Assert.Equal(8, selectedFrame.GetPixel(0, 0));
        Assert.Equal(16, selectedFrame.Format.BitDepth);
        Assert.Equal(1, selectedFrame.FrameCount);
        Assert.Equal(0, selectedFrame.Format.HeaderOffset);
        Assert.Equal(HdrMode.None, selectedFrame.Format.Hdr);
    }

    [Theory]
    [MemberData(nameof(FilterCases))]
    public void Filters_MatchIndependentTwoDimensionalReference(
        BayerPattern pattern, ImageFilterKind kind, int radius)
    {
        // 奇数寸法、窓より小さい同色プレーン、端の複製をすべて通す。
        using RawImage source = Raw(9, 7, (x, y) => (ushort)((x * 9173 + y * 1297 + 881) % 65536), pattern);
        var options = new ImageFilterOptions(kind, radius, Sigma: 1.3, Amount: 1.7);
        using RawImage result = ImageFilters.Apply(source, options);
        int step = pattern == BayerPattern.None ? 1 : 2;
        Assert.Equal(pattern, result.Format.Bayer);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                ushort expected = ReferenceFilter(source, x, y, step, options);
                Assert.InRange(Math.Abs(result.GetPixel(x, y) - expected), 0, 1);
            }
        }
    }

    [Theory]
    [InlineData(ImageFilterKind.Gaussian)]
    [InlineData(ImageFilterKind.Median)]
    [InlineData(ImageFilterKind.UnsharpMask)]
    [InlineData(ImageFilterKind.Sobel)]
    [InlineData(ImageFilterKind.Minimum)]
    [InlineData(ImageFilterKind.Maximum)]
    public void Filters_ConstantCfaPlanesNeverMixIncludingOddEdges(ImageFilterKind kind)
    {
        ushort[] levels = { 100, 1000, 9000, 50000 };
        foreach (BayerPattern pattern in Enum.GetValues<BayerPattern>().Where(p => p != BayerPattern.None))
        {
            using RawImage source = Raw(15, 11, (x, y) => levels[(y % 2) * 2 + x % 2], pattern);
            using RawImage result = ImageFilters.Apply(source, new(kind, Radius: 3));
            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    Assert.Equal(kind == ImageFilterKind.Sobel ? 0 : source.GetPixel(x, y), result.GetPixel(x, y));
                }
            }
        }
    }

    [Theory]
    [InlineData(ImageFilterKind.UnsharpMask)]
    [InlineData(ImageFilterKind.Median)]
    [InlineData(ImageFilterKind.Sobel)]
    public void Filters_RgbMatchesThreeSeparateMonochromeImages(ImageFilterKind kind)
    {
        const int width = 13;
        const int height = 12;
        var rgb = new ushort[width * height * 3];
        for (int i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (ushort)((i * 1973 + 733) % 65536);
        }

        ColorImage source = ColorImage.FromInterleaved(width, height, 16, rgb);
        var options = new ImageFilterOptions(kind, Radius: 2);
        ColorImage result = ImageFilters.Apply(source, options);
        for (int c = 0; c < 3; c++)
        {
            using RawImage channel = Raw(width, height, (x, y) => rgb[(y * width + x) * 3 + c]);
            using RawImage expected = ImageFilters.Apply(channel, options);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    result.GetPixel(x, y, out ushort r, out ushort g, out ushort b);
                    Assert.Equal(expected.GetPixel(x, y), c == 0 ? r : c == 1 ? g : b);
                }
            }
        }

        // 入力を書き換えない。source は rgb を写さずに包むので、rgb ではなく元の値の定数と比べる
        source.GetPixel(0, 0, out ushort originalR, out _, out _);
        Assert.Equal((ushort)733, originalR); // (0 × 1973 + 733) % 65536
    }

    [Fact]
    public void Filters_SelectFrameAndHonorPatternOverride()
    {
        using RawImage source = RawImage.FromPixels(new RawFormat { Width = 4, Height = 4, FrameCount = 2 },
            Enumerable.Repeat((ushort)0, 16).Concat(Enumerable.Range(0, 16)
                .Select(i => (ushort)((i % 4 % 2) + (i / 4 % 2) * 100))).ToArray());
        using RawImage result = ImageFilters.Apply(source, new(ImageFilterKind.Mean), 1, BayerPattern.Gbrg);
        Assert.Equal(BayerPattern.Gbrg, result.Format.Bayer);
        Assert.Equal(101, result.GetPixel(3, 3));
        Assert.Equal(1, result.FrameCount);
        using RawImage binned = ImageBinning.Apply(source, 2, frame: 1, pattern: BayerPattern.Gbrg);
        Assert.Equal(101, binned.GetPixel(1, 1));
    }

    [Fact]
    public void SinglePixel_AllFiltersHaveDefinedBorders()
    {
        using RawImage source = Raw(1, 1, (_, _) => 23456, BayerPattern.Rggb);
        foreach (ImageFilterKind kind in Enum.GetValues<ImageFilterKind>())
        {
            using RawImage result = ImageFilters.Apply(source, new(kind, Radius: 4));
            Assert.Equal(kind == ImageFilterKind.Sobel ? 0 : 23456, result.GetPixel(0, 0));
        }
    }

    [Fact]
    public void InvalidParametersAndPreCancelledToken_Throw()
    {
        using RawImage source = Raw(8, 8, (_, _) => 500);
        foreach (int factor in new[] { 1, 17 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ImageBinning.Apply(source, factor));
        }

        Assert.Throws<ArgumentException>(() => ImageBinning.GetDimensions(3, 4, 2, BayerPattern.Rggb));
        Assert.Equal((4, 2, 3, 3), ImageBinning.GetDimensions(19, 11, 4, BayerPattern.Rggb));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageBinning.Apply(source, 2, (BinningMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageBinning.Apply(source, 2, frame: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageFilters.Apply(source, new((ImageFilterKind)99)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageFilters.Apply(source, new(ImageFilterKind.Mean), frame: -1));
        foreach (var options in new[]
        {
            new ImageFilterOptions(ImageFilterKind.Mean, Radius: 0),
            new ImageFilterOptions(ImageFilterKind.Median, Radius: 1000),
            new ImageFilterOptions(ImageFilterKind.Gaussian, Sigma: double.NaN),
            new ImageFilterOptions(ImageFilterKind.Gaussian, Sigma: 0),
            new ImageFilterOptions(ImageFilterKind.UnsharpMask, Amount: double.NaN),
            new ImageFilterOptions(ImageFilterKind.UnsharpMask, Amount: -1),
        })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ImageFilters.Apply(source, options));
        }

        // 強度の下限 0 は受け付け(ダイアログでも入力できる)、アンシャープの結果は元の画像のまま
        using RawImage impulse = Raw(9, 9, (x, y) => x == 4 && y == 4 ? (ushort)60000 : (ushort)1000);
        using RawImage unsharpZero = ImageFilters.Apply(impulse, new(ImageFilterKind.UnsharpMask, Amount: 0));
        Assert.Equal(60000, unsharpZero.GetPixel(4, 4));
        Assert.Equal(1000, unsharpZero.GetPixel(3, 4));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => ImageBinning.Apply(source, 2, cancellationToken: cts.Token));
        Assert.Throws<OperationCanceledException>(() => ImageFilters.Apply(source, new(ImageFilterKind.Median), cancellationToken: cts.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationDuringProgress_DoesNotReturnPartialImage(bool binning)
    {
        using RawImage source = Raw(256, 256, (x, y) => (ushort)(x * y));
        using var cts = new CancellationTokenSource();
        var progress = new CancelProgress(cts);
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            using RawImage result = binning
                ? ImageBinning.Apply(source, 2, progress: progress, cancellationToken: cts.Token)
                : ImageFilters.Apply(source, new(ImageFilterKind.Gaussian), progress: progress, cancellationToken: cts.Token);
        });
        Assert.Equal(16, source.GetPixel(4, 4));
    }

    [Fact]
    public void MemoryMappedInput_BinningAndFilterResultsCanBeSavedAndReopened()
    {
        var format = new RawFormat { Width = 20, Height = 16, BitDepth = 12, Bayer = BayerPattern.Bggr };
        string input = TestData.WriteTempFile(TestData.EncodeRawFile(TestData.MakePattern(320, 12), format));
        string output = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".raw");
        try
        {
            using RawImage source = RawLoader.Load(input, format, inMemoryPixelThreshold: 0);
            Assert.True(source.IsMemoryMapped);
            using RawImage binned = ImageBinning.Apply(source, 2);
            using RawImage filtered = ImageFilters.Apply(binned, new(ImageFilterKind.Gaussian));
            RawSaver.Save(filtered, output, BitPacking.Lsb, Endianness.Little);
            using RawImage restored = RawLoader.Load(output, filtered.Format);
            Assert.Equal(10, restored.Width);
            Assert.Equal(8, restored.Height);
            for (int y = 0; y < restored.Height; y++)
            {
                for (int x = 0; x < restored.Width; x++)
                {
                    Assert.Equal(filtered.GetPixel(x, y), restored.GetPixel(x, y));
                }
            }
        }
        finally
        {
            AtomicFileWriter.TryDelete(input);
            AtomicFileWriter.TryDelete(output);
        }
    }

    private sealed class CancelProgress(CancellationTokenSource cts) : IProgress<double>
    {
        public void Report(double value) => cts.Cancel();
    }

    private static ushort Round(double value) => (ushort)Math.Clamp(Math.Floor(value + 0.5), 0, 65535);

    private static ushort ReferenceFilter(RawImage source, int x, int y, int step, ImageFilterOptions options)
    {
        int radius = options.Kind == ImageFilterKind.Sobel ? 1 : options.Radius;
        var samples = new List<ushort>();
        double weighted = 0;
        double weightSum = 0;
        double gx = 0;
        double gy = 0;
        int[] derivative = { -1, 0, 1 };
        int[] smoothing = { 1, 2, 1 };
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int sx = x + dx * step;
                int sy = y + dy * step;
                // 独立な境界処理: 範囲外座標を同位相の端へ寄せる。
                while (sx < 0) { sx += step; }
                while (sx >= source.Width) { sx -= step; }
                while (sy < 0) { sy += step; }
                while (sy >= source.Height) { sy -= step; }
                ushort v = source.GetPixel(sx, sy);
                samples.Add(v);
                double weight = options.Kind is ImageFilterKind.Gaussian or ImageFilterKind.UnsharpMask
                    ? Math.Exp(-(dx * dx + dy * dy) / (2 * options.Sigma * options.Sigma)) : 1;
                weighted += v * weight;
                weightSum += weight;
                if (options.Kind == ImageFilterKind.Sobel)
                {
                    gx += v * derivative[dx + 1] * smoothing[dy + 1];
                    gy += v * derivative[dy + 1] * smoothing[dx + 1];
                }
            }
        }

        samples.Sort();
        return Round(options.Kind switch
        {
            ImageFilterKind.Median => samples[samples.Count / 2],
            ImageFilterKind.Minimum => samples[0],
            ImageFilterKind.Maximum => samples[^1],
            ImageFilterKind.UnsharpMask => source.GetPixel(x, y) + options.Amount * (source.GetPixel(x, y) - weighted / weightSum),
            ImageFilterKind.Sobel => Math.Sqrt(gx * gx + gy * gy) / 4,
            _ => weighted / weightSum,
        });
    }
}
