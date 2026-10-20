using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 16bit にする処理(平均ビニング・フィルタ・HDR 合成)が、Nbit 素材の最大 code を 65535 へ写すこと
/// (検証の指摘 r2conv-sat16-1)。以前は 12bit の白飛びが 65520 で止まり、16bit の結果では飽和率 0%、
/// カラー現像の白飛びの判定からも漏れてマゼンタに着色した。
/// </summary>
public class SixteenBitSaturationTests
{
    private const int Size = 8;

    /// <summary>行の和が1(白→白)で非対角が負の、典型的なカメラRGB→表示RGBの行列。</summary>
    private static readonly ColorMatrix TypicalCcm = new(
        1.6, -0.4, -0.2,
        -0.2, 1.4, -0.2,
        0.0, -0.5, 1.5);

    public static TheoryData<string> Processes() => new() { "binning", "box", "gaussian", "median", "maximum" };

    [Theory]
    [MemberData(nameof(Processes))]
    public void SaturatedTwelveBit_StaysSaturatedAfterSixteenBitProcessing(string process)
    {
        // 上半分が白飛び(code 4095)、下半分が code 100 の 12bit Bayer
        var codes = new ushort[Size * Size];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = i < codes.Length / 2 ? (ushort)4095 : (ushort)100;
        }

        using RawImage source = TestImages.FromCodes(codes, Size, Size, bitDepth: 12, bayer: BayerPattern.Rggb);
        using RawImage result = Process(source, process);

        Assert.Equal(16, result.Format.BitDepth);
        Assert.Equal(65535, result.GetPixel(0, 0));

        // 暗部は code×65535/4095(12bit の code 100 は 1600)
        Assert.Equal(1600, result.GetPixel(0, result.Height - 1));

        HistogramMetrics metrics = ImageAnalysis.ComputeHistogramMetrics(
            ImageAnalysis.ComputeHistogram(result, 0));
        Assert.InRange(metrics.SaturatedPercent, 25, 100);

        // 白点は処理前の白(最大コード)を写した 65535。白飛びはカラー現像で中性のまま
        var develop = DevelopLuts.Create(new DevelopParameters(
            GainR: 2.0, GainB: 1.5, Gamma: 1.0, Matrix: TypicalCcm, WhitePoint: 65535, SourceBitDepth: 16));
        develop.Convert(result.GetPixel(0, 0), result.GetPixel(1, 0), result.GetPixel(1, 1),
            out byte r, out byte g, out byte b);
        Assert.Equal((255, 255, 255), (r, g, b));
    }

    [Fact]
    public void SaturatedEightBitColor_StaysSaturatedAfterBinning()
    {
        var rgb = new ushort[Size * Size * 3];
        Array.Fill(rgb, (ushort)(255 << 8));
        ColorImage color = ColorImage.FromInterleaved(Size, Size, 8, rgb);

        ColorImage binned = ImageBinning.Apply(color, 2);

        binned.GetPixel(0, 0, out ushort r, out ushort g, out ushort b);
        Assert.Equal((65535, 65535, 65535), (r, g, b));
    }

    [Fact]
    public void HdrMerge_SaturatedInAllExposures_IsFullScale()
    {
        // 12bit 2段で長秒・短秒とも飽和した画素は、合成域のフルスケール(16bit の 65535)になる。以前は
        // フルスケールを (65535−黒)×露光比 にしており、12bit の最大 65520 から作った合成値が届かず 65520 だった
        var format = new RawFormat { Width = 2, Height = 2, BitDepth = 12 };
        using RawImage longFrame = TestImages.FromCodes([4095, 4095, 1000, 4095], format);
        using RawImage shortFrame = TestImages.FromCodes([4095, 4095, 62, 4095], format);

        HdrImage merged = HdrMerger.Merge([longFrame, shortFrame], new HdrMergeParameters(16, 64 << 4));
        using RawImage quantized = merged.ToRawImage16();

        Assert.Equal(((65520f - 1024) * 16), merged.FullScale);
        Assert.Equal(65535, quantized.GetPixel(0, 0));
        HistogramMetrics metrics = ImageAnalysis.ComputeHistogramMetrics(
            ImageAnalysis.ComputeHistogram(quantized, 0));
        Assert.Equal(75, metrics.SaturatedPercent, 6);
    }

    [Fact]
    public void SixteenBitSource_IsUnchangedByScaling()
    {
        // 16bit の素材は最大 code が 65535 なので値を変えない
        var codes = new ushort[Size * Size];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = i < codes.Length / 2 ? (ushort)65535 : (ushort)30001;
        }

        using RawImage source = TestImages.FromCodes(codes, Size, Size);

        using RawImage filtered = ImageFilters.Apply(source, new ImageFilterOptions(ImageFilterKind.Mean));
        using RawImage binned = ImageBinning.Apply(source, 2);

        Assert.Equal(65535, filtered.GetPixel(0, 0));
        Assert.Equal(30001, filtered.GetPixel(0, Size - 1));
        Assert.Equal(65535, binned.GetPixel(0, 0));
        Assert.Equal(30001, binned.GetPixel(0, binned.Height - 1));
    }

    private static RawImage Process(RawImage source, string process) => process switch
    {
        "binning" => ImageBinning.Apply(source, 2),
        "box" => ImageFilters.Apply(source, new ImageFilterOptions(ImageFilterKind.Mean)),
        "gaussian" => ImageFilters.Apply(source, new ImageFilterOptions(ImageFilterKind.Gaussian)),
        "median" => ImageFilters.Apply(source, new ImageFilterOptions(ImageFilterKind.Median)),
        _ => ImageFilters.Apply(source, new ImageFilterOptions(ImageFilterKind.Maximum)),
    };
}
