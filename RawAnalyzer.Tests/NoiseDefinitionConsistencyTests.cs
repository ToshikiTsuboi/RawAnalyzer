using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// ノイズ測定の定義(評価画素数・平均・σ_total・σ_temporal = σ(A−B)/√2・
/// σ_FPN = √(σ_total² − σ_temporal²)・飽和信号レベル)が、格子版
/// (<see cref="ChannelRegionAnalysis"/>)と矩形版(<see cref="NoiseAnalysis"/>)で一致すること。
/// </summary>
/// <remarks>
/// 同じ画素集合を与えるため、非Bayerの画像 X の各画素を別の画像 Y の2画素刻みの格子
/// (X0 + 2i, Y0 + 2j)へ埋め込み、格子の間は極端な値(0 と最大値)で埋める。
/// Y の格子(格子版)と X の矩形(矩形版)は同じ画素の集合なので、結果は完全に一致するはず
/// (和・二乗和は整数で厳密に集計されるため、定義が同じなら浮動小数点の値までそろう)。
/// </remarks>
public class NoiseDefinitionConsistencyTests
{
    private const int Width = 5;
    private const int Height = 4;

    public static IEnumerable<object[]> Conditions()
    {
        // ビット深度, 格子の原点X, 原点Y, 飽和コード
        yield return new object[] { 8, 0, 0, 0.0 };
        yield return new object[] { 10, 1, 1, 1000.0 };
        yield return new object[] { 12, 1, 0, double.NaN };
        yield return new object[] { 14, 0, 1, 1e9 };   // ビット深度の最大値へクランプされる
        yield return new object[] { 16, 1, 1, -5.0 };  // 0以下はビット深度の最大値
    }

