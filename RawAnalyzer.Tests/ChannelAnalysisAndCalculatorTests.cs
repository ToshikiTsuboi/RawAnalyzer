using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class ChannelAnalysisTests
{
    [Theory]
    [InlineData(5, 5)]
    [InlineData(5, 4)]
    [InlineData(4, 5)]
    [InlineData(1, 1)]
    [InlineData(1, 4)]
    [InlineData(5, 1)]
    public void ComputeChannelAnalysis_OddSize_CountsAllPixels(int width, int height)
    {
        // 奇数サイズでも最終行/列が統計から脱落しないこと
        var codes = new ushort[width * height];
        long expectedSum = 0;
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(100 + i);
            expectedSum += 100 + i;
        }

        using RawImage image = TestImages.FromCodes(codes, width, height);
        ChannelAnalysisResult result = ImageAnalysis.ComputeChannelAnalysis(
            image, 0, BayerPattern.Rggb);

        Assert.Equal(width * height, result.Total.Statistics.SampleCount);
        Assert.Equal(
            (double)expectedSum / (width * height), result.Total.Statistics.Mean, 6);
        Assert.Equal(
            width * height, result.Channels.Sum(c => c.Statistics.SampleCount));
    }

    [Theory]
    [InlineData(BayerPattern.Rggb)]
    [InlineData(BayerPattern.Grbg)]
    public void ComputeChannelAnalysis_ConstantChannels_SeparatesExactly(BayerPattern pattern)
    {
        const int size = 8;
        ushort[] mosaic = ColorPipelineTests.BuildConstantMosaic(
            size, size, pattern, r: 1000, g: 2000, b: 3000);

        // Gr/Gbを区別するためGbだけ値を変える
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (BayerHelper.GetChannel(pattern, x, y) == BayerChannel.Gb)
                {
                    mosaic[y * size + x] = 2500;
                }
            }
        }

        using RawImage image = TestImages.FromCodes(mosaic, size, size);
        ChannelAnalysisResult result = ImageAnalysis.ComputeChannelAnalysis(image, 0, pattern);

        Assert.Equal(4, result.Channels.Count);
        ChannelHistogram r = result.Channels.First(c => c.Channel == BayerChannel.R);
        ChannelHistogram gr = result.Channels.First(c => c.Channel == BayerChannel.Gr);
        ChannelHistogram gb = result.Channels.First(c => c.Channel == BayerChannel.Gb);
        ChannelHistogram b = result.Channels.First(c => c.Channel == BayerChannel.B);

        Assert.Equal(1000, r.Statistics.Mean, 10);
        Assert.Equal(2000, gr.Statistics.Mean, 10);
        Assert.Equal(2500, gb.Statistics.Mean, 10);
        Assert.Equal(3000, b.Statistics.Mean, 10);
        Assert.Equal(16, r.Statistics.SampleCount);
        Assert.Equal(0, r.Statistics.Sigma, 10);

        // チャネルビンの合計 = 全体ビン
        Assert.Equal(16u, r.Bins[1000]);
        Assert.Equal(64, result.Total.SampleCount);
        Assert.Equal(16u, result.Total.Bins[2500]);
    }

    [Fact]
    public void ComputeChannelAnalysis_Roi_RestrictsToRegion()
    {
        const int size = 8;
        ushort[] mosaic = ColorPipelineTests.BuildConstantMosaic(
            size, size, BayerPattern.Rggb, 100, 200, 300);
        using RawImage image = TestImages.FromCodes(mosaic, size, size);

        ChannelAnalysisResult result = ImageAnalysis.ComputeChannelAnalysis(
            image, 0, BayerPattern.Rggb, new RegionOfInterest(2, 2, 4, 4));

        Assert.Equal(16, result.Total.SampleCount);
        Assert.Equal(4, result.Channels.First(c => c.Channel == BayerChannel.R)
            .Statistics.SampleCount);
    }

    [Fact]
    public void ComputeChannelAnalysis_OddAlignedRoi_CoversWholeRegion()
    {
        // 内側へ切り詰めると roi=(1,1,4,4) が16画素中4画素になり、
        // 各チャネル1サンプル(σ=0)という無意味な統計になっていた。
        // 外側スナップで全チャネルが複数サンプルを持つこと。
        const int size = 8;
        ushort[] mosaic = ColorPipelineTests.BuildConstantMosaic(
            size, size, BayerPattern.Rggb, 100, 200, 300);
        using RawImage image = TestImages.FromCodes(mosaic, size, size);

        ChannelAnalysisResult result = ImageAnalysis.ComputeChannelAnalysis(
            image, 0, BayerPattern.Rggb, new RegionOfInterest(1, 1, 4, 4));

        // x:0..5, y:0..5 の3x3ブロック = 36画素(各チャネル9サンプル)
        Assert.Equal(36, result.Total.SampleCount);
        foreach (ChannelHistogram channel in result.Channels)
        {
            Assert.Equal(9, channel.Statistics.SampleCount);
        }
    }

    [Fact]
    public void ComputeChannelAnalysis_LargeImage_SamplesAllChannelsEqually()
    {
        const int size = 64;
        ushort[] mosaic = ColorPipelineTests.BuildConstantMosaic(
            size, size, BayerPattern.Rggb, 100, 200, 300);
        using RawImage image = TestImages.FromCodes(mosaic, size, size);

        ChannelAnalysisResult result = ImageAnalysis.ComputeChannelAnalysis(
            image, 0, BayerPattern.Rggb, maxSamples: 400);

        Assert.True(result.Total.IsSampled);
        long rCount = result.Channels.First(c => c.Channel == BayerChannel.R).Statistics.SampleCount;
        long bCount = result.Channels.First(c => c.Channel == BayerChannel.B).Statistics.SampleCount;
        Assert.Equal(rCount, bCount);
        Assert.True(rCount > 0);
        Assert.Equal(100, result.Channels.First(c => c.Channel == BayerChannel.R).Statistics.Mean, 10);
    }

    [Fact]
    public void ComputeChannelAnalysis_NoBayer_ReturnsTotalOnly()
    {
        ushort[] codes = TestData.MakePattern(16 * 16, 12);
        using RawImage image = TestImages.FromCodes(codes, 16, 16, bitDepth: 12);

        ChannelAnalysisResult result = ImageAnalysis.ComputeChannelAnalysis(
            image, 0, BayerPattern.None);

        Assert.Empty(result.Channels);
        Assert.Equal(256, result.Total.SampleCount);
    }
}

