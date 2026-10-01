using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// ファイルを開くときのフォーマット候補(FormatCandidates)の作り方と順序。
/// </summary>
public class FormatCandidatesTests
{
    // 640×480 12bit(2byte/画素)1フレーム
    private const long Size = 640 * 480 * 2;
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RawFormat Fmt(
        int width, int height, int bitDepth = 12, BayerPattern bayer = BayerPattern.None)
    {
        return new RawFormat { Width = width, Height = height, BitDepth = bitDepth, Bayer = bayer };
    }

    private static FormatHistory History(params (long Size, string Extension, RawFormat Format)[] memories)
    {
        // 後に書いたものほど新しい
        var history = new FormatHistory();
        for (int i = 0; i < memories.Length; i++)
        {
            history.Record(memories[i].Size, memories[i].Extension, memories[i].Format, null,
                T0.AddMinutes(i));
        }

        return history;
    }

    [Fact]
    public void Build_OrdersBySourceAndDropsDuplicates()
    {
        RawFormat exact = Fmt(640, 480, bayer: BayerPattern.Rggb);
        RawFormat other = Fmt(640, 240, bayer: BayerPattern.Gbrg);
        FormatHistory history = History(
            (Size, ".raw", exact),
            (640 * 240 * 2, ".raw", other));
        RawFormat displayed = Fmt(480, 640, bitDepth: 16);

        IReadOnlyList<FormatCandidate> candidates = FormatCandidates.Build(
            Size, ".raw", @"D:\data\a_320x960.raw", history, displayed, maxCount: 20);

        var expected = new[]
        {
            new FormatCandidate(exact, FormatCandidateSource.SameSizeMemory),
            new FormatCandidate(displayed, FormatCandidateSource.DisplayedImage),
            new FormatCandidate(other with { FrameCount = 2 }, FormatCandidateSource.MemoryFrameCount),
            new FormatCandidate(other with { Height = 480 }, FormatCandidateSource.MemoryHeight),

            // 記憶の組み合わせ×解像度表(640×480 の Gbrg・Rggb)は上と重複するので出ない。
            // ファイル名は記憶の組み合わせ(新しい順)→既定値
            new FormatCandidate(Fmt(320, 960, bayer: BayerPattern.Gbrg), FormatCandidateSource.FileName),
            new FormatCandidate(Fmt(320, 960, bayer: BayerPattern.Rggb), FormatCandidateSource.FileName),
            new FormatCandidate(Fmt(320, 960), FormatCandidateSource.FileName),
            new FormatCandidate(Fmt(640, 480), FormatCandidateSource.ResolutionTable),
        };
        Assert.Equal(expected, candidates);

        // 件数の上限で打ち切る(順序はそのまま)
        Assert.Equal(expected.Take(3), FormatCandidates.Build(
            Size, ".raw", @"D:\data\a_320x960.raw", history, displayed, maxCount: 3));
        Assert.Equal(10, FormatCandidates.DefaultMaxCount);
    }

    [Fact]
    public void Build_SameSizeMemoriesComeNewestFirstAndKeepTrailingData()
    {
        // 末尾に余りのあるファイル。同じキーの記憶は記録時にこのサイズで開けたのでそのまま候補にする
        const long size = Size + 100;
        RawFormat older = Fmt(640, 480);
        RawFormat newer = Fmt(640, 480, bayer: BayerPattern.Bggr);
        RawFormat withHeader = Fmt(640, 480, bitDepth: 10) with { HeaderOffset = 100 };
        FormatHistory history = History(
            (size, ".raw", older),
            (size, ".raw", newer),
            (size, ".bin", withHeader));

        IReadOnlyList<FormatCandidate> candidates = FormatCandidates.Build(
            size, ".RAW", "x_640x480.raw", history, displayedFormat: Fmt(640, 480, bitDepth: 16));

        Assert.Equal(
            new[]
            {
                new FormatCandidate(newer, FormatCandidateSource.SameSizeMemory),
                new FormatCandidate(older, FormatCandidateSource.SameSizeMemory),

                // ヘッダ込みでちょうど合う別キーの記憶はそのまま(フレーム数は変わらない)
                new FormatCandidate(withHeader, FormatCandidateSource.MemoryFrameCount),
            },
            candidates);

        // 表示中の画像(余りの分だけ合わない)・ファイル名(ヘッダなしでは合わない)は出さない
        Assert.DoesNotContain(candidates, c => c.Source == FormatCandidateSource.DisplayedImage);
        Assert.DoesNotContain(candidates, c => c.Source == FormatCandidateSource.FileName);
    }

