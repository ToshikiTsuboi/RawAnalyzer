using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class HdrSplitterTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Split_LineInterleaved_SeparatesByRowPeriod(int stages)
    {
        // Bayerなしは1行単位なので段数がそのまま行周期になる(2段=偶奇行、3段=3行周期)
        const int width = 2;
        const int rowsPerFrame = 3;
        int height = stages * rowsPerFrame;
        var values = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                values[y * width + x] = (ushort)(y * 100 + x);
            }
        }

        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16,
            Hdr = HdrMode.Auto, HdrStages = stages,
        };
        using RawImage image = TestImages.FromCodes(values, format);

        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image);

        Assert.Equal(stages, frames.Count);
        Assert.All(frames, f => Assert.Equal(rowsPerFrame, f.Height));
        Assert.All(frames, f => Assert.Equal(HdrMode.None, f.Format.Hdr));
        for (int stage = 0; stage < stages; stage++)
        {
            for (int y = 0; y < rowsPerFrame; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal(
                        (ushort)((y * stages + stage) * 100 + x), frames[stage].GetPixel(x, y));
                }
            }
        }
    }

    [Fact]
    public void Split_WithOverriddenFormat_UsesGivenBayerPattern()
    {
        // Bayerセンサは色ペア(2行)単位で長/短が交互になる。
        // フォーマットパネルで後からBayerを指定した場合、RawImage.Format は
        // 読み込み時のまま固定なので blockHeight=1 になり色ペアが崩れていた
        const int width = 4;
        const int height = 8;
        var values = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                values[y * width + x] = (ushort)(y * 100);
            }
        }

        // 読み込み時は Bayer 未指定
        var loadedFormat = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16,
            Hdr = HdrMode.Auto, HdrStages = 2,
        };
        using RawImage image = TestImages.FromCodes(values, loadedFormat);

        // パネルで RGGB を指定した状態を渡す
        RawFormat panelFormat = loadedFormat with { Bayer = BayerPattern.Rggb };
        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image, panelFormat);
        try
        {
            Assert.Equal(2, frames.Count);
            Assert.All(frames, f => Assert.Equal(4, f.Height));
            Assert.All(frames, f => Assert.Equal(BayerPattern.Rggb, f.Format.Bayer));

            // 2行ブロックで交互 = 長秒は行0,1,4,5 / 短秒は行2,3,6,7
            int[] longRows = { 0, 100, 400, 500 };
            int[] shortRows = { 200, 300, 600, 700 };
            for (int y = 0; y < 4; y++)
            {
                Assert.Equal(longRows[y], frames[0].GetPixel(0, y));
                Assert.Equal(shortRows[y], frames[1].GetPixel(0, y));
            }
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
    public void Split_LineBlock1WithBayer_TakesEveryOtherRow()
    {
        // 物理ライン1本ごとに長秒/短秒を読み出すセンサ(DOL)。
        // 既定の2行単位で切ると別の露光が混ざるため、1行単位を明示できること。
        const int width = 4;
        const int height = 12;
        var values = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // 偶数行=長秒(1000番台) / 奇数行=短秒(2000番台)、下2桁に元行番号
                values[y * width + x] = (ushort)((y % 2 == 0 ? 1000 : 2000) + y);
            }
        }

        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16,
            Hdr = HdrMode.Auto, HdrStages = 2, Bayer = BayerPattern.Rggb,
            HdrLineBlock = 1,
        };
        using RawImage image = TestImages.FromCodes(values, format);

        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image, format);
        try
        {
            Assert.Equal(6, frames[0].Height);
            Assert.Equal(6, frames[1].Height);
            for (int y = 0; y < 6; y++)
            {
                Assert.Equal(1000 + y * 2, frames[0].GetPixel(0, y));
                Assert.Equal(2000 + y * 2 + 1, frames[1].GetPixel(0, y));
            }

            // 1行単位でも部分画像側では2行ごとに色位相が進むのでRGGBのまま
            Assert.Equal(BayerPattern.Rggb, frames[0].Format.Bayer);
            Assert.Equal(BayerPattern.Rggb, frames[1].Format.Bayer);
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
    public void Split_RowOffset_AlignsStagesAndCropsOverlap()
    {
        // 短秒側が2行ぶん下にずれて格納されている想定。
        // オフセットを指定すると、同じ内容が同じ行に来るように切り出される。
        const int width = 2;
        const int height = 24;
        var values = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            int sub = y / 2;                       // 部分画像側の行
            int content = y % 2 == 0 ? sub : sub - 2;  // 短秒は2行遅れ
            for (int x = 0; x < width; x++)
            {
                values[y * width + x] = (ushort)(100 + Math.Max(0, content));
            }
        }

        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16,
            Hdr = HdrMode.Auto, HdrStages = 2,
            HdrLineBlock = 1, HdrRowOffset = 2,
        };
        using RawImage image = TestImages.FromCodes(values, format);

        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image, format);
        try
        {
            // 12行のうち重なるのは10行
            Assert.Equal(10, frames[0].Height);
            Assert.Equal(10, frames[1].Height);
            for (int y = 0; y < 10; y++)
            {
                Assert.Equal(frames[0].GetPixel(0, y), frames[1].GetPixel(0, y));
            }
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
    public void Split_OddRowOffsetWithBayer_KeepsPhaseAcrossStages()
    {
        // 位相は整列後のコンテンツ(実際に読み出したセンサ行)で決まる。
        // Split_RowOffset_AlignsStagesAndCropsOverlap が保証するとおり
        // 整列後の全段は同一のセンサ行を含むため、奇数オフセットでも
        // Bayerパターンは全段で同一でなければならない
        // (旧実装は切り出し位置基準で短秒側を Gbrg にしてしまっていた)
        const int width = 2;
        const int height = 16;
        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16,
            Hdr = HdrMode.Auto, HdrStages = 2, Bayer = BayerPattern.Rggb,
            HdrLineBlock = 1, HdrRowOffset = 1,
        };
        using RawImage image = TestImages.FromCodes(new ushort[width * height], format);

        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image, format);
        try
        {
            Assert.Equal(BayerPattern.Rggb, frames[0].Format.Bayer);
            Assert.Equal(BayerPattern.Rggb, frames[1].Format.Bayer);
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
    public void Split_NegativeOddRowOffset_ShiftsAllStagesTogether()
    {
        // 負のオフセットでは基準段側が奇数行から始まるため、
        // 全段が同じ1行シフトの位相(Rggb→Gbrg)になる
        const int width = 2;
        const int height = 16;
        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16,
            Hdr = HdrMode.Auto, HdrStages = 2, Bayer = BayerPattern.Rggb,
            HdrLineBlock = 1, HdrRowOffset = -1,
        };
        using RawImage image = TestImages.FromCodes(new ushort[width * height], format);

        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image, format);
        try
        {
            Assert.Equal(frames[0].Format.Bayer, frames[1].Format.Bayer);
            Assert.Equal(BayerPattern.Gbrg, frames[0].Format.Bayer);
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
    public void Split_RowOffsetLargerThanImage_Throws()
    {
        var format = new RawFormat
        {
            Width = 2, Height = 8, BitDepth = 16,
            Hdr = HdrMode.Auto, HdrStages = 2, HdrLineBlock = 1, HdrRowOffset = 100,
        };
        using RawImage image = TestImages.FromCodes(new ushort[16], format);

        Assert.Throws<InvalidOperationException>(() => HdrSplitter.Split(image, format));
    }

    [Fact]
    public void ResolveLayout_AutoInfersFromFrameCount()
    {
        var lineInterleaved = new RawFormat
        {
            Width = 2, Height = 4, Hdr = HdrMode.Auto, FrameCount = 1, HdrStages = 2,
        };
        var frameSequential = lineInterleaved with { FrameCount = 2 };

        Assert.Equal(HdrMode.LineInterleaved, HdrSplitter.ResolveLayout(lineInterleaved, 2));
        Assert.Equal(HdrMode.FrameSequential, HdrSplitter.ResolveLayout(frameSequential, 2));

        // 段数ともフレーム数1とも一致しない場合は推定できない
        Assert.Throws<InvalidOperationException>(
            () => HdrSplitter.ResolveLayout(lineInterleaved with { FrameCount = 4 }, 2));
    }

    [Fact]
    public void Split_LineInterleavedMultiFrame_SplitsRequestedFrame()
    {
        // 行交互HDRのマルチフレームは、各フレームが別時刻の1回の撮影(長秒/短秒を行交互に含む)。
        // 2フレーム目を表示してHDR分割・合成しても先頭フレームの画素が使われ、
        // 別時刻の画像を表示・保存していた(レビュー指摘 #4)
        const int width = 2;
        const int height = 4;
        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16, FrameCount = 2,
            Hdr = HdrMode.LineInterleaved, HdrStages = 2,
        };
        using RawImage image = TestImages.FromCodes(
            BuildLineInterleavedFrames(width, height, (1000, 100), (5000, 500)), format);

        IReadOnlyList<RawImage> split = HdrSplitter.Split(image, format, frame: 1);
        try
        {
            Assert.Equal(2, split.Count);
            AssertAllPixels(split[0], 5000); // 長秒
            AssertAllPixels(split[1], 500);  // 短秒
        }
        finally
        {
            foreach (RawImage stage in split)
            {
                stage.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Split_FrameOutOfRange_Throws(int frame)
    {
        var format = new RawFormat
        {
            Width = 2, Height = 4, BitDepth = 16, FrameCount = 2,
            Hdr = HdrMode.LineInterleaved, HdrStages = 2,
        };
        using RawImage image = TestImages.FromCodes(new ushort[2 * 4 * 2], format);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => HdrSplitter.Split(image, format, frame));
    }

    [Theory]
    [InlineData(HdrMode.LineInterleaved, 1)]
    [InlineData(HdrMode.FrameSequential, 2)]
    public void Split_CanceledToken_ThrowsOperationCanceled(HdrMode hdr, int frames)
    {
        // 回帰テスト: HDR分割は取り消しの手段がなく、計算中に別のファイルを開いても全画素のコピーが最後まで走り、
        // 終わるまで新しいファイルを表示できなかった(CLAUDE.md 性能ルール4)。取り消しを受け付ける
        var format = new RawFormat
        {
            Width = 4, Height = 8, BitDepth = 16, FrameCount = frames, Hdr = hdr, HdrStages = 2,
        };
        using RawImage image = TestImages.FromCodes(new ushort[4 * 8 * frames], format);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => HdrSplitter.Split(image, format, 0, cts.Token));
    }

    /// <summary>
    /// 1行ごとに長秒/短秒が交互に並ぶ行交互HDRのフレーム群を作る(フレーム順に連結)。
    /// </summary>
    private static ushort[] BuildLineInterleavedFrames(
        int width, int height, params (ushort Long, ushort Short)[] frames)
    {
        var values = new ushort[width * height * frames.Length];
        for (int f = 0; f < frames.Length; f++)
        {
            for (int y = 0; y < height; y++)
            {
                ushort value = y % 2 == 0 ? frames[f].Long : frames[f].Short;
                Array.Fill(values, value, (f * height + y) * width, width);
            }
        }

        return values;
    }

    private static void AssertAllPixels(RawImage image, ushort expected)
    {
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Assert.Equal(expected, image.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void Split_FrameSequentialWithWrongFrameCount_Throws()
    {
        var format = new RawFormat
        {
            Width = 2, Height = 4, BitDepth = 16, FrameCount = 1,
            Hdr = HdrMode.FrameSequential, HdrStages = 2,
        };
        using RawImage image = TestImages.FromCodes(new ushort[8], format);

        Assert.Throws<InvalidOperationException>(() => HdrSplitter.Split(image, format));
    }

    [Fact]
    public void Split_WithMismatchedLayout_Throws()
    {
        const int width = 4;
        const int height = 4;
        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16, Hdr = HdrMode.Auto, HdrStages = 2,
        };
        using RawImage image = TestImages.FromCodes(new ushort[width * height], format);

        Assert.Throws<ArgumentException>(
            () => HdrSplitter.Split(image, format with { Width = width * 2 }));
        Assert.Throws<ArgumentException>(
            () => HdrSplitter.Split(image, format with { BitDepth = 12 }));
    }

    [Fact]
    public void Split_FrameSequential_ReturnsEachFrameRegardlessOfFrameArgument()
    {
        // フレーム連結ではフレームそのものが各露光。表示中のフレーム(1)を渡しても露光の選択と取り違えず、
        // 全フレームを長秒→短秒の順に返す(レビュー指摘 #4 の修正で frame 引数を足したときの回帰の確認)
        const int width = 4;
        const int height = 3;
        ushort[] values = TestData.MakePattern(width * height * 2, 16);
        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16,
            FrameCount = 2, Hdr = HdrMode.Auto, HdrStages = 2,
        };
        using RawImage image = TestImages.FromCodes(values, format);

        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image, format, frame: 1);

        Assert.Equal(2, frames.Count);
        for (int f = 0; f < 2; f++)
        {
            Assert.Equal(height, frames[f].Height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal(image.GetPixel(x, y, f), frames[f].GetPixel(x, y));
                }
            }
        }
    }

    [Fact]
    public void Split_NoHdrMode_Throws()
    {
        ushort[] values = TestData.MakePattern(4, 16);
        var format = new RawFormat { Width = 2, Height = 2, BitDepth = 16 };
        using RawImage image = TestImages.FromCodes(values, format);
        Assert.Throws<InvalidOperationException>(() => HdrSplitter.Split(image));
    }
}

