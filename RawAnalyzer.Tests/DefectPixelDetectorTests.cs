using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class DefectPixelDetectorTests
{
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
        using RawImage image = TestImages.FromCodes(codes, 64, 64, bitDepth: 12);

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
        using RawImage image = TestImages.FromCodes(codes, 32, 32, bitDepth: 12);

        DefectDetectionResult result = DefectPixelDetector.Detect(image, sigmaFactor: 6.0);

        Assert.Equal(2, result.Defects.Count);
        Assert.Contains(new DefectPixel(5, 7, 4000, DefectType.Hot), result.Defects);
        Assert.Contains(new DefectPixel(20, 15, 10, DefectType.Dead), result.Defects);
        Assert.Equal(1, result.HotCount);
        Assert.Equal(1, result.DeadCount);
        Assert.False(result.Truncated);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Detect_OneTypeOnly_IgnoresTheOtherType(bool detectHot, bool detectDead)
    {
        // Detect_HotAndDeadPixels_FindsBoth と同じ素材(両方を探せば白点・黒点が1つずつ見つかる)で、
        // 片方だけを探すと、もう片方は見つけず、白点を黒点(またはその逆)として数えもしない
        ushort[] codes = MakeFlatWithDefects(32, 32, 1000, (5, 7, 4000), (20, 15, 10));
        using RawImage image = TestImages.FromCodes(codes, 32, 32, bitDepth: 12);

        DefectDetectionResult result = DefectPixelDetector.Detect(
            image, detectHot: detectHot, detectDead: detectDead);

        DefectPixel expected = detectHot
            ? new DefectPixel(5, 7, 4000, DefectType.Hot)
            : new DefectPixel(20, 15, 10, DefectType.Dead);
        Assert.Equal(expected, Assert.Single(result.Defects));
        Assert.Equal((detectHot ? 1 : 0, detectDead ? 1 : 0), (result.HotCount, result.DeadCount));
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

        using RawImage image = TestImages.FromCodes(codes, size, size, bitDepth: 12);

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

    /// <summary>
    /// HDR分割ビューと同じく、左に長秒(信号 long)・右に短秒(信号 short)の段を並べた画面。
    /// </summary>
    private static ushort[] MakeSideBySide(
        int segmentWidth, int height, ushort longCode, ushort shortCode,
        params (int X, int Y, ushort Code)[] defects)
    {
        int width = segmentWidth * 2;
        var codes = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ushort level = x < segmentWidth ? longCode : shortCode;
                codes[y * width + x] = (ushort)(level + ((x + y) % 2)); // σ>0にするためのディザ
            }
        }

        foreach ((int x, int y, ushort code) in defects)
        {
            codes[y * width + x] = code;
        }

        return codes;
    }

    [Theory]
    [InlineData(BayerPattern.Rggb)]
    [InlineData(BayerPattern.None)]
    public void Detect_SideBySideExposures_PerSegment_FindsDefectsOfEachExposure(BayerPattern pattern)
    {
        // 露光比4のフラット(長秒 2000・短秒 500 code)を左右に並べた HDR 分割ビュー。
        // 画像全体で1組の統計を取ると各チャネルが mean≈1250・σ≈750 になり、黒点の閾値は負、
        // 白点の閾値は値域の外へ出て、長秒の黒点(1000)も短秒の白点(1500)も0件になっていた
        const int segment = 32;
        ushort[] codes = MakeSideBySide(segment, 32, 2000, 500, (10, 10, 1000), (40, 12, 1500));
        using RawImage image = TestImages.FromCodes(codes, segment * 2, 32, 12, pattern);

        DefectDetectionResult mixed = DefectPixelDetector.Detect(image, pattern: pattern);
        Assert.Empty(mixed.Defects);

        DefectDetectionResult result = DefectPixelDetector.Detect(
            image, pattern: pattern, segmentWidth: segment);

        Assert.Equal(2, result.Defects.Count);
        Assert.Contains(new DefectPixel(10, 10, 1000, DefectType.Dead), result.Defects);
        Assert.Contains(new DefectPixel(40, 12, 1500, DefectType.Hot), result.Defects);
        Assert.Equal(2, result.SegmentCount);

        // 閾値は段×チャネルごと(モノクロは段ごと)に返す。スカラー閾値は使わせない
        int channels = pattern == BayerPattern.None ? 1 : 4;
        Assert.Equal(2 * channels, result.ChannelThresholds.Count);
        Assert.True(double.IsNaN(result.HotThreshold));
        Assert.All(result.ChannelThresholds.Where(t => t.Segment == 0), t => Assert.InRange(t.Mean, 1900, 2010));
        Assert.All(result.ChannelThresholds.Where(t => t.Segment == 1), t => Assert.InRange(t.Mean, 490, 510));
    }

    [Fact]
    public void Detect_SegmentWidthNotDividingImage_LastSegmentIsNarrower()
    {
        // 区画の幅で割り切れない右端は、残りの幅の区画として統計を取る(取りこぼさない)
        ushort[] codes = MakeFlatWithDefects(40, 32, 1000, (38, 5, 4000));
        using RawImage image = TestImages.FromCodes(codes, 40, 32, bitDepth: 12);

        DefectDetectionResult result = DefectPixelDetector.Detect(image, segmentWidth: 16);

        Assert.Equal(3, result.SegmentCount);
        Assert.Equal(new DefectPixel(38, 5, 4000, DefectType.Hot), Assert.Single(result.Defects));
    }

    [Fact]
    public void Detect_Truncated_KeepsFirstDefectsInRowOrder_AndIsRepeatable()
    {
        // 上半分は1行に白点1つ(まばら)、下半分は1画素おきに白点(密)。上限で打ち切るとき、残すのは
        // 上から(y→x順)の先頭の件数。以前は並列走査で先に共有リストへ移したスレッド(密な下半分を走査した
        // スレッド)の行範囲が残り、上半分の白点が落ちて、どの帯が残るかも実行ごとに変わった
        const int width = 256;
        const int height = 512;
        var codes = new ushort[width * height];
        var expected = new List<DefectPixel>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool hot = y < height / 2 ? x == (y * 7 % width) : (x & 1) == 0;
                codes[y * width + x] = hot ? (ushort)4000 : (ushort)(1000 + ((x + y) % 2));
                if (hot)
                {
                    expected.Add(new DefectPixel(x, y, 4000, DefectType.Hot));
                }
            }
        }

        using RawImage image = TestImages.FromCodes(codes, width, height, bitDepth: 12);
        const int limit = 1000;

        for (int run = 0; run < 3; run++)
        {
            DefectDetectionResult result = DefectPixelDetector.Detect(
                image, sigmaFactor: 0.5, detectDead: false, maxResults: limit);

            Assert.True(result.Truncated);
            Assert.Equal(expected.Take(limit), result.Defects);
        }
    }

    [Fact]
    public void Detect_ExactlyMaxResults_IsNotTruncated()
    {
        // 上限ちょうどの件数は打ち切りではない(上限を超える欠陥があったときだけ打ち切りと示す)
        ushort[] codes = MakeFlatWithDefects(64, 64, 1000, (3, 3, 4000), (40, 50, 4000));
        using RawImage image = TestImages.FromCodes(codes, 64, 64, bitDepth: 12);

        DefectDetectionResult result = DefectPixelDetector.Detect(image, maxResults: 2);
        DefectDetectionResult truncated = DefectPixelDetector.Detect(image, maxResults: 1);

        Assert.False(result.Truncated);
        Assert.Equal(2, result.Defects.Count);
        Assert.True(truncated.Truncated);
        Assert.Equal(new DefectPixel(3, 3, 4000, DefectType.Hot), Assert.Single(truncated.Defects));
    }

    [Fact]
    public void Detect_CancelledToken_ThrowsOperationCanceled()
    {
        ushort[] codes = MakeFlatWithDefects(64, 64, 1000, (5, 5, 4000));
        using RawImage image = TestImages.FromCodes(codes, 64, 64, bitDepth: 12);
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
        using RawImage image = TestImages.FromCodes(codes, 4, 4, bitDepth: 12);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DefectPixelDetector.Detect(image, sigmaFactor: sigmaFactor));
    }
}