    [Fact]
    public void Build_FrameCountVariant_SkipsFrameSequentialAndAutoHdr()
    {
        const long size = 3 * Size;
        RawFormat lineInterleaved = Fmt(640, 480) with { Hdr = HdrMode.LineInterleaved };
        RawFormat frameSequential = Fmt(640, 480) with
        {
            Hdr = HdrMode.FrameSequential, FrameCount = 2, ExposureRatio = 4, HdrLineBlock = 2, HdrRowOffset = 1,
        };
        RawFormat auto = Fmt(640, 480) with { Hdr = HdrMode.Auto, FrameCount = 2 };
        RawFormat threeStage = Fmt(640, 480) with
        {
            Hdr = HdrMode.FrameSequential, HdrStages = 3, FrameCount = 3,
        };
        FormatHistory history = History(
            (Size, ".raw", lineInterleaved),
            (2 * Size, ".raw", frameSequential),
            (2 * Size, ".bin", auto),
            (size, ".bin", threeStage));

        IReadOnlyList<FormatCandidate> candidates = FormatCandidates.Build(
            size, ".raw", "a.raw", history, null, maxCount: 50);

        List<RawFormat> frameVariants = candidates
            .Where(c => c.Source == FormatCandidateSource.MemoryFrameCount)
            .Select(c => c.Format).ToList();

        // 3段フレーム連結はフレーム数を変えずにちょうど合う(意味が変わらない)ので候補にする
        Assert.Equal(new[] { threeStage, lineInterleaved with { FrameCount = 3 } }, frameVariants);

        // フレーム連結・自動(2フレーム)の記憶は、高さだけを変えた1フレームで HDR なしにする
        List<RawFormat> heightVariants = candidates
            .Where(c => c.Source == FormatCandidateSource.MemoryHeight)
            .Select(c => c.Format).ToList();
        // (段数・露光比・ライン単位・行オフセットの HDR の設定も既定へ戻すので、3つの記憶は同じ1つの候補になる。
        // 戻し忘れると HDR なしで露光比4などの余分な候補が増える)
        Assert.Equal(new[] { Fmt(640, 1440), lineInterleaved with { Height = 1440 } }, heightVariants);
    }

    [Fact]
    public void Build_HeightVariant_KeepsWidthHeaderBitDepthPackingEndianAndBayer()
    {
        RawFormat memory = new()
        {
            Width = 1920, Height = 1080, BitDepth = 10, Packing = BitPacking.Msb,
            Endianness = Endianness.Big, Bayer = BayerPattern.Rggb, HeaderOffset = 64,
        };
        const long size = 64 + (1920L * 1200 * 2);
        FormatHistory history = History((64 + (1920L * 1080 * 2), ".raw", memory));

        IReadOnlyList<FormatCandidate> candidates = FormatCandidates.Build(
            size, ".raw", "a.raw", history, null);

        // 同じ組み合わせ×解像度表の 1920×1200 は高さ違いと同じなので重複として出ない
        Assert.Equal(
            new[] { new FormatCandidate(memory with { Height = 1200 }, FormatCandidateSource.MemoryHeight) },
            candidates);
    }

    [Fact]
    public void Build_MemoryCombinationWithResolutionTable()
    {
        RawFormat memory = new()
        {
            Width = 640, Height = 480, BitDepth = 8, Bayer = BayerPattern.Bggr, HeaderOffset = 16,
        };
        FormatHistory history = History((16 + (640 * 480), ".raw", memory));
        const long size = 16 + (1024 * 768);

        IReadOnlyList<FormatCandidate> candidates = FormatCandidates.Build(
            size, ".raw", "a.raw", history, null);

        Assert.Equal(
            new[]
            {
                new FormatCandidate(
                    memory with { Width = 1024, Height = 768 }, FormatCandidateSource.MemoryResolution),
            },
            candidates);
    }

    [Fact]
    public void Build_FileNameDimensions_UseMemoryCombinationsThenDefaults()
    {
        // 幅 999 の記憶はこのサイズのフレーム数・高さ違いにならない(組み合わせだけを使う)
        RawFormat memory = new()
        {
            Width = 999, Height = 10, BitDepth = 10, Packing = BitPacking.Msb,
            Endianness = Endianness.Big,
        };
        FormatHistory history = History((999 * 10 * 2, ".raw", memory));
        const long size = 1000 * 600 * 2;

        IReadOnlyList<FormatCandidate> candidates = FormatCandidates.Build(
            size, ".raw", "cam_1000x600_A.raw", history, null);

        Assert.Equal(
            new[]
            {
                new FormatCandidate(memory with { Width = 1000, Height = 600 }, FormatCandidateSource.FileName),
                new FormatCandidate(Fmt(1000, 600), FormatCandidateSource.FileName),
            },
            candidates);
        Assert.Equal(FormatCandidates.DefaultFormat with { Width = 1000, Height = 600 }, candidates[1].Format);
    }