public class ImageCalculatorTests
{
    [Fact]
    public void Subtract_ClampsAtZero()
    {
        ushort[] a = { 1000, 500, 100, 4095 };
        ushort[] b = { 300, 500, 200, 95 };
        using RawImage imageA = TestImages.FromCodes(
            a, 2, 2, bitDepth: 12, bayer: BayerPattern.Rggb);
        using RawImage imageB = TestImages.FromCodes(b, 2, 2, bitDepth: 12);

        using RawImage result = ImageCalculator.Apply(imageA, imageB, ImageOperation.Subtract);

        // 正規化16bit域(<<4)での減算
        Assert.Equal((1000 - 300) << 4, result.GetPixel(0, 0));
        Assert.Equal(0, result.GetPixel(1, 0));
        Assert.Equal(0, result.GetPixel(0, 1));            // 100-200 → クランプ0
        Assert.Equal((4095 - 95) << 4, result.GetPixel(1, 1));

        // 結果はA(source)のフォーマット(Bayer/ビット深度)を引き継ぐ
        Assert.Equal(BayerPattern.Rggb, result.Format.Bayer);
        Assert.Equal(12, result.Format.BitDepth);
    }

    [Fact]
    public void AbsoluteDifference_ReturnsMagnitude()
    {
        ushort[] a = { 1000, 100 };
        ushort[] b = { 300, 500 };
        using RawImage imageA = TestImages.FromCodes(a, 2, 1, bitDepth: 12);
        using RawImage imageB = TestImages.FromCodes(b, 2, 1, bitDepth: 12);

        using RawImage result = ImageCalculator.Apply(
            imageA, imageB, ImageOperation.AbsoluteDifference);

        Assert.Equal((1000 - 300) << 4, result.GetPixel(0, 0));
        Assert.Equal((500 - 100) << 4, result.GetPixel(1, 0));
    }

    [Fact]
    public void DivideGain_FlattensReferencePattern()
    {
        // A = B(同一シェーディング)なら結果は全画素 ≈ mean(B)
        ushort[] shading = { 1000, 2000, 3000, 4000 };
        using RawImage imageA = TestImages.FromCodes(shading, 2, 2, bitDepth: 12);
        using RawImage imageB = TestImages.FromCodes(shading, 2, 2, bitDepth: 12);

        using RawImage result = ImageCalculator.Apply(imageA, imageB, ImageOperation.DivideGain);

        int expected = (int)((1000 + 2000 + 3000 + 4000) / 4.0 * 16);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                Assert.InRange((int)result.GetPixel(x, y), expected - 16, expected + 16);
            }
        }
    }

    [Fact]
    public void Apply_SizeMismatch_Throws()
    {
        using RawImage imageA = TestImages.FromCodes(new ushort[4], 2, 2, bitDepth: 12);
        using RawImage imageB = TestImages.FromCodes(new ushort[2], 2, 1, bitDepth: 12);
        Assert.Throws<ArgumentException>(() =>
            ImageCalculator.Apply(imageA, imageB, ImageOperation.Subtract));
    }
}