    [Theory]
    [MemberData(nameof(Conditions))]
    public void SamePixelSet_EveryRegion_MatchesExactly(
        int bitDepth, int originX, int originY, double saturationCode)
    {
        // A は2フレーム(フレーム1を測る)、B は1フレーム
        ushort[] a = RandomCodes(Width * Height * 2, bitDepth, seed: bitDepth);
        ushort[] b = RandomCodes(Width * Height, bitDepth, seed: bitDepth + 100);
        using RawImage rectA = TestImages.FromCodes(a, new RawFormat
        {
            Width = Width, Height = Height, BitDepth = bitDepth, FrameCount = 2,
        });
        using RawImage rectB = TestImages.FromCodes(b, Width, Height, bitDepth);
        using RawImage latticeA = Embed(a, frames: 2, bitDepth, originX, originY);
        using RawImage latticeB = Embed(b, frames: 1, bitDepth, originX, originY);

        // 領域なし(画像全体)
        var whole = new ChannelRegion(originX, originY, Width, Height);
        Assert.Equal(
            NoiseAnalysis.MeasurePair(rectA, rectB, 1, 0, null, BayerPattern.None, saturationCode),
            ChannelRegionAnalysis.MeasurePair(latticeA, latticeB, 1, 0, whole, saturationCode));

        // すべての矩形(1画素・1行・1列を含む)
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                for (int h = 1; y + h <= Height; h++)
                {
                    for (int w = 1; x + w <= Width; w++)
                    {
                        var roi = new RegionOfInterest(x, y, w, h);
                        var region = new ChannelRegion(originX + 2 * x, originY + 2 * y, w, h);

                        Assert.Equal(
                            NoiseAnalysis.MeasureSingle(
                                rectA, 1, roi, BayerPattern.None, saturationCode),
                            ChannelRegionAnalysis.MeasureSingle(
                                latticeA, 1, region, saturationCode));
                        Assert.Equal(
                            NoiseAnalysis.MeasurePair(
                                rectA, rectB, 1, 0, roi, BayerPattern.None, saturationCode),
                            ChannelRegionAnalysis.MeasurePair(
                                latticeA, latticeB, 1, 0, region, saturationCode));
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(2, 1, 0, 3)]  // 幅0(高さはある)
    [InlineData(1, 2, 3, 0)]  // 高さ0
    public void EmptyPixelSet_MatchesLattice(int x, int y, int width, int height)
    {
        // 画素0個の集合。格子版はゼロの測定値を返す。矩形版も同じ値を返すこと
        // (矩形版の2枚測定は、幅0の行を差分の走査で読みに行き例外になっていた)
        ushort[] a = RandomCodes(Width * Height, 12, seed: 1);
        ushort[] b = RandomCodes(Width * Height, 12, seed: 2);
        using RawImage rectA = TestImages.FromCodes(a, Width, Height, 12);
        using RawImage rectB = TestImages.FromCodes(b, Width, Height, 12);
        using RawImage latticeA = Embed(a, frames: 1, 12, 1, 1);
        using RawImage latticeB = Embed(b, frames: 1, 12, 1, 1);
        var roi = new RegionOfInterest(x, y, width, height);
        var region = new ChannelRegion(1 + 2 * x, 1 + 2 * y, width, height);

        NoiseMeasurement expectedPair = ChannelRegionAnalysis.MeasurePair(
            latticeA, latticeB, 0, 0, region);
        Assert.Equal(new NoiseMeasurement(0, 0, 0, 0, 0, 4095), expectedPair);
        Assert.Equal(expectedPair, NoiseAnalysis.MeasurePair(rectA, rectB, region: roi));
        Assert.Equal(
            expectedPair,
            NoiseAnalysis.MeasurePair(rectA, rectB, region: roi, pattern: BayerPattern.Rggb));

        NoiseMeasurement expectedSingle = ChannelRegionAnalysis.MeasureSingle(latticeA, 0, region);
        Assert.Equal(expectedSingle, NoiseAnalysis.MeasureSingle(rectA, region: roi));
        Assert.Equal(
            expectedSingle,
            NoiseAnalysis.MeasureSingle(rectA, region: roi, pattern: BayerPattern.Rggb));

        // 画像の外の矩形はクランプで画素0個になる(格子版は範囲外を受け付けないので矩形版だけ)
        var outside = new RegionOfInterest(Width + 3, 0, 2, Height);
        Assert.Equal(expectedPair, NoiseAnalysis.MeasurePair(rectA, rectB, region: outside));
    }

    [Theory]
    [InlineData(0, 0, 8, 6)]
    [InlineData(1, 1, 5, 4)]
    [InlineData(3, 2, 1, 3)]  // 1列(2チャネルだけを含む)
    [InlineData(2, 5, 4, 1)]  // 1行
    public void BayerRectangle_PoolsTheVariancesOfItsChannelLattices(
        int x, int y, int width, int height)
    {
        // 矩形版のBayer空間統計は、ROIを絶対座標の偶奇(=チャネル)で分けたチャネル内分散の
        // 画素数重みプール √(Σ nᵢσᵢ² / Σ nᵢ)。ROI内の各チャネルの格子を格子版で測った
        // 値から合成しても同じになること(分散の合成の定義がそろっていること)
        const int imageWidth = 8;
        const int imageHeight = 6;
        var codes = new ushort[imageWidth * imageHeight];
        var random = new Random(7);
        for (int py = 0; py < imageHeight; py++)
        {
            for (int px = 0; px < imageWidth; px++)
            {
                int channelOffset = ((py & 1) * 2 + (px & 1)) * 700;
                codes[py * imageWidth + px] = (ushort)(500 + channelOffset + random.Next(200));
            }
        }

        using RawImage image = TestImages.FromCodes(
            codes, imageWidth, imageHeight, 12, BayerPattern.Rggb);
        var roi = new RegionOfInterest(x, y, width, height);

        NoiseMeasurement rect = NoiseAnalysis.MeasureSingle(
            image, 0, roi, BayerPattern.Rggb);

        long count = 0;
        double sum = 0;
        double pooled = 0;
        for (int parityY = 0; parityY < 2; parityY++)
        {
            for (int parityX = 0; parityX < 2; parityX++)
            {
                // ROI内で座標の偶奇が (parityX, parityY) になる最初の画素から2画素刻み
                int startX = x + ((parityX - x) & 1);
                int startY = y + ((parityY - y) & 1);
                int columns = startX < x + width ? (x + width - startX + 1) / 2 : 0;
                int rows = startY < y + height ? (y + height - startY + 1) / 2 : 0;
                if (columns == 0 || rows == 0)
                {
                    continue;
                }

                NoiseMeasurement channel = ChannelRegionAnalysis.MeasureSingle(
                    image, 0, new ChannelRegion(startX, startY, columns, rows));
                count += channel.SampleCount;
                sum += channel.Mean * channel.SampleCount;
                pooled += channel.SigmaTotal * channel.SigmaTotal * channel.SampleCount;
            }
        }

        Assert.Equal(roi.PixelCount, count);
        Assert.Equal(count, rect.SampleCount);
        Assert.Equal(sum / count, rect.Mean, 9);
        Assert.Equal(Math.Sqrt(pooled / count), rect.SigmaTotal, 9);
    }

    private static ushort[] RandomCodes(int count, int bitDepth, int seed)
    {
        var random = new Random(seed);
        var codes = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            codes[i] = (ushort)random.Next(1 << bitDepth);
        }

        // 最小値・最大値を必ず含める(桁あふれ・丸めの境界)
        codes[0] = 0;
        codes[^1] = (ushort)((1 << bitDepth) - 1);
        return codes;
    }

    /// <summary>
    /// Width×Height の各フレームを、(originX + 2i, originY + 2j) の格子に埋め込んだ画像を作る。
    /// 格子以外の画素は 0 と最大値の交互で埋め、格子外を1画素でも読めば結果が変わるようにする。
    /// </summary>
    private static RawImage Embed(ushort[] codes, int frames, int bitDepth, int originX, int originY)
    {
        int width = originX + 2 * Width;
        int height = originY + 2 * Height;
        ushort max = (ushort)((1 << bitDepth) - 1);
        var embedded = new ushort[width * height * frames];
        for (int f = 0; f < frames; f++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    embedded[(f * height + y) * width + x] = (x + y) % 2 == 0 ? max : (ushort)0;
                }
            }

            for (int j = 0; j < Height; j++)
            {
                for (int i = 0; i < Width; i++)
                {
                    embedded[(f * height + originY + 2 * j) * width + originX + 2 * i] =
                        codes[(f * Height + j) * Width + i];
                }
            }
        }

        return TestImages.FromCodes(embedded, new RawFormat
        {
            Width = width, Height = height, BitDepth = bitDepth, FrameCount = frames,
        });
    }
}
