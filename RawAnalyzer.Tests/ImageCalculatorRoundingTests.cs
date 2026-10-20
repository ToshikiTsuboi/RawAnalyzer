using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 画像演算の結果を元の Nbit の code の格子(code&lt;&lt;(16−N))へ四捨五入して返すこと、ゲイン補正の参照平均を
/// 16bit 値のまま求めること(検証の指摘 processing-calc-1・r2conv-calc-sub-1・processing-calc-2)。
/// </summary>
public class ImageCalculatorRoundingTests
{
    private const int Size = 64;

    [Fact]
    public void DivideGain_12Bit_RoundsToCodeGrid_AndMeanMatchesIdeal()
    {
        // 12bit の A(code 300±30)を 12bit のフラット B(code 2000±200)でゲイン補正する。以前は16bit域の
        // 端数を持ったまま 12bit を名乗り、統計・raw 保存の >>4 で切り捨てられて平均が約 −0.47 code 偏った
        var random = new Random(1);
        ushort[] a = Codes(random, 300, 30);
        ushort[] b = Codes(random, 2000, 200);
        using RawImage imageA = TestImages.FromCodes(a, Size, Size, bitDepth: 12);
        using RawImage imageB = TestImages.FromCodes(b, Size, Size, bitDepth: 12);

        using RawImage result = ImageCalculator.Apply(imageA, imageB, ImageOperation.DivideGain);

        double meanB = b.Average(v => (double)v);
        double ideal = a.Select((v, i) => v * meanB / b[i]).Average();
        double mean = ImageAnalysis.ComputeStatistics(
            result, 0, new RegionOfInterest(0, 0, Size, Size)).Mean;
        Assert.Equal(12, result.Format.BitDepth);
        Assert.InRange(mean, ideal - 0.05, ideal + 0.05);
        AssertOnCodeGrid(result, 4);
        for (int i = 0; i < a.Length; i++)
        {
            // 各画素は 16bit 域の値を code へ四捨五入したもの
            double value16 = (a[i] << 4) * (meanB * 16) / (b[i] << 4);
            Assert.Equal((int)Math.Floor((value16 / 16) + 0.5), result.GetPixel(i % Size, i / Size) >> 4);
        }
    }

    [Theory]
    [InlineData(ImageOperation.Subtract)]
    [InlineData(ImageOperation.AbsoluteDifference)]
    public void SubtractWithOffGridReference_RoundsToCodeGrid(ImageOperation operation)
    {
        // 12bit の A から、外部ツールで平均した 16bit のマスターダーク(下位4bitに端数がある)を引く。
        // 以前は A の 12bit のまま端数が残り、統計・raw 保存で切り捨てられて平均が約 −0.44 code 偏った
        var random = new Random(2);
        ushort[] a = Codes(random, 1800, 50);
        var dark16 = new ushort[a.Length];
        for (int i = 0; i < dark16.Length; i++)
        {
            dark16[i] = (ushort)(1500 + random.Next(0, 160)); // code 93.75〜103.7(16倍で)
        }

        using RawImage imageA = TestImages.FromCodes(a, Size, Size, bitDepth: 12);
        using RawImage dark = TestImages.FromCodes(dark16, Size, Size, bitDepth: 16);

        using RawImage result = ImageCalculator.Apply(imageA, dark, operation);

        Assert.Equal(12, result.Format.BitDepth);
        AssertOnCodeGrid(result, 4);
        double exact = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int difference = (a[i] << 4) - dark16[i];
            int value16 = operation == ImageOperation.Subtract ? Math.Max(0, difference) : Math.Abs(difference);
            exact += value16 / 16.0;
            Assert.Equal((value16 + 8) >> 4, result.GetPixel(i % Size, i / Size) >> 4);
        }

        double mean = ImageAnalysis.ComputeStatistics(
            result, 0, new RegionOfInterest(0, 0, Size, Size)).Mean;
        Assert.InRange(mean, (exact / a.Length) - 0.05, (exact / a.Length) + 0.05);
    }

    [Fact]
    public void Subtract_MaxCodeStaysWithinBitDepth()
    {
        // 四捨五入で最大コードを超えない(12bit の 65535 相当は 4095 で止まる)
        using RawImage imageA = TestImages.FromCodes([4095, 4095], 2, 1, bitDepth: 12);
        using RawImage dark = TestImages.FromCodes([0, 7], 2, 1, bitDepth: 16);

        using RawImage result = ImageCalculator.Apply(imageA, dark, ImageOperation.Subtract);

        Assert.Equal(4095 << 4, result.GetPixel(0, 0));
        Assert.Equal(4095 << 4, result.GetPixel(1, 0)); // 65520 − 7 = 65513 → code 4094.6 → 4095
    }

    [Fact]
    public void DivideGain_ReferenceMeanIsSixteenBitMean_EvenIfLowBitsAreSet()
    {
        // 参照平均を「code 平均×2^shift」で求めると、下位ビットの立った 8bit 参照(以前の WIC の v×257 など)で
        // mean(B) が 256/257 倍になり、一様なフラットで補正しても値が変わった(12bit code 3000 → 2988)
        var flat = new ushort[Size * Size];
        Array.Fill(flat, (ushort)(200 * 257));
        using RawImage reference = RawImage.FromPixels(
            new RawFormat { Width = Size, Height = Size, BitDepth = 8 }, flat);
        using RawImage imageA = TestImages.FromCodes(
            Enumerable.Repeat((ushort)3000, Size * Size).ToArray(), Size, Size, bitDepth: 12);

        using RawImage result = ImageCalculator.Apply(imageA, reference, ImageOperation.DivideGain);

        Assert.All(Pixels(result), v => Assert.Equal(3000 << 4, v));
    }

    private static ushort[] Codes(Random random, int mean, int spread)
    {
        var codes = new ushort[Size * Size];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(mean + random.Next(-spread, spread + 1));
        }

        return codes;
    }

    private static IEnumerable<ushort> Pixels(RawImage image)
    {
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                yield return image.GetPixel(x, y);
            }
        }
    }

    private static void AssertOnCodeGrid(RawImage image, int shift)
    {
        Assert.All(Pixels(image), v => Assert.Equal(0, v & ((1 << shift) - 1)));
    }
}
