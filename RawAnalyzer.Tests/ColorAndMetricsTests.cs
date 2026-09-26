using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

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
    public void RgbToYCbCr_PureRedAndBlue_DriveCrAndCb()
    {
        (int y, int cb, int cr) = ColorConvert.RgbToYCbCr(255, 0, 0, 255);
        Assert.Equal(76, y);                       // 0.299 * 255
        Assert.InRange(cr, 254, 255);              // 最大寄り
        Assert.InRange(cb, 84, 86);                // 128 - 0.168736*255

        // 純青では Cb が最大寄り、Cr は 128 - 0.081312*255 ≈ 107
        (int _, int blueCb, int blueCr) = ColorConvert.RgbToYCbCr(0, 0, 255, 255);
        Assert.InRange(blueCb, 254, 255);
        Assert.InRange(blueCr, 107, 109);
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
        // 2x2: 純赤・純緑・純青と混色(16bitフルスケール)。期待値はBT.601係数の手計算
        //   0.299*65535 = 19594.97, 0.587*65535 = 38469.05, 0.114*65535 = 7470.99
        //   0.299*20000 + 0.587*40000 + 0.114*10000 = 30600
        // 丸め規則の違いは ±1 で吸収する
        var rgb = new ushort[]
        {
            65535, 0, 0,   0, 65535, 0,
            0, 0, 65535,   20000, 40000, 10000,
        };
        ColorImage image = ColorImage.FromInterleaved(2, 2, 8, rgb);
        using RawImage luminance = image.ToLuminance();

        Assert.Equal(2, luminance.Width);
        Assert.Equal(2, luminance.Height);
        Assert.Equal(8, luminance.Format.BitDepth);   // 元画像のビット深度が伝播する
        Assert.Equal(BayerPattern.None, luminance.Format.Bayer);
        Assert.InRange((int)luminance.GetPixel(0, 0), 19594, 19596);
        Assert.InRange((int)luminance.GetPixel(1, 0), 38468, 38470);
        Assert.InRange((int)luminance.GetPixel(0, 1), 7470, 7472);
        Assert.InRange((int)luminance.GetPixel(1, 1), 30599, 30601);
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

    /// <summary>
    /// 番兵値(4095)で埋めた画像の ROI 内側だけに roiValues[row, col] を置く。
    /// ROI が1画素でもはみ出せば番兵値が平均に混ざって落ちる。
    /// </summary>
    private static RawImage LoadWithRoiValues(
        int width, int height, RegionOfInterest roi, ushort[,] roiValues)
    {
        var codes = new ushort[width * height];
        Array.Fill(codes, (ushort)4095);
        for (int row = 0; row < roi.Height; row++)
        {
            for (int col = 0; col < roi.Width; col++)
            {
                codes[(roi.Y + row) * width + roi.X + col] = roiValues[row, col];
            }
        }

        return LoadImage(codes, width, height);
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
    public void ComputeProjections_Horizontal_AveragesEachColumnWithinRoi()
    {
        // 8x5 の ROI(2,1,4,3) に
        //   y=1:  90 190 290 390
        //   y=2: 100 200 300 400
        //   y=3: 110 210 310 410
        // を置く。列ごとの行方向平均は 100 / 200 / 300 / 400
        var roi = new RegionOfInterest(2, 1, 4, 3);
        ushort[,] roiValues =
        {
            { 90, 190, 290, 390 },
            { 100, 200, 300, 400 },
            { 110, 210, 310, 410 },
        };
        using RawImage image = LoadWithRoiValues(8, 5, roi, roiValues);

        (double[] horizontal, double[] vertical) = ImageAnalysis.ComputeProjections(image, 0, roi);

        Assert.Equal(4, horizontal.Length);
        Assert.Equal(3, vertical.Length);
        Assert.Equal(100, horizontal[0], 10);
        Assert.Equal(200, horizontal[1], 10);
        Assert.Equal(300, horizontal[2], 10);
        Assert.Equal(400, horizontal[3], 10);
    }

    [Fact]
    public void ComputeProjections_Vertical_AveragesEachRowWithinRoi()
    {
        // 6x8 の ROI(1,2,4,3) に
        //   y=2:   10   20   30   40  → 平均   25
        //   y=3:  100  200  300  400  → 平均  250
        //   y=4: 1000 1100 1200 1300  → 平均 1150
        var roi = new RegionOfInterest(1, 2, 4, 3);
        ushort[,] roiValues =
        {
            { 10, 20, 30, 40 },
            { 100, 200, 300, 400 },
            { 1000, 1100, 1200, 1300 },
        };
        using RawImage image = LoadWithRoiValues(6, 8, roi, roiValues);

        (double[] horizontal, double[] vertical) = ImageAnalysis.ComputeProjections(image, 0, roi);

        Assert.Equal(4, horizontal.Length);
        Assert.Equal(3, vertical.Length);
        Assert.Equal(25, vertical[0], 10);
        Assert.Equal(250, vertical[1], 10);
        Assert.Equal(1150, vertical[2], 10);
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
}
