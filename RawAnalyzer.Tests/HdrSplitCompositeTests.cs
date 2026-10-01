using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// HDR分割ビューでのノイズ測定の2枚目(raw)を、対象(A)の分割ビューと同じ並びで読む規約の検証。
/// </summary>
/// <remarks>
/// 分割ビューの表示画像は各露光の段を左右に並べた1枚(幅×段数・高さ÷段数)。以前は2枚目の raw を
/// その並置画像のフォーマットのまま読み、ファイルの連続した行を「幅2倍の1行」として読んでいた。
/// 行交互(ライン単位0・行オフセット0)やフレーム連結ではファイルサイズも一致してサイズ警告が出ず、
/// 別の画素同士の差分から σ_temporal・σ_FPN・DR を黙って出していた。
/// </remarks>
public class HdrSplitCompositeTests
{
    private const int Width = 8;
    private const int Height = 8;

    private static ushort[] Distinct(int count, int bitDepth)
    {
        // 画素ごとに違う値(どの画素同士を比べたかが差分に出る)
        int max = (1 << bitDepth) - 1;
        return Enumerable.Range(0, count).Select(i => (ushort)((i * 37 % max) + 1)).ToArray();
    }

    private static RawImage SplitView(RawImage source, RawFormat splitFormat, int frame = 0)
    {
        IReadOnlyList<RawImage> frames = HdrSplitter.Split(source, splitFormat, frame);
        try
        {
            return HdrSplitComposite.Compose(frames, splitFormat);
        }
        finally
        {
            foreach (RawImage f in frames)
            {
                f.Dispose();
            }
        }
    }

    public static TheoryData<RawFormat> HdrFormats() => new()
    {
        // 行交互・Bayer(ライン単位は既定の2行)
        new RawFormat
        {
            Width = Width, Height = Height, BitDepth = 12, Bayer = BayerPattern.Rggb,
            Hdr = HdrMode.LineInterleaved, HdrStages = 2,
        },

        // 行交互・Auto・3段・モノクロ・ライン単位1
        new RawFormat
        {
            Width = Width, Height = 12, BitDepth = 10, Hdr = HdrMode.Auto, HdrStages = 3,
        },

        // フレーム連結(2段)。ファイルは2フレーム分で、並置画像と同じバイト数になる
        new RawFormat
        {
            Width = Width, Height = Height, BitDepth = 12, Bayer = BayerPattern.Grbg,
            Hdr = HdrMode.FrameSequential, HdrStages = 2, FrameCount = 2,
        },
    };

    [Theory]
    [MemberData(nameof(HdrFormats))]
    public void ReferenceRaw_IsSplitLikeTarget_SoIdenticalShotsHaveZeroTemporalNoise(RawFormat format)
    {
        // 同じ内容の別ファイル(同じ撮影を2つのファイルに書いたもの)。同じ画素同士を比べれば差分は0
        ushort[] codes = Distinct(format.Width * format.Height * format.FrameCount, format.BitDepth);
        string pathA = TestData.WriteTempFile(TestData.EncodeRawFile(codes, format));
        string pathB = TestData.WriteTempFile(TestData.EncodeRawFile(codes, format));
        try
        {
            using RawImage a = RawLoader.Load(pathA, format);
            using RawImage target = SplitView(a, format);

            using RawImage reference = HdrSplitComposite.LoadReference(
                pathB, format, targetScaling: null, CancellationToken.None);

            Assert.Equal(target.Width, reference.Width);
            Assert.Equal(target.Height, reference.Height);
            for (int y = 0; y < target.Height; y++)
            {
                for (int x = 0; x < target.Width; x++)
                {
                    Assert.Equal(target.GetPixel(x, y), reference.GetPixel(x, y));
                }
            }

            NoiseMeasurement m = NoiseAnalysis.MeasurePair(
                target, reference, pattern: target.Format.Bayer);
            Assert.Equal(0, m.SigmaTemporal);
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    [Fact]
    public void ReferenceReadFormat_LineInterleavedReadsFirstFrame_FrameSequentialReadsAllStages()
    {
        RawFormat lineInterleaved = new()
        {
            Width = Width, Height = Height, BitDepth = 12, Bayer = BayerPattern.Rggb,
            Hdr = HdrMode.LineInterleaved, FrameCount = 5, HeaderOffset = 64,
        };
        RawFormat frameSequential = lineInterleaved with { Hdr = HdrMode.Auto, FrameCount = 2 };

        RawFormat li = HdrSplitComposite.ReferenceReadFormat(lineInterleaved);
        RawFormat fs = HdrSplitComposite.ReferenceReadFormat(frameSequential);

        // 期待サイズは元の raw ファイルの形式(並置画像の形式ではない)
        Assert.Equal(64 + (Width * Height * 2), li.RequiredBytes());
        Assert.Equal(1, li.FrameCount);
        Assert.Equal(64 + (2 * Width * Height * 2), fs.RequiredBytes());
        Assert.Equal(HdrMode.FrameSequential, HdrSplitter.ResolveLayout(fs, fs.HdrStages));
    }

    [Theory]
    [InlineData(HdrMode.LineInterleaved, 1, 0, 0)]  // 行交互: 2枚目は先頭フレームを分割。Aの元が先頭なら同じデータ
    [InlineData(HdrMode.LineInterleaved, 3, 2, 2)]  // Aの元が3フレーム目なら別データ
    [InlineData(HdrMode.FrameSequential, 2, 1, 0)]  // フレーム連結: 全フレームを分割するので表示フレームによらず同じデータ
    public void EquivalentTargetFrame(HdrMode mode, int frameCount, int sourceFrame, int expected)
    {
        RawFormat format = new()
        {
            Width = Width, Height = Height, BitDepth = 12, Hdr = mode, FrameCount = frameCount,
        };

        Assert.Equal(expected, HdrSplitComposite.EquivalentTargetFrame(format, sourceFrame));
    }
}
