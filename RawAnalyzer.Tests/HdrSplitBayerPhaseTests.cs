using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// HDR分割ビュー(各露光の段を左から並置した1枚)の Bayer 位相。各段は1枚の画像なので、チャネルは段の左端を
/// 列0とする位相で決まる。
/// </summary>
/// <remarks>
/// レビュー 2026-10-03 H1。以前は並置画像全体に1つの Bayer パターンを当てており、段の幅が奇数だと後ろの段
/// (左端が奇数の列)の色の位置がずれた。同じ 5×4 RGGB の2段でも、右の段では R/Gr/Gb/B の平均 1000/2000/3000/4000 が
/// 2000/1000/4000/3000 になり、欠陥検出の段ごとの閾値も入れ替わり、WB のスポイトは左右で違うゲイン
/// (Rgain 2.5 と 1.25)を返して保存の色まで変わった。
/// </remarks>
public class HdrSplitBayerPhaseTests
{
    private const int StageWidth = 5;
    private const int StageHeight = 4;
    private const BayerPattern Pattern = BayerPattern.Rggb;

    // 各段の中の座標でのチャネルの値(16bit の code。チャネルごとに一定)
    private const ushort R = 1000;
    private const ushort Gr = 2000;
    private const ushort Gb = 3000;
    private const ushort B = 4000;

    /// <summary>
    /// 幅5(奇数)・RGGB の段を stages 段持つフレーム連結の raw を分割し、分割ビューと同じく並置する。
    /// 各段の中では (偶数列, 偶数行)=R、(奇数, 偶数)=Gr、(偶数, 奇数)=Gb、(奇数, 奇数)=B。
    /// </summary>
    private static RawImage OddWidthSplitView(int stages)
    {
        var format = new RawFormat
        {
            Width = StageWidth, Height = StageHeight, BitDepth = 16, Bayer = Pattern,
            Hdr = HdrMode.FrameSequential, HdrStages = stages, FrameCount = stages,
        };
        var codes = new ushort[StageWidth * StageHeight * stages];
        for (int frame = 0; frame < stages; frame++)
        {
            for (int y = 0; y < StageHeight; y++)
            {
                for (int x = 0; x < StageWidth; x++)
                {
                    codes[(frame * StageHeight + y) * StageWidth + x] = (x % 2, y % 2) switch
                    {
                        (0, 0) => R,
                        (1, 0) => Gr,
                        (0, 1) => Gb,
                        _ => B,
                    };
                }
            }
        }

        using RawImage raw = TestImages.FromCodes(codes, format);
        IReadOnlyList<RawImage> frames = HdrSplitter.Split(raw, format, 0);
        try
        {
            return HdrSplitComposite.Compose(frames, format);
        }
        finally
        {
            foreach (RawImage frame in frames)
            {
                frame.Dispose();
            }
        }
    }

    private static void AssertStageChannels(IReadOnlyList<ChannelHistogram>? channels)
    {
        Assert.NotNull(channels);
        var expected = new Dictionary<BayerChannel, ushort>
        {
            [BayerChannel.R] = R, [BayerChannel.Gr] = Gr, [BayerChannel.Gb] = Gb, [BayerChannel.B] = B,
        };
        Assert.Equal(4, channels.Count);
        foreach (ChannelHistogram channel in channels)
        {
            // 段ごとに一定の値なので、別のチャネルの画素が混ざれば最小・最大が分かれる
            Assert.Equal(expected[channel.Channel], channel.Statistics.Mean);
            Assert.Equal(expected[channel.Channel], channel.Statistics.Min);
            Assert.Equal(expected[channel.Channel], channel.Statistics.Max);
        }
    }

    public static TheoryData<int, int> StagesOfSplitViews() => new()
    {
        { 2, 0 }, { 2, 1 }, { 3, 0 }, { 3, 1 }, { 3, 2 },
    };

