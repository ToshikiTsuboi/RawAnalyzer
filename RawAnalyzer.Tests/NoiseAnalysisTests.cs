using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class NoiseAnalysisTests
{
    [Fact]
    public void MeasurePair_SeparatesFpnAndTemporalNoise()
    {
        // FPN: 列パリティで ±20 / 時間ノイズ: 行パリティで ±6(FPNと独立にする)
        const int width = 32;
        const int height = 32;
        var a = new ushort[width * height];
        var b = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int fixedPattern = x % 2 == 0 ? -20 : 20;
                int temporal = y % 2 == 0 ? -6 : 6;
                int index = y * width + x;
                a[index] = (ushort)(2000 + fixedPattern + temporal);
                b[index] = (ushort)(2000 + fixedPattern - temporal);
            }
        }

        using RawImage imageA = TestImages.FromCodes(a, width, height, bitDepth: 12);
        using RawImage imageB = TestImages.FromCodes(b, width, height, bitDepth: 12);

        NoiseMeasurement result = NoiseAnalysis.MeasurePair(imageA, imageB);

        // 差分は ±12 → σ_diff = 12 → σ_temporal = 12/√2 ≈ 8.485
        Assert.Equal(12.0 / Math.Sqrt(2), result.SigmaTemporal, 6);

        // σ_total² = 20² + 6² = 436 → σ_total ≈ 20.88
        Assert.Equal(Math.Sqrt(436), result.SigmaTotal, 6);

        // σ_fpn = √(436 − 72) = √364 ≈ 19.08
        Assert.Equal(Math.Sqrt(436 - 72), result.SigmaFpn, 6);
    }

    [Fact]
    public void DynamicRange_UsesTemporalNoiseAndSaturation()
    {
        const int size = 16;
        var a = new ushort[size * size];
        var b = new ushort[size * size];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = 1000;
            b[i] = (ushort)(i % 2 == 0 ? 990 : 1010);
        }

        using RawImage imageA = TestImages.FromCodes(a, size, size, bitDepth: 12);
        using RawImage imageB = TestImages.FromCodes(b, size, size, bitDepth: 12);

        NoiseMeasurement result = NoiseAnalysis.MeasurePair(imageA, imageB);

        // 差分が ±10 で半々 → σ_diff = 10 → σ_temporal = 10/√2
        double sigmaTemporal = 10.0 / Math.Sqrt(2);
        Assert.Equal(sigmaTemporal, result.SigmaTemporal, 8);

        // DRは飽和コード(12bit: 4095)と σ_temporal から
        double expectedDb = 20 * Math.Log10(4095 / sigmaTemporal);
        Assert.Equal(expectedDb, result.DynamicRangeTemporalDb, 6);
        Assert.Equal(Math.Log2(4095 / sigmaTemporal), result.DynamicRangeTemporalStops, 6);
    }

    [Fact]
    public void MeasurePair_ExplicitSaturation_OverridesBitDepthMax()
    {
        const int size = 8;
        var a = new ushort[size * size];
        var b = new ushort[size * size];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = 500;
            b[i] = (ushort)(i % 2 == 0 ? 496 : 504);
        }

        using RawImage imageA = TestImages.FromCodes(a, size, size, bitDepth: 12);
        using RawImage imageB = TestImages.FromCodes(b, size, size, bitDepth: 12);

        NoiseMeasurement result = NoiseAnalysis.MeasurePair(
            imageA, imageB, saturationCode: 3600);

        // 差分は ±4 → σ_diff = 4 → σ_temporal = 4/√2
        Assert.Equal(3600, result.SaturationCode);
        Assert.Equal(20 * Math.Log10(3600 / (4.0 / Math.Sqrt(2))),
            result.DynamicRangeTemporalDb, 6);
    }

    [Fact]
    public void MeasurePair_Roi_RestrictsEvaluation()
    {
        const int size = 16;
        var a = new ushort[size * size];
        var b = new ushort[size * size];
        Array.Fill(a, (ushort)1000);
        Array.Fill(b, (ushort)1000);
        // ROI外に大きな差分を置く
        for (int x = 0; x < size; x++)
        {
            a[0 * size + x] = 4000;
        }

        using RawImage imageA = TestImages.FromCodes(a, size, size, bitDepth: 12);
        using RawImage imageB = TestImages.FromCodes(b, size, size, bitDepth: 12);

        NoiseMeasurement result = NoiseAnalysis.MeasurePair(
            imageA, imageB, region: new RegionOfInterest(0, 1, size, size - 1));

        Assert.Equal(0, result.SigmaTemporal, 10);
        Assert.Equal((size - 1) * size, result.SampleCount);
    }

    [Fact]
    public void MeasureSingle_ReportsTotalSigmaOnly()
    {
        const int size = 8;
        var codes = new ushort[size * size];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(1000 + (i % 2) * 20);
        }

        using RawImage image = TestImages.FromCodes(codes, size, size, bitDepth: 12);
        NoiseMeasurement result = NoiseAnalysis.MeasureSingle(image);

        Assert.Equal(10, result.SigmaTotal, 6);
        Assert.True(double.IsNaN(result.SigmaTemporal));
        Assert.True(double.IsNaN(result.SigmaFpn));
        Assert.True(double.IsNaN(result.DynamicRangeTemporalDb));
        Assert.Equal(20 * Math.Log10(4095 / 10.0), result.DynamicRangeTotalDb, 6);
    }

    [Fact]
    public void MeasurePair_BayerPattern_ExcludesChannelOffsetsFromFpn()
    {
        // チャネル間の感度差(R=1000/G=2000/B=1500)だけがあり、真のFPN=0・時間ノイズ0の2枚。
        // 混合統計ならσ_FPN≈410と誤るが、チャネル別ならσ_FPN≈0になるはず
        const int size = 32;
        var codes = new ushort[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                codes[y * size + x] = (y & 1) == 0
                    ? ((x & 1) == 0 ? (ushort)1000 : (ushort)2000)
                    : ((x & 1) == 0 ? (ushort)2000 : (ushort)1500);
            }
        }

        using RawImage a = TestImages.FromCodes(codes, size, size, bitDepth: 12);
        using RawImage b = TestImages.FromCodes(codes, size, size, bitDepth: 12);

        NoiseMeasurement mixed = NoiseAnalysis.MeasurePair(a, b);
        NoiseMeasurement perChannel = NoiseAnalysis.MeasurePair(
            a, b, pattern: BayerPattern.Rggb);

        Assert.True(mixed.SigmaFpn > 300);       // 混合では感度差がFPNに化ける
        Assert.Equal(0, perChannel.SigmaFpn, 6); // チャネル別なら真値0
        Assert.Equal(0, perChannel.SigmaTemporal, 10);
        Assert.Equal(mixed.Mean, perChannel.Mean, 6);
        Assert.Equal(mixed.SampleCount, perChannel.SampleCount);
    }

    [Fact]
    public void MeasureSingle_BayerPattern_PoolsWithinChannelVariance()
    {
        // 各チャネル内のσは10相当(±10の交互ディザ)、チャネル平均は1000/2000/1500
        const int size = 32;
        var codes = new ushort[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                ushort baseCode = (y & 1) == 0
                    ? ((x & 1) == 0 ? (ushort)1000 : (ushort)2000)
                    : ((x & 1) == 0 ? (ushort)2000 : (ushort)1500);

                // 同一チャネル内で+10/-10を交互に振る(チャネル内σ=10)
                int dither = ((x / 2 + y / 2) % 2 == 0) ? 10 : -10;
                codes[y * size + x] = (ushort)(baseCode + dither);
            }
        }

        using RawImage image = TestImages.FromCodes(codes, size, size, bitDepth: 12);

        NoiseMeasurement result = NoiseAnalysis.MeasureSingle(
            image, pattern: BayerPattern.Rggb);

        Assert.Equal(10.0, result.SigmaTotal, 6);
    }

    [Fact]
    public void MeasurePair_BayerRoi_UsesSameRegionForSpatialAndTemporal()
    {
        // レビュー指摘#6の再現値: 6×6 RGGB、roi=(1,1,4,4) 内はすべて1000、roi外の(0,0)だけ5000。
        // 空間統計だけ roi を外側の2x2境界へ広げると36画素・σ_total≈628.5 になり、
        // 同じ画像同士(σ_temporal=0)で σ_FPN≈628.5 という存在しないFPNが出ていた
        const int size = 6;
        var codes = new ushort[size * size];
        Array.Fill(codes, (ushort)1000);
        codes[0] = 5000;
        using RawImage image = TestImages.FromCodes(codes, size, size, bitDepth: 16);
        var roi = new RegionOfInterest(1, 1, 4, 4);

        NoiseMeasurement single = NoiseAnalysis.MeasureSingle(
            image, region: roi, pattern: BayerPattern.Rggb);
        NoiseMeasurement pair = NoiseAnalysis.MeasurePair(
            image, image, region: roi, pattern: BayerPattern.Rggb);

        Assert.Equal(16, single.SampleCount);
        Assert.Equal(1000, single.Mean, 10);
        Assert.Equal(0, single.SigmaTotal, 10);
        Assert.Equal(16, pair.SampleCount);
        Assert.Equal(0, pair.SigmaTotal, 10);
        Assert.Equal(0, pair.SigmaTemporal, 10);
        Assert.Equal(0, pair.SigmaFpn, 10);
    }

    [Theory]
    [InlineData(1, 1, 5, 3)]   // 奇数座標・奇数サイズ(2x2境界へ広げると 6×4=24 画素になる)
    [InlineData(2, 1, 4, 4)]   // 偶数X・奇数Y
    [InlineData(3, 2, 1, 3)]   // 1列だけ(2チャネルしか含まない)
    public void MeasurePair_BayerRoi_MatchesPerChannelReferenceOnExactRoi(
        int roiX, int roiY, int roiWidth, int roiHeight)
    {
        // roi 外は極端な値にして、評価領域が1画素でもはみ出せば結果が変わるようにする
        const int width = 8;
        const int height = 6;
        var roi = new RegionOfInterest(roiX, roiY, roiWidth, roiHeight);
        var a = new ushort[width * height];
        var b = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                bool inside = x >= roi.X && x < roi.X + roi.Width
                    && y >= roi.Y && y < roi.Y + roi.Height;
                int channelOffset = ((y & 1) * 2 + (x & 1)) * 300;
                a[i] = inside ? (ushort)(1000 + channelOffset + (i * 37 % 23) * 3) : (ushort)4000;
                b[i] = inside ? (ushort)(a[i] + (i * 17 % 7) - 3) : (ushort)0;
            }
        }

        using RawImage imageA = TestImages.FromCodes(a, width, height, bitDepth: 12);
        using RawImage imageB = TestImages.FromCodes(b, width, height, bitDepth: 12);

        NoiseMeasurement result = NoiseAnalysis.MeasurePair(
            imageA, imageB, region: roi, pattern: BayerPattern.Rggb);
        NoiseMeasurement mixed = NoiseAnalysis.MeasurePair(imageA, imageB, region: roi);

        // 期待値: roi の画素だけを絶対座標の偶奇(=Bayerチャネル)で分け、
        // チャネル内分散を画素数重みでプールする(2パスで計算)
        var channelValues = new List<double>[4];
        for (int c = 0; c < 4; c++)
        {
            channelValues[c] = new List<double>();
        }

        var diffs = new List<double>();
        for (int y = roi.Y; y < roi.Y + roi.Height; y++)
        {
            for (int x = roi.X; x < roi.X + roi.Width; x++)
            {
                int i = y * width + x;
                channelValues[(y & 1) * 2 + (x & 1)].Add(a[i]);
                diffs.Add(a[i] - b[i]);
            }
        }

        double pooled = 0;
        foreach (List<double> values in channelValues.Where(values => values.Count > 0))
        {
            double channelMean = values.Average();
            pooled += values.Sum(v => (v - channelMean) * (v - channelMean));
        }

        double sigmaTotal = Math.Sqrt(pooled / roi.PixelCount);
        double diffMean = diffs.Average();
        double sigmaTemporal = Math.Sqrt(
            diffs.Sum(d => (d - diffMean) * (d - diffMean)) / diffs.Count / 2.0);
        double mean = channelValues.SelectMany(values => values).Average();

        Assert.Equal(roi.PixelCount, result.SampleCount);
        Assert.Equal(mixed.SampleCount, result.SampleCount);
        Assert.Equal(mean, result.Mean, 9);
        Assert.Equal(mixed.Mean, result.Mean, 9);
        Assert.Equal(sigmaTotal, result.SigmaTotal, 9);
        Assert.Equal(sigmaTemporal, result.SigmaTemporal, 9);
        Assert.Equal(
            Math.Sqrt(Math.Max(0, sigmaTotal * sigmaTotal - sigmaTemporal * sigmaTemporal)),
            result.SigmaFpn, 9);
    }

    [Fact]
    public void MeasurePair_SizeMismatch_Throws()
    {
        using RawImage a = TestImages.FromCodes(new ushort[16], 4, 4, bitDepth: 12);
        using RawImage b = TestImages.FromCodes(new ushort[8], 4, 2, bitDepth: 12);
        Assert.Throws<ArgumentException>(() => NoiseAnalysis.MeasurePair(a, b));
    }

    [Fact]
    public void MeasurePair_BitDepthMismatch_Throws()
    {
        // 12bit rawから保存した16bit TIFFを2枚目に指定するだけで成立する組み合わせ。
        // 従来は無警告で通り、σ_temporalが約11倍・σ_FPN=0・DRが約21dB低下していた。
        using RawImage a = TestImages.FromCodes(new ushort[16], 4, 4, bitDepth: 12);
        using RawImage b = TestImages.FromCodes(new ushort[16], 4, 4, bitDepth: 16);

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => NoiseAnalysis.MeasurePair(a, b));
        Assert.Contains("ビット深度", ex.Message);
    }

    [Fact]
    public void MeasureSingle_SaturationCodeAboveBitDepth_IsClamped()
    {
        // ファイル切替でビット深度が下がったとき、前の飽和コードが残っても
        // DRを過大評価しない(10bitに4095を指定 → 1023へクランプ)
        const int size = 8;
        var codes = new ushort[size * size];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(500 + (i % 2) * 20); // σ=10
        }

        using RawImage image = TestImages.FromCodes(codes, size, size, bitDepth: 10);
        NoiseMeasurement result = NoiseAnalysis.MeasureSingle(image, saturationCode: 4095);

        Assert.Equal(1023, result.SaturationCode, 6);
        Assert.Equal(20 * Math.Log10(1023 / 10.0), result.DynamicRangeTotalDb, 6);
    }
}
