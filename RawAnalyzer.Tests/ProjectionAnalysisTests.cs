using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 水平射影(各列の縦方向の平均・最小・最大を x に沿って並べる)と垂直射影(各行の横方向の平均・最小・最大を
/// y に沿って並べる)。期待値は手計算か、画素値の式から閉じた形で求めた値と比べる(同じ走査を書き直して比べない)。
/// </summary>
public class ProjectionAnalysisTests
{
    [Fact]
    public void RoiProjections_AverageAndExtremesOfEachColumnAndRowWithinRoi()
    {
        // 8×5 の ROI(2,1,4,3) に
        //   y=1:  90 190 290 390
        //   y=2: 100 200 330 400
        //   y=3: 110 170 310 410
        // を置き、ROI の外は 4095(混ざれば分かる)。
        // 列: 平均 100 / 186.67 / 310 / 400、最小 90 / 170 / 290 / 390、最大 110 / 200 / 330 / 410
        // 行: 平均 240 / 257.5 / 250、最小 90 / 100 / 110、最大 390 / 400 / 410
        var roi = new RegionOfInterest(2, 1, 4, 3);
        using RawImage image = WithRoiValues(8, 5, roi, new ushort[,]
        {
            { 90, 190, 290, 390 },
            { 100, 200, 330, 400 },
            { 110, 170, 310, 410 },
        });

        ProjectionResult result = ProjectionAnalysis.Compute(image, 0, roi, ProjectionAxes.Both);

        ProjectionProfile horizontal = result.Horizontal!;
        AssertClose(new[] { 100, 560 / 3.0, 310, 400 }, horizontal.Mean);
        Assert.Equal(new double[] { 90, 170, 290, 390 }, horizontal.Min);
        Assert.Equal(new double[] { 110, 200, 330, 410 }, horizontal.Max);
        Assert.Equal(3, horizontal.SamplesPerPosition);

        ProjectionProfile vertical = result.Vertical!;
        AssertClose(new[] { 240, 257.5, 250 }, vertical.Mean);
        Assert.Equal(new double[] { 90, 100, 110 }, vertical.Min);
        Assert.Equal(new double[] { 390, 400, 410 }, vertical.Max);
        Assert.Equal(4, vertical.SamplesPerPosition);
    }

    [Fact]
    public void WholeImage_UsesEveryPixel()
    {
        //   1 2 3
        //   7 5 9
        using RawImage image = TestImages.FromCodes(new ushort[] { 1, 2, 3, 7, 5, 9 }, 3, 2, bitDepth: 12);

        ProjectionResult result = ProjectionAnalysis.ComputeWholeImage(image, 0, ProjectionAxes.Both);

        Assert.Equal(new[] { 4, 3.5, 6 }, result.Horizontal!.Mean);
        Assert.Equal(new double[] { 1, 2, 3 }, result.Horizontal.Min);
        Assert.Equal(new double[] { 7, 5, 9 }, result.Horizontal.Max);
        Assert.Equal(new double[] { 2, 7 }, result.Vertical!.Mean);
        Assert.Equal(new double[] { 1, 5 }, result.Vertical.Min);
        Assert.Equal(new double[] { 3, 9 }, result.Vertical.Max);
    }

    [Fact]
    public void OnlyRequestedAxes_AreComputed()
    {
        using RawImage image = TestImages.FromCodes(new ushort[] { 1, 2, 3, 7, 5, 9 }, 3, 2);

        ProjectionResult horizontal = ProjectionAnalysis.ComputeWholeImage(image, 0, ProjectionAxes.Horizontal);
        ProjectionResult vertical = ProjectionAnalysis.ComputeWholeImage(image, 0, ProjectionAxes.Vertical);

        Assert.Equal(new[] { 4, 3.5, 6 }, horizontal.Horizontal!.Mean);
        Assert.Null(horizontal.Vertical);
        Assert.Null(vertical.Horizontal);
        Assert.Equal(new double[] { 2, 7 }, vertical.Vertical!.Mean);
        Assert.Equal(new ProjectionResult(null, null),
            ProjectionAnalysis.ComputeWholeImage(image, 0, ProjectionAxes.None));
    }

    [Fact]
    public void ChannelGrid_UsesOnlyThatChannelsPixels()
    {
        // 値 = 10y + x の 6×4(RGGB)。Gr の格子 (1,0) から 3×2 = 元画像の (1,0) (3,0) (5,0) / (1,2) (3,2) (5,2)。
        // 列(格子の列 = 元画像の x 1,3,5): 平均 (1+21)/2=11, (3+23)/2=13, (5+25)/2=15、最小 1,3,5、最大 21,23,25
        // 行(格子の行 = 元画像の y 0,2): 平均 (1+3+5)/3=3, (21+23+25)/3=23、最小 1,21、最大 5,25
        ushort[] codes = Enumerable.Range(0, 24).Select(i => (ushort)((10 * (i / 6)) + (i % 6))).ToArray();
        using RawImage image = TestImages.FromCodes(codes, 6, 4, bitDepth: 10, BayerPattern.Rggb);

        ProjectionResult result = ProjectionAnalysis.Compute(
            image, 0, new ChannelRegion(1, 0, 3, 2), ProjectionAxes.Both);

        Assert.Equal(new double[] { 11, 13, 15 }, result.Horizontal!.Mean);
        Assert.Equal(new double[] { 1, 3, 5 }, result.Horizontal.Min);
        Assert.Equal(new double[] { 21, 23, 25 }, result.Horizontal.Max);
        Assert.Equal(2, result.Horizontal.SamplesPerPosition);
        Assert.Equal(new double[] { 3, 23 }, result.Vertical!.Mean);
        Assert.Equal(new double[] { 1, 21 }, result.Vertical.Min);
        Assert.Equal(new double[] { 5, 25 }, result.Vertical.Max);
        Assert.Equal(3, result.Vertical.SamplesPerPosition);
    }

    [Fact]
    public void SelectedFrame_IsRead()
    {
        // 2×2×2 フレーム。frame0 は全画素 100、frame1 は 1 2 / 3 4
        var format = new RawFormat { Width = 2, Height = 2, BitDepth = 12, FrameCount = 2 };
        using RawImage image = TestImages.FromCodes(new ushort[] { 100, 100, 100, 100, 1, 2, 3, 4 }, format);

        ProjectionResult result = ProjectionAnalysis.ComputeWholeImage(image, 1, ProjectionAxes.Both);

        Assert.Equal(new double[] { 2, 3 }, result.Horizontal!.Mean);
        Assert.Equal(new[] { 1.5, 3.5 }, result.Vertical!.Mean);
    }

    [Theory]
    [InlineData(1, 1)]       // タイルが画素ごと(列・行とも部分和を合算する)
    [InlineData(3, 2)]       // 列の帯も行の帯も複数(端のタイルは欠ける)
    [InlineData(7, 2)]       // 列の帯が1本(行は直接書き、列は部分和を合算する)
    [InlineData(2, 5)]       // 行の帯が1本(列は直接書き、行は部分和を合算する)
    [InlineData(7, 5)]       // タイル1つ
    public void AnyTiling_GivesTheSameValues(int stripWidth, int bandHeight)
    {
        // 値 = x + 2y(7×5、16bit)。列 x: 平均 x + (H−1) = x + 4、最小 x、最大 x + 8。
        // 行 y: 平均 2y + (W−1)/2 = 2y + 3、最小 2y、最大 2y + 6。(4,3) だけ 60000 にして、端でない最大も確かめる
        const int width = 7;
        const int height = 5;
        ushort[] codes = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                codes[(y * width) + x] = (ushort)(x + (2 * y));
            }
        }

        codes[(3 * width) + 4] = 60000;
        using RawImage image = TestImages.FromCodes(codes, width, height);

        ProjectionResult result = ProjectionAnalysis.ComputeLattice(
            image, 0, new ProjectionAnalysis.Lattice(0, 0, 1, width, height), ProjectionAxes.Both,
            new ProjectionTiling(stripWidth, bandHeight), CancellationToken.None);

        double spike = 60000 - (4 + (2 * 3));
        for (int x = 0; x < width; x++)
        {
            Assert.Equal(x + 4 + (x == 4 ? spike / height : 0), result.Horizontal!.Mean[x], Tolerance);
            Assert.Equal(x, result.Horizontal.Min[x]);
            Assert.Equal(x == 4 ? 60000 : x + 8, result.Horizontal.Max[x]);
        }

        for (int y = 0; y < height; y++)
        {
            Assert.Equal((2 * y) + 3 + (y == 3 ? spike / width : 0), result.Vertical!.Mean[y], Tolerance);
            Assert.Equal(2 * y, result.Vertical.Min[y]);
            Assert.Equal(y == 3 ? 60000 : (2 * y) + 6, result.Vertical.Max[y]);
        }
    }

    [Fact]
    public void ChannelGrid_WithSmallTiles_GivesTheSameValuesAsWholeTile()
    {
        using RawImage image = TestImages.FromCodes(
            TestData.MakePattern(31 * 23, 14), 31, 23, bitDepth: 14, BayerPattern.Rggb);
        var lattice = new ProjectionAnalysis.Lattice(1, 2, 2, 15, 11);

        ProjectionResult whole = ProjectionAnalysis.ComputeLattice(
            image, 0, lattice, ProjectionAxes.Both, new ProjectionTiling(15, 11), CancellationToken.None);
        ProjectionResult tiled = ProjectionAnalysis.ComputeLattice(
            image, 0, lattice, ProjectionAxes.Both, new ProjectionTiling(4, 3), CancellationToken.None);

        Assert.Equal(whole.Horizontal!.Mean, tiled.Horizontal!.Mean);
        Assert.Equal(whole.Horizontal.Min, tiled.Horizontal.Min);
        Assert.Equal(whole.Horizontal.Max, tiled.Horizontal.Max);
        Assert.Equal(whole.Vertical!.Mean, tiled.Vertical!.Mean);
        Assert.Equal(whole.Vertical.Min, tiled.Vertical.Min);
        Assert.Equal(whole.Vertical.Max, tiled.Vertical.Max);

        // 格子の列 0 は元画像の x=1、行 y = 2,4,…,22 の画素(1つずつ拾った値)
        int shift = 16 - 14;
        double expected = Enumerable.Range(0, 11).Average(j => image.GetPixel(1, 2 + (2 * j)) >> shift);
        Assert.Equal(expected, whole.Horizontal.Mean[0], Tolerance);
    }

    [Theory]
    [InlineData(32768, 32768)] // 10億画素の正方形
    [InlineData(100_000, 3)]   // 行の少ない横長(列の帯を広げ、行の帯は1本)
    [InlineData(3, 1_000_000)] // 列の少ない縦長(列の帯は1本)
    [InlineData(5000, 5000)]
    [InlineData(1, 1)]
    public void Tiling_BoundsPartialSumsAndTileSize(int width, int height)
    {
        // 全画素を読んでも作業メモリが画像の大きさに比例しないこと: 部分和を持つ向き(帯が2本以上の向き)の
        // 長さは 4096 以下、1タイルはおよそ 100万画素以下(1行が長いときは 1行)
        ProjectionTiling tiling = ProjectionAnalysis.ChooseTiling(width, height);
        int strips = (width + tiling.StripWidth - 1) / tiling.StripWidth;
        int bands = (height + tiling.BandHeight - 1) / tiling.BandHeight;

        if (bands > 1)
        {
            Assert.InRange(tiling.StripWidth, 1, ProjectionAnalysis.MaxPartialLength);
        }

        if (strips > 1)
        {
            Assert.InRange(tiling.BandHeight, 1, ProjectionAnalysis.MaxPartialLength);
        }

        Assert.True((long)tiling.StripWidth * tiling.BandHeight
            <= Math.Max(ProjectionAnalysis.TargetTilePixels, tiling.StripWidth));
        Assert.True(tiling.StripWidth <= Math.Max(width, 1) && tiling.BandHeight <= Math.Max(height, 1));
    }

    [Fact]
    public void MemoryMappedImage_GivesTheSameValuesAsHeapImage()
    {
        // 1億画素超の画像は MemoryMappedFile で参照する。閾値0で読んで MMF の経路を通す
        var format = new RawFormat { Width = 9000, Height = 130, BitDepth = 12 };
        ushort[] codes = TestData.MakePattern(format.Width * format.Height, 12);
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(codes, format));
        try
        {
            using RawImage mapped = RawLoader.Load(path, format, inMemoryPixelThreshold: 0);
            using RawImage heap = TestImages.FromCodes(codes, format);
            Assert.True(mapped.IsMemoryMapped);

            ProjectionResult fromFile = ProjectionAnalysis.ComputeWholeImage(mapped, 0, ProjectionAxes.Both);
            ProjectionResult fromHeap = ProjectionAnalysis.ComputeWholeImage(heap, 0, ProjectionAxes.Both);

            Assert.Equal(fromHeap.Horizontal!.Mean, fromFile.Horizontal!.Mean);
            Assert.Equal(fromHeap.Horizontal.Max, fromFile.Horizontal.Max);
            Assert.Equal(fromHeap.Vertical!.Mean, fromFile.Vertical!.Mean);
            Assert.Equal(fromHeap.Vertical.Min, fromFile.Vertical.Min);

            // 列 0 の平均は全行の画素の平均(1つずつ拾った値)
            double expected = Enumerable.Range(0, format.Height).Average(y => (double)codes[y * format.Width]);
            Assert.Equal(expected, fromFile.Horizontal.Mean[0], Tolerance);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // MMF の解放が OS に反映されるまで消せないことがある
            }
        }
    }

    [Fact]
    public void Canceled_Throws()
    {
        using RawImage image = TestImages.FromCodes(new ushort[64 * 64], 64, 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ProjectionAnalysis.ComputeWholeImage(image, 0, ProjectionAxes.Both, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ProjectionAnalysis.Compute(image, 0, new ChannelRegion(0, 0, 4, 4), ProjectionAxes.Horizontal, cts.Token));
    }

    [Fact]
    public void CanceledWhileReading_StopsBeforeReadingTheRest()
    {
        // 読んでいる途中の取り消し(ROI・送りの変更で前の計算を取り消す)。最初のタイルを読み終えた時点で取り消すと、
        // 残りのタイルを読まずに止まり、途中までの値を結果として返さない。タイルの中でも数十行ごとに確かめる
        using RawImage image = TestImages.FromCodes(new ushort[64 * 16384], 64, 16384);
        using var cts = new CancellationTokenSource();
        int reports = 0;
        var progress = new SynchronousProgress(_ =>
        {
            Interlocked.Increment(ref reports);
            cts.Cancel();
        });

        Assert.ThrowsAny<OperationCanceledException>(() => ProjectionAnalysis.ComputeLattice(
            image, 0, new ProjectionAnalysis.Lattice(0, 0, 1, 64, 16384), ProjectionAxes.Both,
            new ProjectionTiling(64, 256), cts.Token, progress));
        Assert.InRange(reports, 1, 63); // 64 タイルのうち、取り消したときに読み終えていたものだけ
    }

    [Fact]
    public void Progress_ReachesOneWhenFinished()
    {
        using RawImage image = TestImages.FromCodes(new ushort[64 * 1024], 64, 1024);
        var reported = new List<double>();
        var progress = new SynchronousProgress(value =>
        {
            lock (reported)
            {
                reported.Add(value);
            }
        });

        ProjectionAnalysis.ComputeLattice(
            image, 0, new ProjectionAnalysis.Lattice(0, 0, 1, 64, 1024), ProjectionAxes.Both,
            new ProjectionTiling(64, 256), CancellationToken.None, progress);

        Assert.Equal(4, reported.Count);
        Assert.Equal(1.0, reported.Max());
    }

    [Fact]
    public void RoiOutsideImage_IsClampedAndChannelGridOutsideImage_Throws()
    {
        using RawImage image = TestImages.FromCodes(new ushort[] { 1, 2, 3, 7, 5, 9 }, 3, 2);

        ProjectionResult clamped = ProjectionAnalysis.Compute(
            image, 0, new RegionOfInterest(1, -5, 10, 10), ProjectionAxes.Both);
        Assert.Equal(new[] { 3.5, 6 }, clamped.Horizontal!.Mean);
        Assert.Equal(new[] { 2.5, 7 }, clamped.Vertical!.Mean);

        ProjectionResult outside = ProjectionAnalysis.Compute(
            image, 0, new RegionOfInterest(5, 5, 2, 2), ProjectionAxes.Both);
        Assert.Equal(0, outside.Horizontal!.Length);
        Assert.Equal(0, outside.Vertical!.Length);

        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectionAnalysis.Compute(
            image, 0, new ChannelRegion(1, 0, 2, 1), ProjectionAxes.Both)); // x = 1, 3 → 3 は幅3の範囲外
    }

    private const int Tolerance = 9;

    private static void AssertClose(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], actual[i], Tolerance);
        }
    }

    private static RawImage WithRoiValues(int width, int height, RegionOfInterest roi, ushort[,] values)
    {
        var codes = new ushort[width * height];
        Array.Fill(codes, (ushort)4095);
        for (int row = 0; row < roi.Height; row++)
        {
            for (int col = 0; col < roi.Width; col++)
            {
                codes[((roi.Y + row) * width) + roi.X + col] = values[row, col];
            }
        }

        return TestImages.FromCodes(codes, width, height, bitDepth: 12);
    }

    /// <summary>報告を呼んだスレッドでそのまま受け取る進み具合(Progress&lt;T&gt; は同期コンテキストへ送るので使わない)。</summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