/// <summary>
/// HDR分割の1億画素制限は、実際にヒープへ展開する画素数で判定する
/// (行交互は指定フレーム1枚分、フレーム連結は使う全フレーム)。
/// </summary>
/// <remarks>
/// 全フレーム合計が1億画素を超える画像が要るため、1.01億画素ぶん(8bitで101,000,000バイト)の
/// rawファイルを長さだけ確保して作り(伸ばした範囲は0として読める)、MemoryMappedFile経由で開く。
/// 同じファイルを幅・高さ・フレーム数の異なるフォーマットで読み替えてクラス内で共有する。
/// </remarks>
public sealed class HdrSplitterPixelLimitTests
    : IClassFixture<HdrSplitterPixelLimitTests.LargeRawFile>
{
    private readonly LargeRawFile _file;

    public HdrSplitterPixelLimitTests(LargeRawFile file)
    {
        _file = file;
    }

    [Fact]
    public void Split_LineInterleavedMultiFrame_LimitsOnlyTheSplitFrame()
    {
        // 1000×1000 の行交互HDRが101フレーム(全体1.01億画素、1フレーム100万画素)。
        // 展開するのは指定フレーム1枚分なのに、全フレーム合計で判定して分割を拒否していた
        var format = new RawFormat
        {
            Width = LargeRawFile.FrameWidth, Height = LargeRawFile.FrameHeight, BitDepth = 8,
            FrameCount = LargeRawFile.FrameCount, Hdr = HdrMode.LineInterleaved, HdrStages = 2,
        };
        Assert.True(format.TotalPixels > RawLoader.DefaultInMemoryPixelThreshold);
        using RawImage image = RawLoader.Load(_file.FilePath, format);

        IReadOnlyList<RawImage> split = HdrSplitter.Split(image, format, frame: 1);
        try
        {
            Assert.Equal(2, split.Count);
            AssertAllCodes(split[0], LargeRawFile.Frame1Long);
            AssertAllCodes(split[1], LargeRawFile.Frame1Short);
        }
        finally
        {
            foreach (RawImage stage in split)
            {
                stage.Dispose();
            }
        }
    }

    [Fact]
    public void Split_LineInterleavedFrameOverLimit_Throws()
    {
        // 1フレームだけで1億画素を超える行交互は、従来どおり分割しない(全画素をヒープへ展開しない)
        var format = new RawFormat
        {
            Width = 10_100, Height = 10_000, BitDepth = 8,
            Hdr = HdrMode.LineInterleaved, HdrStages = 2,
        };
        using RawImage image = RawLoader.Load(_file.FilePath, format);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => HdrSplitter.Split(image, format, frame: 0));
        Assert.Contains("1億画素", ex.Message);
    }

    [Fact]
    public void Split_FrameSequentialOverLimitInTotal_Throws()
    {
        // フレーム連結は全フレーム(=全露光)を展開するので、1フレームが1億画素以下
        // (5,050万画素×2)でも合計で判定する
        var format = new RawFormat
        {
            Width = 10_100, Height = 5_000, BitDepth = 8, FrameCount = 2,
            Hdr = HdrMode.FrameSequential, HdrStages = 2,
        };
        using RawImage image = RawLoader.Load(_file.FilePath, format);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => HdrSplitter.Split(image, format, frame: 1));
        Assert.Contains("1億画素", ex.Message);
    }

    /// <summary>分割結果の全画素が指定の8bitコード(16bit正規化で code&lt;&lt;8)であること。</summary>
    private static void AssertAllCodes(RawImage image, byte code)
    {
        var row = new ushort[image.Width];
        for (int y = 0; y < image.Height; y++)
        {
            image.CopyRegion(0, 0, y, image.Width, 1, row);
            Assert.All(row, value => Assert.Equal(code << 8, value));
        }
    }

    /// <summary>
    /// 1000×1000・8bit・101フレームぶんのrawファイル。先頭2フレームだけ行交互の値を書き、
    /// 残りは長さを確保するだけにする(実データを書かないので作成は速い)。
    /// </summary>
    public sealed class LargeRawFile : IDisposable
    {
        internal const int FrameWidth = 1000;
        internal const int FrameHeight = 1000;
        internal const int FrameCount = 101;
        internal const byte Frame0Long = 100;
        internal const byte Frame0Short = 10;
        internal const byte Frame1Long = 200;
        internal const byte Frame1Short = 20;

        public LargeRawFile()
        {
            string directory = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".raw");
            using var stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write);
            stream.SetLength((long)FrameWidth * FrameHeight * FrameCount);

            // 偶数行=長秒・奇数行=短秒。先頭から連続して書くので、確保だけの範囲は0埋めされない
            var row = new byte[FrameWidth];
            foreach ((byte longCode, byte shortCode) in new[]
                { (Frame0Long, Frame0Short), (Frame1Long, Frame1Short) })
            {
                for (int y = 0; y < FrameHeight; y++)
                {
                    Array.Fill(row, y % 2 == 0 ? longCode : shortCode);
                    stream.Write(row);
                }
            }
        }

        /// <summary>ファイルのパス。</summary>
        public string FilePath { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(FilePath);
            }
            catch (IOException)
            {
                // MMFの解放がOSに反映されるまで削除できないことがある
            }
        }
    }
}

