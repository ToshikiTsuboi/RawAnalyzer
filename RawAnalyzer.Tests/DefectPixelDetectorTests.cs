using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class DefectPixelDetectorTests
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

    /// <summary>ほぼフラット(±1のディザ)な画面に欠陥を埋め込む。</summary>
    private static ushort[] MakeFlatWithDefects(
        int width, int height, ushort baseCode, params (int X, int Y, ushort Code)[] defects)
    {
        var codes = new ushort[width * height];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(baseCode + (i % 2)); // σ>0にするためのディザ
        }

        foreach ((int x, int y, ushort code) in defects)
        {
            codes[y * width + x] = code;
        }

        return codes;
    }

    /// <summary>RGGBのチャネル別平均を持つフラットフィールドを作る。</summary>
    private static ushort[] MakeBayerFlat(
        int width, int height, ushort r, ushort g, ushort b,
        params (int X, int Y, ushort Code)[] defects)
    {
        var codes = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ushort baseCode = (y & 1) == 0
                    ? ((x & 1) == 0 ? r : g)
                    : ((x & 1) == 0 ? g : b);
                codes[y * width + x] = (ushort)(baseCode + ((x + y) % 2 == 0 ? 0 : 1));
            }
        }

        foreach ((int x, int y, ushort code) in defects)
        {
            codes[y * width + x] = code;
        }

        return codes;
    }

    [Fact]
    public void Detect_BayerFlatField_FindsDefectsPerChannel()
    {
        // R=1000/G=2000/B=1500の感度差があるフラットフィールド。
        // 混合統計だと σ≈410 で閾値が値域外に出て、完全な黒点(0)すら検出できない
        ushort[] codes = MakeBayerFlat(
            64, 64, 1000, 2000, 1500,
            (10, 10, 4095),   // R位置の白点
            (21, 20, 0),      // Gr位置の黒点
            (33, 33, 0));     // B位置の黒点
        using RawImage image = LoadImage(codes, 64, 64);

        DefectDetectionResult result = DefectPixelDetector.Detect(
            image, pattern: BayerPattern.Rggb);

        Assert.Equal(1, result.HotCount);
        Assert.Equal(2, result.DeadCount);
        Assert.Contains(result.Defects, d => d is { X: 10, Y: 10, Type: DefectType.Hot });
        Assert.Contains(result.Defects, d => d is { X: 21, Y: 20, Type: DefectType.Dead });
        Assert.Contains(result.Defects, d => d is { X: 33, Y: 33, Type: DefectType.Dead });

        // チャネル別閾値が返り、スカラー閾値はNaN(参照させない)
        Assert.Equal(4, result.ChannelThresholds.Count);
        Assert.True(double.IsNaN(result.HotThreshold));
        DefectChannelThreshold rT = result.ChannelThresholds.First(t => t.Channel == BayerChannel.R);
        Assert.InRange(rT.Mean, 995, 1010);
    }

    [Fact]
    public void Detect_HotAndDeadPixels_FindsBoth()
    {
        ushort[] codes = MakeFlatWithDefects(32, 32, 1000, (5, 7, 4000), (20, 15, 10));
        using RawImage image = LoadImage(codes, 32, 32);

        DefectDetectionResult result = DefectPixelDetector.Detect(image, sigmaFactor: 6.0);

        Assert.Equal(2, result.Defects.Count);
        Assert.Contains(new DefectPixel(5, 7, 4000, DefectType.Hot), result.Defects);
        Assert.Contains(new DefectPixel(20, 15, 10, DefectType.Dead), result.Defects);
        Assert.Equal(1, result.HotCount);
        Assert.Equal(1, result.DeadCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Detect_HotOnly_IgnoresDeadPixels()
    {
        ushort[] codes = MakeFlatWithDefects(16, 16, 1000, (3, 3, 4000), (8, 8, 10));
        using RawImage image = LoadImage(codes, 16, 16);

        DefectDetectionResult result = DefectPixelDetector.Detect(
            image, detectHot: true, detectDead: false);

        Assert.Single(result.Defects);
        Assert.Equal(DefectType.Hot, result.Defects[0].Type);
    }

    [Fact]
    public void Detect_HighHitRate_RespectsMaxResultsWithoutHoarding()
    {
        // ヒット率が高い画像では、スレッドローカルListを最後まで貯めると
        // (ワーカ数 × 全ヒット数)が同時生存してOOMになる。
        // 途中でグローバルへ吸い上げつつ、上限どおりに打ち切ることを確認する。
        const int size = 256;
        var codes = new ushort[size * size];
        for (int i = 0; i < codes.Length; i++)
        {
            // 半数が極端に高い = 全体のσに対して片側が必ず閾値を超える構成
            codes[i] = (ushort)((i & 1) == 0 ? 100 : 4000);
        }

        using RawImage image = LoadImage(codes, size, size);

        DefectDetectionResult result = DefectPixelDetector.Detect(
            image, sigmaFactor: 0.5, maxResults: 1000);

        Assert.True(result.Truncated, "上限で打ち切られること");
        Assert.Equal(1000, result.Defects.Count);

        // 打ち切っても行→列の順序は保たれる
        for (int i = 1; i < result.Defects.Count; i++)
        {
            DefectPixel previous = result.Defects[i - 1];
            DefectPixel current = result.Defects[i];
            Assert.True(
                current.Y > previous.Y || (current.Y == previous.Y && current.X > previous.X),
                $"順序が崩れている: ({previous.X},{previous.Y}) → ({current.X},{current.Y})");
        }
    }

    [Fact]
    public void Detect_CancelledToken_ThrowsOperationCanceled()
    {
        ushort[] codes = MakeFlatWithDefects(64, 64, 1000, (5, 5, 4000));
        using RawImage image = LoadImage(codes, 64, 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => DefectPixelDetector.Detect(image, cancellationToken: cts.Token));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)] // NaN は比較を素通りするので有限性の検査が要る
    public void Detect_InvalidSigma_Throws(double sigmaFactor)
    {
        ushort[] codes = MakeFlatWithDefects(4, 4, 100);
        using RawImage image = LoadImage(codes, 4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DefectPixelDetector.Detect(image, sigmaFactor: sigmaFactor));
    }
}
