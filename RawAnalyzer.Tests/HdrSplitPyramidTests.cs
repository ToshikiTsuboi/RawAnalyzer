using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// HDR分割ビュー(各露光の段を左から並置した1枚)の縮小ピラミッド。各段は1枚の画像なので段ごとに縮小し、
/// 縮小表示でも各列はその列の段の画素だけから描く。
/// </summary>
/// <remarks>
/// レビュー 2026-10-03 H2。以前は並置画像全体を一様に縮小していたため、段の幅が縮小率の倍数でないと露光の境目の
/// ブロックが両方の段の画素を平均し、その後で段ごとの表示ゲインを掛けていた。幅6・長秒 10000/短秒 1000・
/// ゲイン 1 倍/10 倍の 25% 表示で、本来 39,39,39 となる列が 39,214,39 になった(偽の明るい縦帯)。
/// </remarks>
public class HdrSplitPyramidTests
{
    /// <summary>
    /// 幅 stageWidth・高さ height の段を、フレーム連結の raw から分割ビューと同じく並置する。
    /// 段 s の画素 (x, y) は value(s, x, y)。
    /// </summary>
    private static RawImage SplitView(int stages, int stageWidth, int height, Func<int, int, int, ushort> value)
    {
        var format = new RawFormat
        {
            Width = stageWidth, Height = height, BitDepth = 16,
            Hdr = HdrMode.FrameSequential, HdrStages = stages, FrameCount = stages,
        };
        var codes = new ushort[stageWidth * height * stages];
        for (int s = 0; s < stages; s++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < stageWidth; x++)
                {
                    codes[(s * height + y) * stageWidth + x] = value(s, x, y);
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

    [Fact]
    public void QuarterZoom_EachColumnIsDrawnFromItsOwnStage()
    {
        // レビューの再現: 段の幅6、長秒 10000・短秒 1000、表示ゲイン 1 倍/10 倍。どの段も表示上は同じ明るさ
        ushort[] stageValues = { 10000, 1000 };
        using RawImage composite = SplitView(2, 6, 8, (s, _, _) => stageValues[s]);
        DisplayLut[] luts =
        {
            DisplayLut.Create(new DisplayParameters(0, 65535, 1, 1, 1)),
            DisplayLut.Create(new DisplayParameters(0, 65535, 10, 1, 1)),
        };
        byte expected = luts[0].Map(stageValues[0]);
        Assert.Equal(expected, luts[1].Map(stageValues[1]));

        TilePyramid pyramid = TilePyramid.Create(composite, maxLevelPixels: long.MaxValue);
        var request = new RenderRequest
        {
            Source = new PyramidLevelRenderSource(pyramid.GetLevel(4)!, composite.Width, composite.Height),
            Lut = luts[0],
            SegmentLuts = luts,
            SegmentWidth = 6,
        };
        var pixels = new byte[3 * 2 * 4];
        ViewportRenderer.Render(request, 0.25, 0, 0, 3, 2, pixels, CancellationToken.None);

        Assert.Equal(new[] { expected, expected, expected }, new[] { pixels[0], pixels[4], pixels[8] });
    }

    [Theory]
    [InlineData(2, 6, 8)]
    [InlineData(2, 13, 37)]
    [InlineData(3, 21, 19)]
    [InlineData(3, 130, 9)]
    public void EveryLevel_AveragesBlocksWithinOneStage(int stages, int stageWidth, int height)
    {
        // 各レベルの画素は、その段の中のブロック(段の左端から縮小率ごと。段の右端の端数のブロックは残りの列)の
        // 平均。前のレベルから連鎖して作るレベルも、元画像から直接作ったときと同じ値になる
        using RawImage composite = SplitView(
            stages, stageWidth, height, (s, x, y) => (ushort)(((s * 7919) + (x * 104729) + (y * 1299709)) % 60000));

        TilePyramid pyramid = TilePyramid.Create(composite, maxLevelPixels: long.MaxValue);

        Assert.NotEmpty(pyramid.Levels);
        foreach (PyramidLevel level in pyramid.Levels)
        {
            int f = level.Factor;
            int stageLevelWidth = (stageWidth + f - 1) / f;
            Assert.Equal(stages * stageLevelWidth, level.Width);
            Assert.Equal((height + f - 1) / f, level.Height);
            for (int s = 0; s < stages; s++)
            {
                for (int j = 0; j < stageLevelWidth; j++)
                {
                    int x0 = s * stageWidth + j * f;
                    int x1 = Math.Min(x0 + f, (s + 1) * stageWidth);
                    for (int i = 0; i < level.Height; i++)
                    {
                        int y0 = i * f;
                        int y1 = Math.Min(y0 + f, height);
                        long sum = 0;
                        for (int y = y0; y < y1; y++)
                        {
                            for (int x = x0; x < x1; x++)
                            {
                                sum += composite.GetPixel(x, y);
                            }
                        }

                        long expected = sum / ((long)(x1 - x0) * (y1 - y0));
                        Assert.True(
                            expected == level.GetPixel(s * stageLevelWidth + j, i),
                            $"縮小率 {f} 段 {s} の ({j}, {i}): {level.GetPixel(s * stageLevelWidth + j, i)}(期待 {expected})");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(2, 13, 2)]
    [InlineData(3, 13, 4)]
    [InlineData(3, 21, 8)]
    [InlineData(2, 6, 4)]
    public void ZoomedOut_EveryColumnShowsTheGrayOfItsStage(int stages, int stageWidth, int factor)
    {
        // 段ごとに一定の値を、段ごとのゲインでどれも同じ明るさに見えるように描く。段の境目のブロックが隣の段の
        // 画素を平均すると、その列だけ明るさが変わる
        ushort[] stageValues = { 32000, 8000, 2000 };
        double[] gains = { 1, 4, 16 };
        using RawImage composite = SplitView(stages, stageWidth, 16, (s, _, _) => stageValues[s]);
        DisplayLut[] luts = Enumerable.Range(0, stages)
            .Select(s => DisplayLut.Create(new DisplayParameters(0, 65535, gains[s], 1, 1)))
            .ToArray();
        byte expected = luts[0].Map(stageValues[0]);
        Assert.All(Enumerable.Range(0, stages), s => Assert.Equal(expected, luts[s].Map(stageValues[s])));

        TilePyramid pyramid = TilePyramid.Create(composite, maxLevelPixels: long.MaxValue);
        var request = new RenderRequest
        {
            Source = new PyramidLevelRenderSource(pyramid.GetLevel(factor)!, composite.Width, composite.Height),
            Lut = luts[0],
            SegmentLuts = luts,
            SegmentWidth = stageWidth,
        };

        // 原点をずらして、描く位置が段の境目の前後のどこに来ても成り立つことを確かめる
        for (int offset = 0; offset < factor; offset++)
        {
            double zoom = 1.0 / factor;
            int destWidth = composite.Width / factor;
            var pixels = new byte[destWidth * 4];
            ViewportRenderer.Render(request, zoom, offset, 0, destWidth, 1, pixels, CancellationToken.None);
            for (int dx = 0; dx < destWidth; dx++)
            {
                double srcX = offset + ((dx + 0.5) * factor);
                if (srcX >= composite.Width)
                {
                    continue;
                }

                Assert.True(
                    expected == pixels[dx * 4],
                    $"縮小率 {factor} 原点 {offset} の列 {dx}(元の列 {srcX}、段 {(int)srcX / stageWidth}): " +
                    $"{pixels[dx * 4]}(期待 {expected})");
            }
        }
    }
}
