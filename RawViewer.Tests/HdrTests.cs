using RawViewer.Core;
using Xunit;

namespace RawViewer.Tests;

public class HdrSplitterTests
{
    private static RawImage LoadImage(ushort[] values, RawFormat format)
    {
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(values, format));
        try
        {
            return RawLoader.Load(path, format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Split_LineInterleaved2Stages_SeparatesEvenOddRows()
    {
        const int width = 4;
        const int height = 6;
        var values = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                values[y * width + x] = (ushort)(y * 1000 + x);
            }
        }

        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16, Hdr = HdrMode.Auto, HdrStages = 2,
        };
        using RawImage image = LoadImage(values, format);

        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image);

        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(3, f.Height));
        Assert.All(frames, f => Assert.Equal(HdrMode.None, f.Format.Hdr));
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Assert.Equal((ushort)(y * 2 * 1000 + x), frames[0].GetPixel(x, y));
                Assert.Equal((ushort)((y * 2 + 1) * 1000 + x), frames[1].GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void Split_LineInterleaved3Stages_SeparatesByRowPeriod()
    {
        const int width = 2;
        const int height = 9;
        var values = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            values[y * width] = (ushort)(y * 100);
            values[y * width + 1] = (ushort)(y * 100 + 1);
        }

        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16,
            Hdr = HdrMode.Auto, HdrStages = 3,
        };
        using RawImage image = LoadImage(values, format);

        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image);

        Assert.Equal(3, frames.Count);
        for (int stage = 0; stage < 3; stage++)
        {
            for (int y = 0; y < 3; y++)
            {
                Assert.Equal((ushort)((y * 3 + stage) * 100), frames[stage].GetPixel(0, y));
            }
        }
    }

    [Fact]
    public void Split_LineInterleavedWithBayer_UsesTwoLineBlocks()
    {
        // Bayerセンサは色ペア(2行)単位で長/短が交互になる
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

        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16,
            Hdr = HdrMode.Auto, HdrStages = 2, Bayer = BayerPattern.Rggb,
        };
        using RawImage image = LoadImage(values, format);

        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image);

        // 長秒 = 行0,1,4,5 / 短秒 = 行2,3,6,7
        Assert.Equal(4, frames[0].Height);
        Assert.Equal(BayerPattern.Rggb, frames[0].Format.Bayer);
        Assert.Equal(0, frames[0].GetPixel(0, 0));
        Assert.Equal(100, frames[0].GetPixel(0, 1));
        Assert.Equal(400, frames[0].GetPixel(0, 2));
        Assert.Equal(500, frames[0].GetPixel(0, 3));
        Assert.Equal(200, frames[1].GetPixel(0, 0));
        Assert.Equal(300, frames[1].GetPixel(0, 1));
        Assert.Equal(600, frames[1].GetPixel(0, 2));
        Assert.Equal(700, frames[1].GetPixel(0, 3));
    }

    [Fact]
    public void Split_WithOverriddenFormat_UsesGivenBayerPattern()
    {
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
        using RawImage image = LoadImage(values, loadedFormat);

        // パネルで RGGB を指定した状態を渡す
        RawFormat panelFormat = loadedFormat with { Bayer = BayerPattern.Rggb };
        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image, panelFormat);

        Assert.Equal(BayerPattern.Rggb, frames[0].Format.Bayer);

        // 2行ブロックで交互 = 長秒は行0,1,4,5
        Assert.Equal(0, frames[0].GetPixel(0, 0));
        Assert.Equal(100, frames[0].GetPixel(0, 1));
        Assert.Equal(400, frames[0].GetPixel(0, 2));
        Assert.Equal(200, frames[1].GetPixel(0, 0));

        foreach (RawImage frame in frames)
        {
            frame.Dispose();
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
        using RawImage image = LoadImage(values, format);

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
        using RawImage image = LoadImage(values, format);

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
    public void Split_OddRowOffsetWithBayer_ShiftsPatternPhase()
    {
        const int width = 2;
        const int height = 16;
        var format = new RawFormat
        {
            Width = width, Height = height, BitDepth = 16,
            Hdr = HdrMode.Auto, HdrStages = 2, Bayer = BayerPattern.Rggb,
            HdrLineBlock = 1, HdrRowOffset = 1,
        };
        using RawImage image = LoadImage(new ushort[width * height], format);

        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image, format);
        try
        {
            // 奇数行から切り出す側は色位相が1行ずれる
            Assert.Equal(BayerPattern.Rggb, frames[0].Format.Bayer);
            Assert.Equal(BayerPattern.Gbrg, frames[1].Format.Bayer);
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
        using RawImage image = LoadImage(new ushort[16], format);

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
    public void ResolveLayout_ExplicitLayoutIgnoresFrameCount()
    {
        // フレーム数4でも「行交互」と明示すればそのまま扱う
        var format = new RawFormat
        {
            Width = 2, Height = 4, Hdr = HdrMode.LineInterleaved, FrameCount = 4, HdrStages = 2,
        };

        Assert.Equal(HdrMode.LineInterleaved, HdrSplitter.ResolveLayout(format, 2));
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
        using RawImage image = LoadImage(values, format);

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
    public void Split_FrameSequentialWithWrongFrameCount_Throws()
    {
        var format = new RawFormat
        {
            Width = 2, Height = 4, BitDepth = 16, FrameCount = 1,
            Hdr = HdrMode.FrameSequential, HdrStages = 2,
        };
        using RawImage image = LoadImage(new ushort[8], format);

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
        using RawImage image = LoadImage(new ushort[width * height], format);

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
        using RawImage image = LoadImage(values, format);

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
        using RawImage image = LoadImage(values, format);
        Assert.Throws<InvalidOperationException>(() => HdrSplitter.Split(image));
    }

    [Fact]
    public void Split_FrameCountMismatch_Throws()
    {
        ushort[] values = TestData.MakePattern(2 * 3 * 2, 16);
        var format = new RawFormat
        {
            Width = 2, Height = 3, BitDepth = 16,
            FrameCount = 2, Hdr = HdrMode.Auto, HdrStages = 3,
        };
        using RawImage image = LoadImage(values, format);
        Assert.Throws<InvalidOperationException>(() => HdrSplitter.Split(image));
    }
}

public class HdrMergerTests
{
    private static RawImage MakeFrame(ushort[] values, int width, int height)
    {
        var format = new RawFormat { Width = width, Height = height, BitDepth = 16 };
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(values, format));
        try
        {
            return RawLoader.Load(path, format);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>整合するシーン(S)から長秒/短秒フレームを合成用に生成する。</summary>
    private static (RawImage Longer, RawImage Shorter, double[] Scene) MakeConsistentPair(
        int width, int height, int ratio, int step, int black = 0)
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

        return (MakeFrame(longer, width, height), MakeFrame(shorter, width, height), scene);
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
    public void Merge_RampIsMonotonicAcrossSaturationBoundary()
    {
        const int width = 256;
        const int height = 8;
        (RawImage longFrame, RawImage shortFrame, _) =
            MakeConsistentPair(width, height, ratio: 16, step: 512);
        using (longFrame)
        using (shortFrame)
        {
            HdrImage merged = HdrMerger.Merge(
                new[] { longFrame, shortFrame }, new HdrMergeParameters(ExposureRatio: 16));

            for (int i = 1; i < merged.Pixels.Length; i++)
            {
                Assert.True(merged.Pixels[i] >= merged.Pixels[i - 1] - 0.01f,
                    $"単調増加が崩れています: i={i}, {merged.Pixels[i - 1]} → {merged.Pixels[i]}");
            }
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

        using RawImage f0 = MakeFrame(longFrame, width, height);
        using RawImage f1 = MakeFrame(midFrame, width, height);
        using RawImage f2 = MakeFrame(shortFrame, width, height);

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
    public void Merge_PreservesBayerPatternForColorDevelop()
    {
        var format = new RawFormat
        {
            Width = 8, Height = 4, BitDepth = 16, Bayer = BayerPattern.Rggb,
        };
        ushort[] values = TestData.MakePattern(8 * 4, 16);
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(values, format));
        RawImage longFrame;
        try
        {
            longFrame = RawLoader.Load(path, format);
        }
        finally
        {
            File.Delete(path);
        }

        string path2 = TestData.WriteTempFile(TestData.EncodeRawFile(values, format));
        RawImage shortFrame;
        try
        {
            shortFrame = RawLoader.Load(path2, format);
        }
        finally
        {
            File.Delete(path2);
        }

        using (longFrame)
        using (shortFrame)
        {
            HdrImage merged = HdrMerger.Merge(
                new[] { longFrame, shortFrame }, new HdrMergeParameters(ExposureRatio: 16));
            Assert.Equal(BayerPattern.Rggb, merged.Bayer);
            using RawImage quantized = merged.ToRawImage16();
            Assert.Equal(BayerPattern.Rggb, quantized.Format.Bayer);
        }
    }

    [Fact]
    public void ToRawImage16_ScalesFullScaleTo65535()
    {
        const int width = 32;
        const int height = 4;
        (RawImage longFrame, RawImage shortFrame, _) =
            MakeConsistentPair(width, height, ratio: 16, step: 8192);
        using (longFrame)
        using (shortFrame)
        {
            HdrImage merged = HdrMerger.Merge(
                new[] { longFrame, shortFrame }, new HdrMergeParameters(ExposureRatio: 16));
            using RawImage quantized = merged.ToRawImage16();

            Assert.Equal(width, quantized.Width);
            Assert.Equal(height, quantized.Height);
            float scale = 65535f / merged.FullScale;
            for (int x = 0; x < width; x++)
            {
                ushort expected = (ushort)Math.Clamp(
                    (int)MathF.Round(merged.Pixels[x] * scale), 0, 65535);
                Assert.Equal(expected, quantized.GetPixel(x, 0));
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

            string dir = Path.Combine(Path.GetTempPath(), "RawViewerTests");
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
        using RawImage a = MakeFrame(TestData.MakePattern(4, 16), 2, 2);
        using RawImage b = MakeFrame(TestData.MakePattern(8, 16), 4, 2);
        Assert.Throws<ArgumentException>(() =>
            HdrMerger.Merge(new[] { a, b }, new HdrMergeParameters()));
    }
}