    [Theory]
    [MemberData(nameof(StagesOfSplitViews))]
    public void ChannelHistogramOfRoiInStage_UsesPhaseOfStage(int stages, int stage)
    {
        using RawImage composite = OddWidthSplitView(stages);
        RoiAnalysisTarget target = RoiAnalysis.Resolve(
            new RegionOfInterest(stage * StageWidth, 0, StageWidth, StageHeight), channelSplitLayout: false,
            composite.Width, composite.Height, Pattern, splitSegmentWidth: StageWidth);
        Assert.IsType<SourceRoiTarget>(target);

        RoiHistogram result = RoiAnalysis.ComputeHistogram(
            composite, 0, target, Pattern, byChannel: true, CancellationToken.None);

        AssertStageChannels(result.Channels);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ChannelHistogramOfWholeSplitView_UsesPhaseOfEachStage(int stages)
    {
        using RawImage composite = OddWidthSplitView(stages);

        RoiHistogram result = RoiAnalysis.ComputeHistogram(
            composite, 0, new WholeImageTarget(), Pattern, byChannel: true, CancellationToken.None);

        AssertStageChannels(result.Channels);

        // 幅5の段の中では偶数列が3列・奇数列が2列(高さ4は偶数行・奇数行とも2行)
        var expectedCounts = new Dictionary<BayerChannel, long>
        {
            [BayerChannel.R] = 3 * 2 * stages, [BayerChannel.Gr] = 2 * 2 * stages,
            [BayerChannel.Gb] = 3 * 2 * stages, [BayerChannel.B] = 2 * 2 * stages,
        };
        Assert.All(result.Channels!, c => Assert.Equal(expectedCounts[c.Channel], c.Statistics.SampleCount));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void DefectThresholdsOfEachStage_UsePhaseOfStage(int stages)
    {
        using RawImage composite = OddWidthSplitView(stages);

        DefectDetectionResult result = DefectPixelDetector.Detect(
            composite, 0, sigmaFactor: 5, pattern: Pattern, segmentWidth: StageWidth);

        Assert.Equal(stages, result.SegmentCount);
        Assert.Equal(4 * stages, result.ChannelThresholds.Count);
        foreach (DefectChannelThreshold threshold in result.ChannelThresholds)
        {
            ushort expected = threshold.Channel switch
            {
                BayerChannel.R => R,
                BayerChannel.Gr => Gr,
                BayerChannel.Gb => Gb,
                _ => B,
            };
            Assert.True(
                expected == threshold.Mean && threshold.Sigma == 0,
                $"段 {threshold.Segment} の {threshold.Channel}: mean {threshold.Mean} σ {threshold.Sigma}(期待 {expected}・σ 0)");
        }

        // チャネルごとに一定なので欠陥はない(位相を取り違えると、別チャネルの閾値で判定して全画素が欠陥になり得る)
        Assert.Empty(result.Defects);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void SpotGains_AreTheSameAtEveryColumnOfEveryStage(int stages)
    {
        // 各段のどの列でスポイトしても、その段の中の 2x2(R=1000, G=(2000+3000)/2, B=4000)を白とみなす。
        // 段の右端の列(奇数幅なので段の中では偶数列)でも、隣の段の画素とブロックを組まない
        using RawImage composite = OddWidthSplitView(stages);
        for (int x = 0; x < composite.Width; x++)
        {
            for (int y = 0; y < composite.Height; y++)
            {
                WhiteBalanceGains gains = WhiteBalance.ComputeSpotGains(composite, 0, Pattern, x, y);
                Assert.True(
                    gains.GainR == 2.5 && gains.GainB == 0.625,
                    $"({x}, {y}) 段 {x / StageWidth}: Rgain {gains.GainR} Bgain {gains.GainB}(期待 2.5・0.625)");
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void CursorReadoutChannel_UsesPhaseOfStage(int stages)
    {
        // カーソル位置のチャネル名も段の中の座標で決める(右の段の左端の列は R)
        using RawImage composite = OddWidthSplitView(stages);
        for (int x = 0; x < composite.Width; x++)
        {
            for (int y = 0; y < composite.Height; y++)
            {
                string expected = ((x % StageWidth) % 2, y % 2) switch
                {
                    (0, 0) => "R",
                    (1, 0) => "Gr",
                    (0, 1) => "Gb",
                    _ => "B",
                };
                string overlay = CursorReadout.Compose(composite, composite.Format, null, x, y, 0)!.Value.Overlay;
                Assert.True(
                    overlay.EndsWith("  " + expected, StringComparison.Ordinal),
                    $"({x}, {y}) 段 {x / StageWidth}: {overlay}(期待 {expected})");
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void GrayWorldGains_UsePhaseOfEachStage(int stages)
    {
        // どの段のどの 2x2 も R=1000, G=2500, B=4000 なので、ゲインは G/R=2.5、G/B=0.625
        using RawImage composite = OddWidthSplitView(stages);

        WhiteBalanceGains gains = WhiteBalance.ComputeGrayWorld(composite, 0, Pattern);

        Assert.Equal(2.5, gains.GainR, 12);
        Assert.Equal(0.625, gains.GainB, 12);
    }
}
