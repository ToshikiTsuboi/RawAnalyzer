using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class NoiseAnalysisTests
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
    public void MeasurePair_IdenticalFrames_HasZeroTemporalNoise()
    {
        // 同一データ = 差分0 → 時間ノイズ0、σはすべてFPN
        const int size = 16;
        var codes = new ushort[size * size];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(1000 + (i % 4) * 10); // 固定パターン
        }

        using RawImage a = LoadImage(codes, size, size);
        using RawImage b = LoadImage(codes, size, size);

        NoiseMeasurement result = NoiseAnalysis.MeasurePair(a, b);

        Assert.Equal(0, result.SigmaTemporal, 10);
        Assert.Equal(result.SigmaTotal, result.SigmaFpn, 6);
        Assert.True(result.SigmaTotal > 0);
    }

    [Fact]
    public void MeasurePair_KnownDifference_DividesSigmaBySqrt2()
    {
        // 差分が ±10 で半々 → σ_diff = 10 → σ_temporal = 10/√2
        const int size = 16;
        var a = new ushort[size * size];
        var b = new ushort[size * size];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = 1000;
            b[i] = (ushort)(i % 2 == 0 ? 990 : 1010);
        }

        using RawImage imageA = LoadImage(a, size, size);
        using RawImage imageB = LoadImage(b, size, size);

        NoiseMeasurement result = NoiseAnalysis.MeasurePair(imageA, imageB);

        Assert.Equal(10.0 / Math.Sqrt(2), result.SigmaTemporal, 8);
    }

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

        using RawImage imageA = LoadImage(a, width, height);
        using RawImage imageB = LoadImage(b, width, height);

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

        using RawImage imageA = LoadImage(a, size, size);
        using RawImage imageB = LoadImage(b, size, size);

        NoiseMeasurement result = NoiseAnalysis.MeasurePair(imageA, imageB);

        double sigmaTemporal = 10.0 / Math.Sqrt(2);
        double expectedDb = 20 * Math.Log10(4095 / sigmaTemporal);
        Assert.Equal(expectedDb, result.DynamicRangeTemporalDb, 6);
        Assert.Equal(Math.Log2(4095 / sigmaTemporal), result.DynamicRangeTemporalStops, 6);

        // 6.02dB = 1stop の関係
        Assert.Equal(
            result.DynamicRangeTemporalDb / 6.0206,
            result.DynamicRangeTemporalStops, 2);
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

        using RawImage imageA = LoadImage(a, size, size);
        using RawImage imageB = LoadImage(b, size, size);

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

        using RawImage imageA = LoadImage(a, size, size);
        using RawImage imageB = LoadImage(b, size, size);

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

        using RawImage image = LoadImage(codes, size, size);
        NoiseMeasurement result = NoiseAnalysis.MeasureSingle(image);

        Assert.Equal(10, result.SigmaTotal, 6);
        Assert.True(double.IsNaN(result.SigmaTemporal));
        Assert.True(double.IsNaN(result.SigmaFpn));
        Assert.True(double.IsNaN(result.DynamicRangeTemporalDb));
        Assert.Equal(20 * Math.Log10(4095 / 10.0), result.DynamicRangeTotalDb, 6);
    }

    [Fact]
    public void MeasurePair_SizeMismatch_Throws()
    {
        using RawImage a = LoadImage(new ushort[16], 4, 4);
        using RawImage b = LoadImage(new ushort[8], 4, 2);
        Assert.Throws<ArgumentException>(() => NoiseAnalysis.MeasurePair(a, b));
    }

    [Fact]
    public void MeasurePair_BitDepthMismatch_Throws()
    {
        // 12bit rawから保存した16bit TIFFを2枚目に指定するだけで成立する組み合わせ。
        // 従来は無警告で通り、σ_temporalが約11倍・σ_FPN=0・DRが約21dB低下していた。
        using RawImage a = LoadImage(new ushort[16], 4, 4, bitDepth: 12);
        using RawImage b = LoadImage(new ushort[16], 4, 4, bitDepth: 16);

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

        using RawImage image = LoadImage(codes, size, size, bitDepth: 10);
        NoiseMeasurement result = NoiseAnalysis.MeasureSingle(image, saturationCode: 4095);

        Assert.Equal(1023, result.SaturationCode, 6);
        Assert.Equal(20 * Math.Log10(1023 / 10.0), result.DynamicRangeTotalDb, 6);
    }
}
