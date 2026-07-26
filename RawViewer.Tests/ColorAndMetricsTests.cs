using RawViewer.Core;
using Xunit;

namespace RawViewer.Tests;

public class ColorConvertTests
{
    [Fact]
    public void RgbToYCbCr_White_IsMaxLumaNeutralChroma()
    {
        (int y, int cb, int cr) = ColorConvert.RgbToYCbCr(255, 255, 255, 255);
        Assert.Equal(255, y);
        Assert.Equal(128, cb);
        Assert.Equal(128, cr);
    }

    [Fact]
    public void RgbToYCbCr_Black_IsZeroLumaNeutralChroma()
    {
        (int y, int cb, int cr) = ColorConvert.RgbToYCbCr(0, 0, 0, 255);
        Assert.Equal(0, y);
        Assert.Equal(128, cb);
        Assert.Equal(128, cr);
    }

    [Fact]
    public void RgbToYCbCr_PureRed_HasHighCrLowCb()
    {
        (int y, int cb, int cr) = ColorConvert.RgbToYCbCr(255, 0, 0, 255);
        Assert.Equal(76, y);                       // 0.299 * 255
        Assert.InRange(cr, 254, 255);              // 最大寄り
        Assert.InRange(cb, 84, 86);                // 128 - 0.168736*255
    }

    [Fact]
    public void RgbToYCbCr_PureBlue_HasHighCb()
    {
        (int _, int cb, int cr) = ColorConvert.RgbToYCbCr(0, 0, 255, 255);
        Assert.InRange(cb, 254, 255);
        Assert.InRange(cr, 107, 109);
    }

    [Fact]
    public void RgbToYCbCr_12BitRange_ScalesChromaCenter()
    {
        (int y, int cb, int cr) = ColorConvert.RgbToYCbCr(2048, 2048, 2048, 4095);
        Assert.Equal(2048, y);
        Assert.Equal(2048, cb);
        Assert.Equal(2048, cr);
    }

    [Fact]
    public void Luma_MatchesBt601Weights()
    {
        Assert.Equal(65535, ColorConvert.Luma(65535, 65535, 65535));
        Assert.Equal(0, ColorConvert.Luma(0, 0, 0));
        Assert.Equal((ushort)Math.Round(0.587 * 65535), ColorConvert.Luma(0, 65535, 0));
    }
}

public class ColorImageTests
{
    private static ColorImage MakeImage(int width, int height, int bitDepth = 8)
    {
        var rgb = new ushort[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            rgb[i * 3] = (ushort)(i * 3);
            rgb[i * 3 + 1] = (ushort)(i * 5);
            rgb[i * 3 + 2] = (ushort)(i * 7);
        }

        return ColorImage.FromInterleaved(width, height, bitDepth, rgb);
    }

    [Fact]
    public void GetPixel_ReturnsInterleavedChannels()
    {
        ColorImage image = MakeImage(4, 3);
        image.GetPixel(1, 1, out ushort r, out ushort g, out ushort b);
        int index = 1 * 4 + 1;
        Assert.Equal(index * 3, r);
        Assert.Equal(index * 5, g);
        Assert.Equal(index * 7, b);
    }

    [Fact]
    public void CopyRow_CopiesInterleavedSegment()
    {
        ColorImage image = MakeImage(4, 2);
        var buffer = new ushort[2 * 3];
        image.CopyRow(1, 1, 2, buffer);
        image.GetPixel(1, 1, out ushort r, out ushort g, out ushort b);
        Assert.Equal(r, buffer[0]);
        Assert.Equal(g, buffer[1]);
        Assert.Equal(b, buffer[2]);
    }

    [Fact]
    public void ToLuminance_ProducesBt601Luma()
    {
        var rgb = new ushort[3] { 65535, 0, 0 };
        ColorImage image = ColorImage.FromInterleaved(1, 1, 16, rgb);
        using RawImage luminance = image.ToLuminance();
        Assert.Equal(1, luminance.Width);
        Assert.Equal(ColorConvert.Luma(65535, 0, 0), luminance.GetPixel(0, 0));
        Assert.Equal(16, luminance.Format.BitDepth);
    }

    [Fact]
    public void FromInterleaved_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentException>(
            () => ColorImage.FromInterleaved(0, 1, 8, new ushort[3]));
        Assert.Throws<ArgumentException>(
            () => ColorImage.FromInterleaved(1, 1, 12, new ushort[3]));
        Assert.Throws<ArgumentException>(
            () => ColorImage.FromInterleaved(2, 2, 8, new ushort[3]));
    }
}