    [Fact]
    public void Build_FileNameDimensions_InOrderWithSingleFrameFirst()
    {
        const long size = 1000 * 600 * 2;

        // 8bit の記憶があると、ファイル名の表記には 8bit → 既定値(12bit)の順で組み合わせを当てる
        // (幅 999 の記憶はこのサイズのフレーム数・高さ違い・解像度表の候補にならない)
        FormatHistory history = History((999 * 10, ".raw", Fmt(999, 10, bitDepth: 8)));

        IReadOnlyList<FormatCandidate> candidates = FormatCandidates.Build(
            size, ".raw", "a_500X600_b_1000 × 600.raw", history, null);

        // 先に出てきた表記から。500×600 はどちらも複数フレームでちょうど合う(8bit は 4、12bit は 2)。
        // 1000×600 は組み合わせの順(8bit が先)によらず、1フレームで合う 12bit を 2フレームで合う 8bit より先に出す
        Assert.Equal(
            new[]
            {
                new FormatCandidate(Fmt(500, 600, bitDepth: 8) with { FrameCount = 4 }, FormatCandidateSource.FileName),
                new FormatCandidate(Fmt(500, 600) with { FrameCount = 2 }, FormatCandidateSource.FileName),
                new FormatCandidate(Fmt(1000, 600), FormatCandidateSource.FileName),
                new FormatCandidate(Fmt(1000, 600, bitDepth: 8) with { FrameCount = 2 }, FormatCandidateSource.FileName),
            },
            candidates);
    }

    [Theory]
    [InlineData("A_640X480_B_1280 × 720.bin", "640x480,1280x720")]
    [InlineData("frame_1920x1080x3.raw", "1920x1080")]
    [InlineData("dup_640x480_640x480.raw", "640x480")]
    [InlineData("big_12345x67890.raw", "12345x67890")]
    [InlineData("123456x100.raw", "")]
    [InlineData("100x123456.raw", "")]   // 後ろも数字に続かないこと(100x12345 を拾わない)
    [InlineData("1x2_3x4.raw", "")]
    [InlineData("00x480.raw", "")]
    [InlineData(@"C:\cap\1920x1080\frame.raw", "")]
    public void ParseFileNameDimensions_FindsWidthByHeightInOrder(string fileName, string expected)
    {
        string actual = string.Join(",", FormatCandidates.ParseFileNameDimensions(fileName)
            .Select(d => $"{d.Width}x{d.Height}"));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Build_OnlyExactFitsExceptSameSizeMemory()
    {
        // 記憶・表示中の画像・ファイル名をいろいろ混ぜても、同じキーの記憶以外は必ずちょうど合う
        FormatHistory history = History(
            (Size, ".raw", Fmt(640, 480)),
            (Size + 10, ".raw", Fmt(640, 480, bitDepth: 8)),
            (Size / 2, ".bin", Fmt(640, 240, bitDepth: 16, bayer: BayerPattern.Grbg)),
            ((1920 * 1080 * 2) + 32, ".raw", Fmt(1920, 1080, bitDepth: 10) with { HeaderOffset = 32 }),
            (3 * Size, ".raw", Fmt(640, 480) with { Hdr = HdrMode.LineInterleaved, FrameCount = 3 }));
        long[] sizes = { Size, Size + 10, Size / 2, 2 * Size, 3 * Size, 1920 * 1080 * 2, 1920 * 1080 * 2 + 32, 12345 };

        foreach (long size in sizes)
        {
            IReadOnlyList<FormatCandidate> candidates = FormatCandidates.Build(
                size, ".raw", "s_640x480_1920x1080.raw", history, Fmt(640, 480, bitDepth: 16),
                maxCount: 100);
            Assert.Equal(candidates.Count, candidates.Select(c => c.Format).Distinct().Count());
            foreach (FormatCandidate candidate in candidates)
            {
                if (candidate.Source == FormatCandidateSource.SameSizeMemory)
                {
                    Assert.True(candidate.Format.RequiredBytes() <= size);
                }
                else
                {
                    Assert.Equal(size, candidate.Format.RequiredBytes());
                }
            }
        }
    }

    [Theory]
    [InlineData(-1L)]
    public void Build_UnknownOrEmptySize_ReturnsNothing(long size)
    {
        FormatHistory history = History((Size, ".raw", Fmt(640, 480)));

        Assert.Empty(FormatCandidates.Build(size, ".raw", "a_640x480.raw", history, Fmt(640, 480)));
    }
}
