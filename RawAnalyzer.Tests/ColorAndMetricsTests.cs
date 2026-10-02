using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class ColorConvertTests
{
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

        // 2画素目まで写す(count を終端Xと取り違えると1画素しか写らない)
        image.GetPixel(2, 1, out ushort r2, out ushort g2, out ushort b2);
        Assert.Equal(r2, buffer[3]);
        Assert.Equal(g2, buffer[4]);
        Assert.Equal(b2, buffer[5]);
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
        ColorImage image = ColorImage.FromInterleaved(2, 2, 16, rgb);
        using RawImage luminance = image.ToLuminance();

        Assert.Equal(2, luminance.Width);
        Assert.Equal(2, luminance.Height);
        Assert.Equal(16, luminance.Format.BitDepth);   // 元画像のビット深度が伝播する
        Assert.Equal(BayerPattern.None, luminance.Format.Bayer);
        Assert.InRange((int)luminance.GetPixel(0, 0), 19594, 19596);
        Assert.InRange((int)luminance.GetPixel(1, 0), 38468, 38470);
        Assert.InRange((int)luminance.GetPixel(0, 1), 7470, 7472);
        Assert.InRange((int)luminance.GetPixel(1, 1), 30599, 30601);
    }

    [Fact]
    public void ToLuminance_8Bit_RawCodeIsRoundedLumaLikeCursor()
    {
        // 8bitのカラー画像(WIC経路は v*257 で置く)の輝度は、解析が value >> 8 で読む raw code が
        // カーソルの YCbCr の Y(8bitの code から BT.601 で求めて四捨五入)と一致すること。
        // 16bitで丸めてから >> 8 で切り捨てると (30,20,10) の Y=21.85 が 21 になり、
        // (240,200,150) の Y=206.26 が 207 になっていた
        var colors = new List<(int R, int G, int B)> { (30, 20, 10), (240, 200, 150) };
        for (int r = 0; r < 256; r += 15)
        {
            for (int g = 0; g < 256; g += 15)
            {
                for (int b = 0; b < 256; b += 15)
                {
                    colors.Add((r, g, Math.Min(255, b + (r % 7))));
                }
            }
        }

        var rgb = new ushort[colors.Count * 3];
        for (int i = 0; i < colors.Count; i++)
        {
            rgb[i * 3] = (ushort)(colors[i].R * 257);
            rgb[(i * 3) + 1] = (ushort)(colors[i].G * 257);
            rgb[(i * 3) + 2] = (ushort)(colors[i].B * 257);
        }

        ColorImage image = ColorImage.FromInterleaved(colors.Count, 1, 8, rgb);
        using RawImage luminance = image.ToLuminance();

        Assert.Equal(8, luminance.Format.BitDepth);
        for (int i = 0; i < colors.Count; i++)
        {
            (int R, int G, int B) c = colors[i];
            int cursorY = ColorConvert.RgbToYCbCr(c.R, c.G, c.B, 255).Y;
            ushort value = luminance.GetPixel(i, 0);
            Assert.True(cursorY == value >> 8, $"({c.R},{c.G},{c.B}): 輝度 {value} >> 8 = {value >> 8}、カーソルの Y = {cursorY}");
            Assert.Equal(cursorY * 257, value);   // 元のチャネルと同じ v*257 の置き方
        }

        // 一様なパッチのROI平均も同じ値になる
        RegionStatistics dark = ImageAnalysis.ComputeStatistics(luminance, 0, new RegionOfInterest(0, 0, 1, 1));
        RegionStatistics bright = ImageAnalysis.ComputeStatistics(luminance, 0, new RegionOfInterest(1, 0, 1, 1));
        Assert.Equal(22, dark.Mean);
        Assert.Equal(206, bright.Mean);
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

        using RawImage image = TestImages.FromCodes(codes.ToArray(), 10, 10, bitDepth: 12);
        HistogramResult histogram = ImageAnalysis.ComputeHistogram(image, 0);
        HistogramMetrics metrics = ImageAnalysis.ComputeHistogramMetrics(histogram);

        Assert.Equal(100, metrics.SampleCount);
        Assert.Equal(100, metrics.Median);
        Assert.Equal(100, metrics.Mode);
        Assert.Equal(5.0, metrics.SaturatedPercent, 6);
        Assert.Equal(5.0, metrics.ZeroPercent, 6);
        Assert.Equal(0, metrics.Min);
        Assert.Equal(4095, metrics.Max);

        // 20·log10(最大code / σ)。σ は母標準偏差 √760573.69 ≈ 872.11 なので 20·log10(4095/872.11) ≈ 13.43
        Assert.Equal(13.43, metrics.DynamicRangeDb, 2);
    }

    /// <summary>1行の12bit画像を作り、全画素のヒストグラム指標を求める。</summary>
    private static HistogramMetrics MetricsOf(ushort[] codes)
    {
        using RawImage image = TestImages.FromCodes(codes, codes.Length, 1, bitDepth: 12);
        return ImageAnalysis.ComputeHistogramMetrics(ImageAnalysis.ComputeHistogram(image, 0));
    }

    [Theory]
    [InlineData(new ushort[] { 70 }, 70, 70, 70)]                      // 1画素
    [InlineData(new ushort[] { 100, 200 }, 100, 200, 200)]             // レビュー指摘#15の再現値(下記)
    [InlineData(new ushort[] { 30, 10, 20 }, 10, 20, 30)]              // 奇数
    [InlineData(new ushort[] { 40, 10, 30, 20 }, 10, 30, 40)]          // 偶数(中央値は上側)
    [InlineData(new ushort[] { 5, 9, 5, 9, 9, 1 }, 1, 9, 9)]           // 重複あり
    public void ComputeHistogramMetrics_SmallSamples_UseSingleRankConvention(
        ushort[] codes, int expectedP1, int expectedMedian, int expectedP99)
    {
        // レビュー指摘#15: コード[100,200]のROIで Median=200・P99=100 と逆転していた
        // (中央値は「累積 > N/2」、P99 は「累積 ≥ floor(0.99N)」と順位規約が違っていた)
        HistogramMetrics metrics = MetricsOf(codes);

        Assert.Equal(expectedP1, metrics.P1);
        Assert.Equal(expectedMedian, metrics.Median);
        Assert.Equal(expectedP99, metrics.P99);
    }

    [Theory]
    [InlineData(100, 1, 50, 98)]
    public void ComputeHistogramMetrics_LargeSamples_KeepEstablishedPercentiles(
        int count, int expectedP1, int expectedMedian, int expectedP99)
    {
        // 規約の統一で、100画素のような通常の標本の値は変わらない
        // (0..N-1 を1個ずつ: P1・P99 は両端から約1%ずつ除いた位置)
        ushort[] codes = Enumerable.Range(0, count).Select(i => (ushort)i).ToArray();

        HistogramMetrics metrics = MetricsOf(codes);

        Assert.Equal(expectedP1, metrics.P1);
        Assert.Equal(expectedMedian, metrics.Median);
        Assert.Equal(expectedP99, metrics.P99);
    }

    [Fact]
    public void ComputeHistogramMetrics_AnySampleCount_MatchesRankConventionAndStaysOrdered()
    {
        // 中央値・P1・P99 は同じ規約で求める: 昇順 N 個のうち0始まりの順位
        // floor(q·(N−1) + 0.5) の値(補間位置 q·(N−1) に最も近い順位、ちょうど中間は上側)
        var random = new Random(15);
        for (int n = 1; n <= 130; n++)
        {
            var codes = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                codes[i] = (ushort)random.Next(0, 64);   // 重複を多く含める
            }

            HistogramMetrics metrics = MetricsOf(codes);
            ushort[] sorted = codes.Order().ToArray();

            Assert.Equal(sorted[(int)Math.Floor(0.01 * (n - 1) + 0.5)], metrics.P1);
            Assert.Equal(sorted[(int)Math.Floor(0.5 * (n - 1) + 0.5)], metrics.Median);
            Assert.Equal(sorted[(int)Math.Floor(0.99 * (n - 1) + 0.5)], metrics.P99);
            Assert.True(
                metrics.P1 <= metrics.Median && metrics.Median <= metrics.P99,
                $"N={n}: P1={metrics.P1}, Median={metrics.Median}, P99={metrics.P99}");
        }
    }
}
