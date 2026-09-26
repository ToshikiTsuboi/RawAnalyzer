using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class RawLoaderTests
{
    /// <summary>
    /// 往復テストの組み合わせ。復号経路が分かれるところだけを残す:
    /// 8bit は 1 バイト経路で packing / endian を参照しない、
    /// 12bit は shift・packing・endian の全分岐を通す、
    /// 16bit は shift 0 で Lsb / Msb が恒等なので endian のみ、
    /// 10 / 14bit は shift 定数が違うだけなので 1 行ずつ。
    /// </summary>
    public static IEnumerable<object[]> AllCombinations()
    {
        foreach (bool memoryMapped in new[] { false, true })
        {
            yield return new object[] { 8, BitPacking.Lsb, Endianness.Little, memoryMapped };

            foreach (BitPacking packing in new[] { BitPacking.Lsb, BitPacking.Msb })
            {
                foreach (Endianness endian in new[] { Endianness.Little, Endianness.Big })
                {
                    yield return new object[] { 12, packing, endian, memoryMapped };
                }
            }

            foreach (Endianness endian in new[] { Endianness.Little, Endianness.Big })
            {
                yield return new object[] { 16, BitPacking.Lsb, endian, memoryMapped };
            }
        }

        yield return new object[] { 10, BitPacking.Lsb, Endianness.Little, false };
        yield return new object[] { 14, BitPacking.Lsb, Endianness.Little, false };
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Load_RoundTripsKnownPattern(
        int bitDepth, BitPacking packing, Endianness endianness, bool memoryMapped)
    {
        const int width = 8;
        const int height = 4;
        var format = new RawFormat
        {
            Width = width,
            Height = height,
            BitDepth = bitDepth,
            Packing = packing,
            Endianness = endianness,
            HeaderOffset = 32,
        };
        ushort[] raw = TestData.MakePattern(width * height, bitDepth);
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(raw, format));
        try
        {
            long threshold = memoryMapped ? 0 : RawLoader.DefaultInMemoryPixelThreshold;
            using RawImage image = RawLoader.Load(path, format, threshold);
            Assert.Equal(memoryMapped, image.IsMemoryMapped);

            int shift = 16 - bitDepth;
            var region = new ushort[width * height];
            image.CopyRegion(0, 0, 0, width, height, region);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    ushort expected = (ushort)(raw[y * width + x] << shift);
                    Assert.Equal(expected, image.GetPixel(x, y));
                    Assert.Equal(expected, region[y * width + x]);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_MultiFrame_ReadsEachFrame(bool memoryMapped)
    {
        const int width = 4;
        const int height = 3;
        const int frames = 2;
        var format = new RawFormat
        {
            Width = width,
            Height = height,
            BitDepth = 12,
            FrameCount = frames,
            HeaderOffset = 16,
            Hdr = HdrMode.Auto,
        };
        ushort[] raw = TestData.MakePattern(width * height * frames, 12);
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(raw, format));
        try
        {
            long threshold = memoryMapped ? 0 : RawLoader.DefaultInMemoryPixelThreshold;
            using RawImage image = RawLoader.Load(path, format, threshold);
            for (int frame = 0; frame < frames; frame++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        ushort expected = (ushort)(raw[(frame * height + y) * width + x] << 4);
                        Assert.Equal(expected, image.GetPixel(x, y, frame));
                    }
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyRegion_PartialRegion_MatchesGetPixel(bool memoryMapped)
    {
        const int width = 16;
        const int height = 8;
        var format = new RawFormat { Width = width, Height = height, BitDepth = 10 };
        ushort[] raw = TestData.MakePattern(width * height, 10);
        string path = TestData.WriteTempFile(TestData.EncodeRawFile(raw, format));
        try
        {
            long threshold = memoryMapped ? 0 : RawLoader.DefaultInMemoryPixelThreshold;
            using RawImage image = RawLoader.Load(path, format, threshold);

            const int rx = 3;
            const int ry = 2;
            const int rw = 7;
            const int rh = 5;
            var region = new ushort[rw * rh];
            image.CopyRegion(0, rx, ry, rw, rh, region);
            for (int y = 0; y < rh; y++)
            {
                for (int x = 0; x < rw; x++)
                {
                    Assert.Equal(image.GetPixel(rx + x, ry + y), region[y * rw + x]);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>報告値を同期的に記録するIProgress(Progress&lt;T&gt;は同期コンテキスト依存のため)。</summary>
    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value) => Values.Add(value);
    }

    [Fact]
    public void Load_InMemory_ReportsMonotonicProgressEndingAtOne()
    {
        // チャンク(8MB)を複数回またぐサイズにして、途中経過が報告されることを確認する
        var format = new RawFormat { Width = 4096, Height = 2560, BitDepth = 16 };
        string path = TestData.WriteTempFile(new byte[format.FrameSizeInBytes]);
        try
        {
            var progress = new RecordingProgress();
            using RawImage image = RawLoader.Load(
                path, format, RawLoader.DefaultInMemoryPixelThreshold,
                CancellationToken.None, progress);

            Assert.True(progress.Values.Count >= 2, "複数回の進捗報告があること");
            Assert.Equal(1.0, progress.Values[^1], 9);
            for (int i = 1; i < progress.Values.Count; i++)
            {
                Assert.True(progress.Values[i] > progress.Values[i - 1], "進捗は単調増加であること");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MemoryMapped_ReportsCompletion()
    {
        var format = new RawFormat { Width = 64, Height = 64, BitDepth = 16 };
        string path = TestData.WriteTempFile(new byte[format.FrameSizeInBytes]);
        try
        {
            var progress = new RecordingProgress();
            using RawImage image = RawLoader.Load(
                path, format, inMemoryPixelThreshold: 0, CancellationToken.None, progress);

            Assert.True(image.IsMemoryMapped);
            Assert.Equal(1.0, Assert.Single(progress.Values), 9);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_FileTooSmall_Throws()
    {
        var format = new RawFormat { Width = 100, Height = 100, BitDepth = 16 };
        string path = TestData.WriteTempFile(new byte[100]);
        try
        {
            Assert.Throws<InvalidDataException>(() => RawLoader.Load(path, format));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_InvalidFormat_Throws()
    {
        var format = new RawFormat { Width = 100, Height = 100, BitDepth = 9 };
        Assert.Throws<ArgumentException>(() => RawLoader.Load("dummy.raw", format));
    }

    [Theory]
    [InlineData(64 + 1920L * 1080 * 2, 12, 64L, 1, 1920, 1080)] // ヘッダ付き 12bit フル HD
    [InlineData(640L * 480, 8, 0L, 1, 640, 480)]                 // 1 バイト画素
    [InlineData(2L * 4056 * 3040 * 2, 12, 0L, 2, 4056, 3040)]    // 2 フレーム DOL
    public void GuessDimensions_KnownSize_FindsCandidate(
        long fileSize, int bitDepth, long header, int frames, int width, int height)
    {
        var format = new RawFormat
        {
            Width = 1,
            Height = 1,
            BitDepth = bitDepth,
            HeaderOffset = header,
            FrameCount = frames,
            Hdr = frames > 1 ? HdrMode.Auto : HdrMode.None,
        };

        Assert.Contains(
            new DimensionCandidate(width, height), RawLoader.GuessDimensions(fileSize, format));
    }

    [Theory]
    [InlineData(12346L, 0L)]                // 表に無いサイズ
    [InlineData(1920L * 1080 * 2 + 1, 0L)]  // 画素バイト数で割り切れない
    [InlineData(512L, 1024L)]               // ヘッダより小さい
    public void GuessDimensions_NoCandidate_ReturnsEmpty(long fileSize, long header)
    {
        var format = new RawFormat { Width = 1, Height = 1, BitDepth = 16, HeaderOffset = header };
        Assert.Empty(RawLoader.GuessDimensions(fileSize, format));
    }
}
