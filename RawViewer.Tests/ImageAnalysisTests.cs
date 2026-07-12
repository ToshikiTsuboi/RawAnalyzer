using RawViewer.Core;
using Xunit;

namespace RawViewer.Tests;

public class ImageAnalysisTests
{
    private static RawImage LoadImage(ushort[] codes, int width, int height, int bitDepth = 16)
    {
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

    [Fact]
    public void ComputeStatistics_KnownData_MatchesExpected()
    {
        // 0..15 の4x4: mean=7.5, σ=sqrt(1240/16 - 56.25)=sqrt(21.25)
        ushort[] codes = Enumerable.Range(0, 16).Select(i => (ushort)i).ToArray();
        using RawImage image = LoadImage(codes, 4, 4);

        RegionStatistics stats = ImageAnalysis.ComputeStatistics(
            image, 0, new RegionOfInterest(0, 0, 4, 4));

        Assert.Equal(7.5, stats.Mean, 10);
        Assert.Equal(Math.Sqrt(21.25), stats.Sigma, 10);
        Assert.Equal(0, stats.Min);
        Assert.Equal(15, stats.Max);
        Assert.Equal(16, stats.SampleCount);
    }

    [Fact]
    public void ComputeStatistics_SubRegion_OnlyCountsRoi()
    {
        ushort[] codes = Enumerable.Range(0, 16).Select(i => (ushort)i).ToArray();
        using RawImage image = LoadImage(codes, 4, 4);

        // 右下2x2 = {10,11,14,15}: mean=12.5
        RegionStatistics stats = ImageAnalysis.ComputeStatistics(
            image, 0, new RegionOfInterest(2, 2, 2, 2));

        Assert.Equal(12.5, stats.Mean, 10);
        Assert.Equal(10, stats.Min);
        Assert.Equal(15, stats.Max);
        Assert.Equal(4, stats.SampleCount);
    }

    [Fact]
    public void ComputeStatistics_12Bit_UsesRawCodeDomain()
    {
        ushort[] codes = { 0, 1000, 2000, 4095 };
        using RawImage image = LoadImage(codes, 2, 2, bitDepth: 12);

        RegionStatistics stats = ImageAnalysis.ComputeStatistics(
            image, 0, new RegionOfInterest(0, 0, 2, 2));

        Assert.Equal((0 + 1000 + 2000 + 4095) / 4.0, stats.Mean, 10);
        Assert.Equal(4095, stats.Max);
    }

    [Fact]
    public void ComputeHistogram_BinsInRawCodeDomain()
    {
        ushort[] codes = { 0, 0, 100, 4095 };
        using RawImage image = LoadImage(codes, 2, 2, bitDepth: 12);

        HistogramResult result = ImageAnalysis.ComputeHistogram(image, 0);

        Assert.Equal(1 << 12, result.Bins.Length);
        Assert.False(result.IsSampled);
        Assert.Equal(4, result.SampleCount);
        Assert.Equal(2u, result.Bins[0]);
        Assert.Equal(1u, result.Bins[100]);
        Assert.Equal(1u, result.Bins[4095]);
    }

    [Fact]
    public void ComputeHistogram_RoiRestrictsSamples()
    {
        ushort[] codes = Enumerable.Range(0, 16).Select(i => (ushort)i).ToArray();
        using RawImage image = LoadImage(codes, 4, 4);

        HistogramResult result = ImageAnalysis.ComputeHistogram(
            image, 0, new RegionOfInterest(2, 2, 2, 2));

        Assert.Equal(4, result.SampleCount);
        Assert.Equal(1u, result.Bins[10]);
        Assert.Equal(0u, result.Bins[0]);
    }

    [Fact]
    public void ComputeHistogram_LargeRegion_IsSampled()
    {
        ushort[] codes = new ushort[64 * 64];
        Array.Fill(codes, (ushort)500);
        using RawImage image = LoadImage(codes, 64, 64, bitDepth: 12);

        HistogramResult result = ImageAnalysis.ComputeHistogram(image, 0, maxSamples: 100);

        Assert.True(result.IsSampled);
        Assert.True(result.SampleCount <= 64 * 64 / 4, "サンプル数が間引かれていること");
        Assert.Equal((uint)result.SampleCount, result.Bins[500]);
        Assert.Equal(500, result.Statistics.Mean, 10);
    }

    [Fact]
    public void ComputeHistogram_BinTotalEqualsSampleCount()
    {
        ushort[] codes = TestData.MakePattern(32 * 32, 12);
        using RawImage image = LoadImage(codes, 32, 32, bitDepth: 12);

        HistogramResult result = ImageAnalysis.ComputeHistogram(image, 0);

        Assert.Equal(result.SampleCount, result.Bins.Sum(b => (long)b));
    }

    [Fact]
    public void ExtractRowProfile_MatchesPixels()
    {
        ushort[] codes = TestData.MakePattern(8 * 4, 12);
        using RawImage image = LoadImage(codes, 8, 4, bitDepth: 12);

        ushort[] profile = ImageAnalysis.ExtractRowProfile(image, 0, 2);

        Assert.Equal(8, profile.Length);
        for (int x = 0; x < 8; x++)
        {
            Assert.Equal(codes[2 * 8 + x], profile[x]);
        }
    }

    [Fact]
    public void ExtractColumnProfile_MatchesPixels()
    {
        ushort[] codes = TestData.MakePattern(8 * 4, 12);
        using RawImage image = LoadImage(codes, 8, 4, bitDepth: 12);

        ushort[] profile = ImageAnalysis.ExtractColumnProfile(image, 0, 3);

        Assert.Equal(4, profile.Length);
        for (int y = 0; y < 4; y++)
        {
            Assert.Equal(codes[y * 8 + 3], profile[y]);
        }
    }

    [Fact]
    public void RegionOfInterest_Clamp_LimitsToImageBounds()
    {
        var roi = new RegionOfInterest(-5, -5, 100, 100).Clamp(10, 8);
        Assert.Equal(new RegionOfInterest(0, 0, 10, 8), roi);

        var outside = new RegionOfInterest(20, 20, 5, 5).Clamp(10, 8);
        Assert.Equal(0, outside.PixelCount);
    }
}
