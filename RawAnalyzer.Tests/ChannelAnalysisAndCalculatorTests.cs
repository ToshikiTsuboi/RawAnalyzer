using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class ChannelAnalysisTests
{
    [Theory]
    [InlineData(5, 5)]
    [InlineData(5, 4)]
    [InlineData(4, 5)]
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

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void ComputeChannelAnalysis_Roi_CountsExactlyTheRoi(int roiX, int roiY)
    {
        // ROIの16画素だけを絶対座標の偶奇でチャネルへ振り分け、ROIの外(65535)は数えない。
        // 奇数座標を境界に持つ roi=(1,1,4,4) は、内側へ切り詰めると4画素(各チャネル1サンプル・σ=0)、
        // 以前のように外側の2x2境界へ広げると36画素になり、ROIの外を集計して同時に表示するROI統計と
        // 食い違っていた。(2,2) は偶数で0でないオフセット(ブロックの開始をROIの左上へずらす)
        var roi = new RegionOfInterest(roiX, roiY, 4, 4);
        using RawImage image = TestImages.FromCodes(MosaicInsideRoi(8, roi), 8, 8);

        ChannelAnalysisResult result = ImageAnalysis.ComputeChannelAnalysis(
            image, 0, BayerPattern.Rggb, roi);

        Assert.Equal(16, result.Total.SampleCount);
        Assert.False(result.Total.IsSampled);
        Assert.Equal(300, result.Total.Statistics.Max);
        AssertChannelsSeparated(result, expectedCount: 4);
    }

    [Fact]
    public void ComputeChannelAnalysis_SampledOddAlignedRoi_StaysInsideRoi()
    {
        // 大きなROIは2x2ブロック単位で間引く(アプリでは1千万画素超のROI)。間引いてもROIの外は数えず、
        // 全チャネルを含めて各チャネルの値を取り違えない
        var roi = new RegionOfInterest(1, 3, 61, 59);
        using RawImage image = TestImages.FromCodes(MosaicInsideRoi(64, roi), 64, 64);

        ChannelAnalysisResult result = ImageAnalysis.ComputeChannelAnalysis(
            image, 0, BayerPattern.Rggb, roi, maxSamples: 400);

        Assert.True(result.Total.IsSampled);
        Assert.True(result.Total.SampleCount < roi.PixelCount);
        Assert.Equal(300, result.Total.Statistics.Max);
        AssertChannelsSeparated(result, expectedCount: null);
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

    /// <summary>
    /// ROIの中は RGGB の R=100 / Gr=200 / Gb=250 / B=300、外は65535 のモザイク。
    /// ROIの外が1画素でも混ざれば最大値に、チャネルを取り違えれば平均・σに出る。
    /// </summary>
    private static ushort[] MosaicInsideRoi(int size, RegionOfInterest roi)
    {
        var codes = new ushort[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool inside = x >= roi.X && x < roi.X + roi.Width
                    && y >= roi.Y && y < roi.Y + roi.Height;
                codes[y * size + x] = inside
                    ? ChannelValue(BayerHelper.GetChannel(BayerPattern.Rggb, x, y))
                    : ushort.MaxValue;
            }
        }

        return codes;
    }

    private static ushort ChannelValue(BayerChannel channel) => channel switch
    {
        BayerChannel.R => 100,
        BayerChannel.Gr => 200,
        BayerChannel.Gb => 250,
        _ => 300,
    };

    private static void AssertChannelsSeparated(ChannelAnalysisResult result, long? expectedCount)
    {
        Assert.Equal(4, result.Channels.Count);
        foreach (ChannelHistogram channel in result.Channels)
        {
            Assert.Equal(ChannelValue(channel.Channel), channel.Statistics.Mean, 10);
            Assert.Equal(0, channel.Statistics.Sigma, 10);
            Assert.True(channel.Statistics.SampleCount > 0);
            if (expectedCount is { } count)
            {
                Assert.Equal(count, channel.Statistics.SampleCount);
            }
        }
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
    public void Apply_WithPattern_ResultCarriesGivenBayer()
    {
        // RGGBとして読み込んだ後、右パネルでBGGRへ変更してから演算する
        // (パネルの変更は画像の Format には入らない)。読込時のRGGBが結果へ複製され、
        // 採用時に最新のBayer指定を上書きしていた(レビュー指摘 #12)
        ushort[] a = { 1000, 500, 100, 4095 };
        ushort[] b = { 300, 500, 200, 95 };
        using RawImage imageA = TestImages.FromCodes(
            a, 2, 2, bitDepth: 12, bayer: BayerPattern.Rggb);
        using RawImage imageB = TestImages.FromCodes(b, 2, 2, bitDepth: 12);

        using RawImage result = ImageCalculator.Apply(
            imageA, imageB, ImageOperation.Subtract, pattern: BayerPattern.Bggr);

        Assert.Equal(BayerPattern.Bggr, result.Format.Bayer);
        Assert.Equal(12, result.Format.BitDepth);
        Assert.Equal((1000 - 300) << 4, result.GetPixel(0, 0)); // 画素演算はパターンに依らない
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

    [Theory]
    [InlineData(HdrMode.LineInterleaved, 1)]
    [InlineData(HdrMode.LineInterleaved, 3)]  // 行交互の連続撮影の1フレームも、1フレームに全露光を含む
    [InlineData(HdrMode.Auto, 1)]             // Auto でフレーム数1は行交互
    public void LineInterleavedHdr_KeepsHdrLayout_SoItCanBeSplitAfterDarkSubtraction(HdrMode hdr, int frames)
    {
        // 行交互HDRの raw をダーク減算してから HDR 合成する手順。演算は画素ごとで行の並びを変えないのに、
        // 以前は結果を常に Hdr=None にしたため、減算した画像を HDR として分割・合成できなかった
        // (HDR 方式は読み込みダイアログでしか指定できず、開き直すと減算が消える)
        RawFormat format = new()
        {
            Width = 4, Height = 8, BitDepth = 12, Bayer = BayerPattern.Rggb, FrameCount = frames,
            Hdr = hdr, HdrStages = 2, ExposureRatio = 8, HdrLineBlock = 2, HdrRowOffset = 0,
        };
        ushort[] codes = Enumerable.Range(0, 4 * 8 * frames).Select(i => (ushort)(200 + (i % 50))).ToArray();
        using RawImage imageA = TestImages.FromCodes(codes, format);
        using RawImage dark = TestImages.FromCodes(
            Enumerable.Repeat((ushort)64, 4 * 8).ToArray(), format with { FrameCount = 1 });

        using RawImage result = ImageCalculator.Apply(imageA, dark, ImageOperation.Subtract, frame: frames - 1);

        Assert.Equal(1, result.FrameCount);
        Assert.Equal(hdr, result.Format.Hdr);
        Assert.Equal(format.HdrStages, result.Format.HdrStages);
        Assert.Equal(format.ExposureRatio, result.Format.ExposureRatio);
        Assert.Equal(format.HdrLineBlock, result.Format.HdrLineBlock);
        IReadOnlyList<RawImage> stages = HdrSplitter.Split(result);
        Assert.Equal(2, stages.Count);
        foreach (RawImage stage in stages)
        {
            stage.Dispose();
        }
    }

    [Fact]
    public void FrameSequentialHdr_ResultIsSingleExposureWithoutHdr()
    {
        // フレーム連結は1フレーム=1露光。表示中の1フレームだけを演算した結果は HDR ではない(従来どおり)
        RawFormat format = new()
        {
            Width = 4, Height = 4, BitDepth = 12, FrameCount = 2, Hdr = HdrMode.FrameSequential, HdrStages = 2,
        };
        using RawImage imageA = TestImages.FromCodes(new ushort[4 * 4 * 2], format);
        using RawImage dark = TestImages.FromCodes(new ushort[4 * 4], format with { FrameCount = 1, Hdr = HdrMode.None });

        using RawImage result = ImageCalculator.Apply(imageA, dark, ImageOperation.Subtract, frame: 1);

        Assert.Equal(HdrMode.None, result.Format.Hdr);
        Assert.Equal(1, result.FrameCount);
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