public class ProfileAndMetricsTests
{
    private static RawImage LoadImage(ushort[] codes, int width, int height, int bitDepth = 12)
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
    public void ComputeProfileStatistics_KnownValues()
    {
        double[] values = { 1, 2, 3, 4, 100 };
        ProfileStatistics stats = ImageAnalysis.ComputeProfileStatistics(values);

        Assert.Equal(5, stats.Count);
        Assert.Equal(22.0, stats.Mean, 10);
        Assert.Equal(1, stats.Min);
        Assert.Equal(100, stats.Max);
        Assert.Equal(3, stats.Median);
        // 母分散: (21²+20²+19²+18²+78²)/5 = 7610/5 = 1522
        Assert.Equal(Math.Sqrt(1522.0), stats.Sigma, 6);
    }

    [Fact]
    public void ComputeProfileStatistics_EvenCount_AveragesMiddlePair()
    {
        double[] values = { 10, 20, 30, 40 };
        ProfileStatistics stats = ImageAnalysis.ComputeProfileStatistics(values);
        Assert.Equal(25, stats.Median);
    }

    [Fact]
    public void ComputeProfileStatistics_Empty_ReturnsZeros()
    {
        ProfileStatistics stats = ImageAnalysis.ComputeProfileStatistics(Array.Empty<double>());
        Assert.Equal(0, stats.Count);
        Assert.Equal(0, stats.Mean);
    }

    [Fact]
    public void ComputeHorizontalProjection_AveragesColumns()
    {
        // 各列が一定値(x*100)、行方向に±ばらつきを持たせる
        const int width = 4;
        const int height = 4;
        var codes = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                codes[y * width + x] = (ushort)(x * 100 + (y % 2 == 0 ? 10 : -10) + 10);
            }
        }

        using RawImage image = LoadImage(codes, width, height);
        double[] projection = ImageAnalysis.ComputeHorizontalProjection(
            image, 0, new RegionOfInterest(0, 0, width, height));

        Assert.Equal(width, projection.Length);
        for (int x = 0; x < width; x++)
        {
            Assert.Equal(x * 100 + 10, projection[x], 10);
        }
    }

    [Fact]
    public void ComputeVerticalProjection_AveragesRows()
    {
        const int width = 4;
        const int height = 3;
        var codes = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                codes[y * width + x] = (ushort)(y * 50 + x);
            }
        }

        using RawImage image = LoadImage(codes, width, height);
        double[] projection = ImageAnalysis.ComputeVerticalProjection(
            image, 0, new RegionOfInterest(0, 0, width, height));

        Assert.Equal(height, projection.Length);
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(y * 50 + 1.5, projection[y], 10);
        }
    }

    [Fact]
    public void ComputeProjection_RestrictedToRoi()
    {
        const int size = 8;
        var codes = new ushort[size * size];
        Array.Fill(codes, (ushort)1000);
        for (int y = 2; y < 4; y++)
        {
            for (int x = 2; x < 6; x++)
            {
                codes[y * size + x] = 2000;
            }
        }

        using RawImage image = LoadImage(codes, size, size);
        double[] projection = ImageAnalysis.ComputeHorizontalProjection(
            image, 0, new RegionOfInterest(2, 2, 4, 2));

        Assert.Equal(4, projection.Length);
        Assert.All(projection, v => Assert.Equal(2000, v, 10));
    }

    [Fact]
    public void ComputeHistogramMetrics_DerivesMedianModeAndPercentiles()
    {
        // code 100 が多数、飽和(4095)と0を少量含む
        var codes = new List<ushort>();
        for (int i = 0; i < 90; i++)
        {
            codes.Add(100);
        }

        for (int i = 0; i < 5; i++)
        {
            codes.Add(4095);
        }

        for (int i = 0; i < 5; i++)
        {
            codes.Add(0);
        }

        using RawImage image = LoadImage(codes.ToArray(), 10, 10);
        HistogramResult histogram = ImageAnalysis.ComputeHistogram(image, 0);
        HistogramMetrics metrics = ImageAnalysis.ComputeHistogramMetrics(histogram);

        Assert.Equal(100, metrics.SampleCount);
        Assert.Equal(100, metrics.Median);
        Assert.Equal(100, metrics.Mode);
        Assert.Equal(5.0, metrics.SaturatedPercent, 6);
        Assert.Equal(5.0, metrics.ZeroPercent, 6);
        Assert.Equal(0, metrics.Min);
        Assert.Equal(4095, metrics.Max);
        Assert.True(metrics.DynamicRangeDb > 0);
    }

    [Fact]
    public void ComputeHistogramMetrics_EmptyRegion_ReturnsZeros()
    {
        using RawImage image = LoadImage(new ushort[4], 2, 2);
        HistogramResult histogram = ImageAnalysis.ComputeHistogram(
            image, 0, new RegionOfInterest(10, 10, 2, 2));
        HistogramMetrics metrics = ImageAnalysis.ComputeHistogramMetrics(histogram);
        Assert.Equal(0, metrics.SampleCount);
    }
}
