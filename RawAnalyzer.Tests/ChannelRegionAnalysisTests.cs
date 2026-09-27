using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// チャネル分割表示の1象限に対応する格子領域(<see cref="ChannelRegion"/>)の写像と解析。
/// 期待値はすべて「表示されている画素を1つずつ元画像から拾って集計した値」と比べる。
/// </summary>
public class ChannelRegionAnalysisTests
{
    [Fact]
    public void TryMapTiledRegion_MatchesPerPixelMapping()
    {
        // タイル画像 10×6(象限 5×3)上のすべての矩形について、
        // 1象限に収まるものは表示画素の写像先と格子が一致し、またぐものは写像しない
        const int tiledWidth = 10;
        const int tiledHeight = 6;
        for (int y = 0; y < tiledHeight; y++)
        {
            for (int x = 0; x < tiledWidth; x++)
            {
                for (int height = 1; y + height <= tiledHeight; height++)
                {
                    for (int width = 1; x + width <= tiledWidth; width++)
                    {
                        var tiled = new RegionOfInterest(x, y, width, height);
                        bool singleQuadrant = x / 5 == (x + width - 1) / 5
                            && y / 3 == (y + height - 1) / 3;

                        bool mapped = BayerSplit.TryMapTiledRegion(
                            tiled, tiledWidth, tiledHeight, out ChannelRegion region);

                        Assert.Equal(singleQuadrant, mapped);
                        if (mapped)
                        {
                            Assert.Equal(
                                DisplayedSourcePixels(tiled, tiledWidth, tiledHeight),
                                LatticePixels(region));
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void TryMapTiledRegion_ClampsToTiledImage()
    {
        // 奇数寸法の元画像はタイル表示で端の行・列が並ばない。表示外の部分は落とす
        bool mapped = BayerSplit.TryMapTiledRegion(
            new RegionOfInterest(3, 3, 5, 5), 4, 4, out ChannelRegion region);

        Assert.True(mapped);
        Assert.Equal(new ChannelRegion(3, 3, 1, 1), region); // 右下象限の (1,1) 要素
    }

    [Theory]
    [InlineData(4, 0, 1, 4)]   // タイル画像の右外
    [InlineData(0, 4, 4, 1)]   // タイル画像の下外
    [InlineData(-3, 0, 2, 2)]  // 左外
    public void TryMapTiledRegion_OutsideTiledImage_ReturnsFalse(int x, int y, int width, int height)
    {
        Assert.False(BayerSplit.TryMapTiledRegion(
            new RegionOfInterest(x, y, width, height), 4, 4, out _));
    }

    [Fact]
    public void ComputeHistogram_MatchesPixelsPickedOneByOne()
    {
        using RawImage image = RandomImage(12, 10, bitDepth: 12, seed: 1);
        var region = new ChannelRegion(1, 2, 5, 4);

        HistogramResult result = ChannelRegionAnalysis.ComputeHistogram(image, 0, region);

        int[] codes = Codes(image, 0, region);
        var expectedBins = new long[4096];
        foreach (int code in codes)
        {
            expectedBins[code]++;
        }

        Assert.Equal(expectedBins, result.Bins);
        Assert.Equal(12, result.BitDepth);
        Assert.False(result.IsSampled);
        Assert.Equal(codes.Length, result.SampleCount);
        AssertStatistics(codes, result.Statistics);
    }

    [Fact]
    public void ComputeHistogram_Sampled_ReadsOnlyThatChannel()
    {
        // 200×200 RGGB。R=1000 以外のチャネルは別の値。R格子全体(100×100)を
        // 間引いても、拾うのはRの画素だけ
        using RawImage image = BayerImage(200, 200, r: 1000, gr: 2000, gb: 3000, b: 4000);
        var region = new ChannelRegion(0, 0, 100, 100);

        HistogramResult result = ChannelRegionAnalysis.ComputeHistogram(
            image, 0, region, maxSamples: 100);

        Assert.True(result.IsSampled);
        Assert.InRange(result.SampleCount, 1, 199);
        Assert.Equal(result.SampleCount, result.Bins[1000]);
        Assert.Equal(new RegionStatistics(1000, 0, 1000, 1000, result.SampleCount), result.Statistics);
    }

    [Fact]
    public void ComputeHistogram_UsesRequestedFrame()
    {
        var format = new RawFormat { Width = 4, Height = 4, BitDepth = 12, FrameCount = 2 };
        ushort[] codes = Enumerable.Repeat((ushort)100, 16).Concat(Enumerable.Repeat((ushort)700, 16))
            .ToArray();
        using RawImage image = TestImages.FromCodes(codes, format);

        HistogramResult result = ChannelRegionAnalysis.ComputeHistogram(
            image, 1, new ChannelRegion(1, 1, 2, 2));

        Assert.Equal(700, result.Statistics.Mean);
        Assert.Equal(4, result.SampleCount);
    }

    [Fact]
    public void ComputeProjections_MatchesPixelsPickedOneByOne()
    {
        using RawImage image = RandomImage(12, 10, bitDepth: 14, seed: 2);
        var region = new ChannelRegion(3, 1, 4, 5);

        (double[] horizontal, double[] vertical) =
            ChannelRegionAnalysis.ComputeProjections(image, 0, region);

        int shift = 16 - 14;
        Assert.Equal(region.Width, horizontal.Length);
        Assert.Equal(region.Height, vertical.Length);
        for (int i = 0; i < region.Width; i++)
        {
            double expected = Enumerable.Range(0, region.Height)
                .Average(j => image.GetPixel(region.X + 2 * i, region.Y + 2 * j) >> shift);
            Assert.Equal(expected, horizontal[i], 9);
        }

        for (int j = 0; j < region.Height; j++)
        {
            double expected = Enumerable.Range(0, region.Width)
                .Average(i => image.GetPixel(region.X + 2 * i, region.Y + 2 * j) >> shift);
            Assert.Equal(expected, vertical[j], 9);
        }
    }

    [Fact]
    public void MeasureSingle_MatchesSameChannelPixelsAsOwnImage()
    {
        using RawImage image = RandomImage(16, 12, bitDepth: 12, seed: 3);
        var region = new ChannelRegion(1, 0, 6, 5);
        using RawImage extracted = Extract(image, 0, region);

        NoiseMeasurement actual = ChannelRegionAnalysis.MeasureSingle(
            image, 0, region, saturationCode: 4000);
        NoiseMeasurement expected = NoiseAnalysis.MeasureSingle(
            extracted, 0, null, BayerPattern.None, saturationCode: 4000);

        AssertMeasurement(expected, actual);
        Assert.True(double.IsNaN(actual.SigmaTemporal));
    }

    [Fact]
    public void MeasurePair_MatchesSameChannelPixelsAsOwnImages()
    {
        var format = new RawFormat { Width = 16, Height = 12, BitDepth = 12, FrameCount = 2 };
        var random = new Random(4);
        ushort[] codes = Enumerable.Range(0, 16 * 12 * 2).Select(_ => (ushort)random.Next(4096))
            .ToArray();
        using RawImage imageA = TestImages.FromCodes(codes, format);
        using RawImage imageB = RandomImage(16, 12, bitDepth: 12, seed: 5);
        var region = new ChannelRegion(0, 1, 7, 4);
        using RawImage extractedA = Extract(imageA, 1, region);
        using RawImage extractedB = Extract(imageB, 0, region);

        NoiseMeasurement actual = ChannelRegionAnalysis.MeasurePair(
            imageA, imageB, 1, 0, region, saturationCode: 0);
        NoiseMeasurement expected = NoiseAnalysis.MeasurePair(
            extractedA, extractedB, 0, 0, null, BayerPattern.None, saturationCode: 0);

        AssertMeasurement(expected, actual);
        Assert.Equal(4095, actual.SaturationCode);
    }

    [Fact]
    public void MeasurePair_SizeOrBitDepthMismatch_Throws()
    {
        using RawImage imageA = RandomImage(8, 8, bitDepth: 12, seed: 6);
        using RawImage smaller = RandomImage(8, 6, bitDepth: 12, seed: 7);
        using RawImage deeper = RandomImage(8, 8, bitDepth: 16, seed: 8);
        var region = new ChannelRegion(0, 0, 2, 2);

        Assert.Throws<ArgumentException>(() =>
            ChannelRegionAnalysis.MeasurePair(imageA, smaller, 0, 0, region));
        Assert.Throws<ArgumentException>(() =>
            ChannelRegionAnalysis.MeasurePair(imageA, deeper, 0, 0, region));
    }

    [Theory]
    [InlineData(-1, 0, 2, 2)]
    [InlineData(0, 0, 5, 1)]   // x = 0,2,4,6,8 → 8 は幅8の範囲外
    [InlineData(1, 1, 1, 4)]   // y = 1,3,5,7 → 7 は高さ6の範囲外
    public void RegionOutsideImage_Throws(int x, int y, int width, int height)
    {
        using RawImage image = RandomImage(8, 6, bitDepth: 12, seed: 9);
        var region = new ChannelRegion(x, y, width, height);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ChannelRegionAnalysis.ComputeHistogram(image, 0, region));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ChannelRegionAnalysis.ComputeProjections(image, 0, region));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ChannelRegionAnalysis.MeasureSingle(image, 0, region));
    }

    [Fact]
    public void EmptyRegion_ReturnsEmptyResults()
    {
        using RawImage image = RandomImage(8, 6, bitDepth: 12, seed: 10);
        var region = new ChannelRegion(2, 2, 0, 3);

        HistogramResult histogram = ChannelRegionAnalysis.ComputeHistogram(image, 0, region);
        (double[] horizontal, double[] vertical) =
            ChannelRegionAnalysis.ComputeProjections(image, 0, region);

        Assert.Equal(0, histogram.SampleCount);
        Assert.Empty(horizontal);
        Assert.Empty(vertical);
    }

    [Fact]
    public void ComputeProjections_Canceled_Throws()
    {
        using RawImage image = RandomImage(8, 8, bitDepth: 12, seed: 11);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => ChannelRegionAnalysis.ComputeProjections(
            image, 0, new ChannelRegion(0, 0, 4, 4), cts.Token));
    }

    /// <summary>タイル矩形の各表示画素が元画像のどの画素か(MapTiledToSource)を集める。</summary>
    private static HashSet<(int X, int Y)> DisplayedSourcePixels(
        RegionOfInterest tiled, int tiledWidth, int tiledHeight)
    {
        var pixels = new HashSet<(int X, int Y)>();
        for (int y = tiled.Y; y < tiled.Y + tiled.Height; y++)
        {
            for (int x = tiled.X; x < tiled.X + tiled.Width; x++)
            {
                pixels.Add(BayerSplit.MapTiledToSource(x, y, tiledWidth, tiledHeight));
            }
        }

        return pixels;
    }

    private static HashSet<(int X, int Y)> LatticePixels(ChannelRegion region)
    {
        var pixels = new HashSet<(int X, int Y)>();
        for (int j = 0; j < region.Height; j++)
        {
            for (int i = 0; i < region.Width; i++)
            {
                pixels.Add((region.X + 2 * i, region.Y + 2 * j));
            }
        }

        return pixels;
    }

    private static int[] Codes(RawImage image, int frame, ChannelRegion region)
    {
        int shift = 16 - image.Format.BitDepth;
        var codes = new List<int>();
        for (int j = 0; j < region.Height; j++)
        {
            for (int i = 0; i < region.Width; i++)
            {
                codes.Add(image.GetPixel(region.X + 2 * i, region.Y + 2 * j, frame) >> shift);
            }
        }

        return codes.ToArray();
    }

    /// <summary>格子の画素だけを並べた独立画像を作る(既存の解析との突き合わせ用)。</summary>
    private static RawImage Extract(RawImage image, int frame, ChannelRegion region)
    {
        ushort[] codes = Codes(image, frame, region).Select(c => (ushort)c).ToArray();
        return TestImages.FromCodes(codes, region.Width, region.Height, image.Format.BitDepth);
    }

    private static RawImage RandomImage(int width, int height, int bitDepth, int seed)
    {
        var random = new Random(seed);
        ushort[] codes = Enumerable.Range(0, width * height)
            .Select(_ => (ushort)random.Next(1 << bitDepth)).ToArray();
        return TestImages.FromCodes(codes, width, height, bitDepth);
    }

    private static RawImage BayerImage(int width, int height, int r, int gr, int gb, int b)
    {
        var codes = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                codes[y * width + x] = (ushort)(((y & 1) << 1 | (x & 1)) switch
                {
                    0 => r,
                    1 => gr,
                    2 => gb,
                    _ => b,
                });
            }
        }

        return TestImages.FromCodes(codes, width, height, 12, BayerPattern.Rggb);
    }

    private static void AssertStatistics(int[] codes, RegionStatistics statistics)
    {
        double mean = codes.Average();
        double sigma = Math.Sqrt(codes.Average(c => (c - mean) * (c - mean)));
        Assert.Equal(mean, statistics.Mean, 9);
        Assert.Equal(sigma, statistics.Sigma, 6);
        Assert.Equal(codes.Min(), statistics.Min);
        Assert.Equal(codes.Max(), statistics.Max);
        Assert.Equal(codes.Length, statistics.SampleCount);
    }

    private static void AssertMeasurement(NoiseMeasurement expected, NoiseMeasurement actual)
    {
        Assert.Equal(expected.SampleCount, actual.SampleCount);
        Assert.Equal(expected.Mean, actual.Mean, 9);
        Assert.Equal(expected.SigmaTotal, actual.SigmaTotal, 6);
        Assert.Equal(expected.SigmaTemporal, actual.SigmaTemporal, 6);
        Assert.Equal(expected.SigmaFpn, actual.SigmaFpn, 6);
        Assert.Equal(expected.SaturationCode, actual.SaturationCode);
    }
}