public class HdrMergerTests
{
    /// <summary>整合するシーン(S)から長秒/短秒フレームを合成用に生成する。</summary>
    private static (RawImage Longer, RawImage Shorter, double[] Scene) MakeConsistentPair(
        int width, int height, int ratio, int step, int black = 0,
        BayerPattern bayer = BayerPattern.None)
    {
        int count = width * height;
        var scene = new double[count];
        var longer = new ushort[count];
        var shorter = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            long s = (long)i * step;
            scene[i] = s;
            longer[i] = (ushort)Math.Min(65535, s + black);
            shorter[i] = (ushort)Math.Min(65535, s / ratio + black);
        }

        return (
            TestImages.FromCodes(longer, width, height, bayer: bayer),
            TestImages.FromCodes(shorter, width, height, bayer: bayer),
            scene);
    }

    [Fact]
    public void Merge_ConsistentScene_RecoversSceneExactly()
    {
        // S = i*512, 最大 ≈ 16×65535。短秒 = S/16 (整数厳密)
        const int width = 128;
        const int height = 16;
        (RawImage longFrame, RawImage shortFrame, double[] scene) =
            MakeConsistentPair(width, height, ratio: 16, step: 512);
        using (longFrame)
        using (shortFrame)
        {
            HdrImage merged = HdrMerger.Merge(
                new[] { longFrame, shortFrame }, new HdrMergeParameters(ExposureRatio: 16));

            for (int i = 0; i < scene.Length; i++)
            {
                Assert.True(Math.Abs(merged.Pixels[i] - scene[i]) <= 0.5,
                    $"i={i}: merged={merged.Pixels[i]} scene={scene[i]}");
            }

            Assert.Equal(65535f * 16, merged.FullScale, 1);
        }
    }

    [Fact]
    public void Merge_WithBlackLevel_SubtractsBeforeScaling()
    {
        const int width = 64;
        const int height = 8;
        const int black = 1000;
        (RawImage longFrame, RawImage shortFrame, double[] scene) =
            MakeConsistentPair(width, height, ratio: 16, step: 256, black: black);
        using (longFrame)
        using (shortFrame)
        {
            HdrImage merged = HdrMerger.Merge(
                new[] { longFrame, shortFrame },
                new HdrMergeParameters(ExposureRatio: 16, BlackLevel: black));

            // 長秒が飽和する明部まで全画素でシーンを厳密復元する。明部は短秒から黒を引いてから露光比を
            // 掛けた値になる(短秒の黒を引き忘れると S+16000、スケール後に引くと S+15000 になる)
            for (int i = 0; i < scene.Length; i++)
            {
                Assert.True(Math.Abs(merged.Pixels[i] - scene[i]) <= 0.5,
                    $"i={i}: merged={merged.Pixels[i]} scene={scene[i]}");
            }
        }
    }

    [Fact]
    public void Merge_ThreeStages_RecoversWideScene()
    {
        // S = i*2048, 最大 ≈ 64×65535。r=8: mid=S/8, short=S/64 (整数厳密)
        const int width = 128;
        const int height = 16;
        int count = width * height;
        var scene = new double[count];
        var longFrame = new ushort[count];
        var midFrame = new ushort[count];
        var shortFrame = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            long s = (long)i * 2048;
            scene[i] = s;
            longFrame[i] = (ushort)Math.Min(65535, s);
            midFrame[i] = (ushort)Math.Min(65535, s / 8);
            shortFrame[i] = (ushort)Math.Min(65535, s / 64);
        }

        using RawImage f0 = TestImages.FromCodes(longFrame, width, height);
        using RawImage f1 = TestImages.FromCodes(midFrame, width, height);
        using RawImage f2 = TestImages.FromCodes(shortFrame, width, height);

        HdrImage merged = HdrMerger.Merge(
            new[] { f0, f1, f2 }, new HdrMergeParameters(ExposureRatio: 8));

        for (int i = 0; i < count; i++)
        {
            Assert.True(Math.Abs(merged.Pixels[i] - scene[i]) <= 1.0,
                $"i={i}: merged={merged.Pixels[i]} scene={scene[i]}");
        }

        Assert.Equal(65535f * 64, merged.FullScale, 0);
    }

    [Fact]
    public void Merge_KeepsValuesBelowBlackLevelNegative_QuantizedImageClampsToZero()
    {
        // 黒付近のノイズは黒レベルの上下に分布する。以前は線形化で黒レベル未満を0に切り詰めていたので、
        // 遮光部の平均が上振れしσが小さく出て、「無損失」のはずの float raw(Pixels)も負側を失っていた。
        // Pixels は負値のまま残す(長秒が飽和に遠い暗部なので合成値は長秒の線形値そのもの)。
        // 16bitへの量子化画像は負値を表せないので0になる(付随テキストに明記する)
        const int black = 4096;
        const int width = 4;
        const int height = 2;
        ushort[] longCodes =
        [
            black - 100, black + 100, black - 30, black + 30,
            black + 30, black - 30, black + 100, black - 100,
        ];
        ushort[] shortCodes = Enumerable.Repeat((ushort)black, width * height).ToArray();
        using RawImage longFrame = TestImages.FromCodes(longCodes, width, height);
        using RawImage shortFrame = TestImages.FromCodes(shortCodes, width, height);

        HdrImage merged = HdrMerger.Merge(
            new[] { longFrame, shortFrame },
            new HdrMergeParameters(ExposureRatio: 16, BlackLevel: black));

        Assert.Equal(-100f, merged.Pixels[0]);
        Assert.Equal(100f, merged.Pixels[1]);
        Assert.Equal(-30f, merged.Pixels[2]);
        Assert.Equal(0.0, merged.Pixels.Average(), 6);

        using RawImage quantized = merged.ToRawImage16();
        Assert.Equal(0, quantized.GetPixel(0, 0));
        Assert.Equal(0, quantized.GetPixel(2, 0));
        Assert.True(quantized.GetPixel(1, 0) > 0);
    }

    [Fact]
    public void ToRawImage16_ScalesFullScaleTo65535()
    {
        // S = i*8192、短秒 = S/16(整数厳密)。合成域のフルスケールは 16×65535 なので
        // 65535 への正規化はちょうど 1/16 倍 = i*512(丸め・クランプの影響なし)。
        // Bayerパターンは合成結果と量子化画像の両方へ引き継がれる(カラー現像用)
        const int width = 32;
        const int height = 4;
        (RawImage longFrame, RawImage shortFrame, _) = MakeConsistentPair(
            width, height, ratio: 16, step: 8192, bayer: BayerPattern.Rggb);
        using (longFrame)
        using (shortFrame)
        {
            HdrImage merged = HdrMerger.Merge(
                new[] { longFrame, shortFrame }, new HdrMergeParameters(ExposureRatio: 16));
            Assert.Equal(BayerPattern.Rggb, merged.Bayer);

            using RawImage quantized = merged.ToRawImage16();

            Assert.Equal(width, quantized.Width);
            Assert.Equal(height, quantized.Height);
            Assert.Equal(BayerPattern.Rggb, quantized.Format.Bayer);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal((y * width + x) * 512, quantized.GetPixel(x, y));
                }
            }
        }
    }

    [Fact]
    public void SaveFloatRaw_RoundTripsBytes()
    {
        const int width = 16;
        const int height = 4;
        (RawImage longFrame, RawImage shortFrame, _) =
            MakeConsistentPair(width, height, ratio: 16, step: 4096);
        using (longFrame)
        using (shortFrame)
        {
            HdrImage merged = HdrMerger.Merge(
                new[] { longFrame, shortFrame }, new HdrMergeParameters(ExposureRatio: 16));

            string dir = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".fraw");
            try
            {
                merged.SaveFloatRaw(path);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.Equal(width * height * 4, bytes.Length);
                for (int i = 0; i < width * height; i++)
                {
                    Assert.Equal(merged.Pixels[i], BitConverter.ToSingle(bytes, i * 4));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ToRawImage16_CanceledToken_ThrowsOperationCanceled()
    {
        // 合成ビューへの量子化(1億画素で全画素)も取り消しを受け付ける
        (RawImage longFrame, RawImage shortFrame, _) = MakeConsistentPair(16, 4, ratio: 16, step: 4096);
        using (longFrame)
        using (shortFrame)
        {
            HdrImage merged = HdrMerger.Merge(
                new[] { longFrame, shortFrame }, new HdrMergeParameters(ExposureRatio: 16));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() => merged.ToRawImage16(cts.Token));
        }
    }

    [Fact]
    public void Merge_MismatchedSizes_Throws()
    {
        using RawImage a = TestImages.FromCodes(TestData.MakePattern(4, 16), 2, 2);
        using RawImage b = TestImages.FromCodes(TestData.MakePattern(8, 16), 4, 2);
        Assert.Throws<ArgumentException>(() =>
            HdrMerger.Merge(new[] { a, b }, new HdrMergeParameters()));
    }
}
