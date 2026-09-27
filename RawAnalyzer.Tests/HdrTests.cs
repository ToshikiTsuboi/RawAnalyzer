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
    public void Split_ExplicitLineInterleaved_WorksWithMultipleFrames()
    {
        // 従来はフレーム数から推定していたため、
        // 「複数フレーム かつ 各フレーム内が行交互」を表現できなかった
        const int width = 2;
        const int height = 4;
        const int frames = 2;
        var values = new ushort[width * height * frames];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (ushort)(i / width * 10);
        }

        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16, FrameCount = frames,
            Hdr = HdrMode.LineInterleaved, HdrStages = 2,
        };
        using RawImage image = TestImages.FromCodes(values, format);

        IReadOnlyList<RawImage> split = HdrSplitter.Split(image, format);
        try
        {
            // 先頭フレーム内の行交互として分割される
            Assert.Equal(2, split.Count);
            Assert.Equal(2, split[0].Height);
            Assert.Equal(0, split[0].GetPixel(0, 0));
            Assert.Equal(10, split[1].GetPixel(0, 0));
        }
        finally
        {
            foreach (RawImage frame in split)
            {
                frame.Dispose();
            }
        }
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

    [Fact]
    public void Split_FrameSequential_UsesAllFramesRegardlessOfFrameArgument()
    {
        // フレーム連結ではフレームそのものが各露光。表示中のフレームを渡しても
        // 露光の選択と取り違えず、全フレームを長秒→短秒の順に返す
        const int width = 2;
        const int height = 2;
        var values = new ushort[width * height * 2];
        Array.Fill(values, (ushort)1000, 0, width * height);
        Array.Fill(values, (ushort)100, width * height, width * height);
        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16, FrameCount = 2,
            Hdr = HdrMode.FrameSequential, HdrStages = 2,
        };
        using RawImage image = TestImages.FromCodes(values, format);

        IReadOnlyList<RawImage> split = HdrSplitter.Split(image, format, frame: 1);
        try
        {
            Assert.Equal(2, split.Count);
            AssertAllPixels(split[0], 1000);
            AssertAllPixels(split[1], 100);
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
    public void Split_FrameSequential_ReturnsEachFrame()
    {
        const int width = 4;
        const int height = 3;
        ushort[] values = TestData.MakePattern(width * height * 2, 16);
        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16,
            FrameCount = 2, Hdr = HdrMode.Auto, HdrStages = 2,
        };
        using RawImage image = TestImages.FromCodes(values, format);

        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image);

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

            // 長秒が飽和しない範囲(S+black ≤ 65535)ではシーンを厳密復元
            for (int i = 0; i < scene.Length; i++)
            {
                if (scene[i] + black > 65535 * 0.8)
                {
                    break;
                }

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

    [Theory]
    [InlineData(12, 2, 0.0)]  // step=16 = 12bit素材の正規化LSB → 無損失
    [InlineData(14, 2, 2.0)]  // LSB=4, step=16 → 2bit
    public void LostBits_IsRelativeToSourceBitDepth(int bitDepth, int stages, double expected)
    {
        // 16bitコンテナのLSB基準で数えると (16-N)bit ぶん過大になる回帰の確認
        var format = new RawFormat { Width = 4, Height = 4, BitDepth = bitDepth };
        var frames = new RawImage[stages];
        try
        {
            for (int i = 0; i < stages; i++)
            {
                frames[i] = TestImages.FromCodes(new ushort[16], format);
            }

            HdrImage merged = HdrMerger.Merge(
                frames, new HdrMergeParameters(ExposureRatio: 16));

            Assert.Equal(bitDepth, merged.SourceBitDepth);
            Assert.Equal(expected, merged.LostBits, 6);
        }
        finally
        {
            foreach (RawImage? frame in frames)
            {
                frame?.Dispose();
            }
        }
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
    public void Merge_MismatchedSizes_Throws()
    {
        using RawImage a = TestImages.FromCodes(TestData.MakePattern(4, 16), 2, 2);
        using RawImage b = TestImages.FromCodes(TestData.MakePattern(8, 16), 4, 2);
        Assert.Throws<ArgumentException>(() =>
            HdrMerger.Merge(new[] { a, b }, new HdrMergeParameters()));
    }
}
