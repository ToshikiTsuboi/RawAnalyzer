using RawViewer.Core;
using Xunit;

namespace RawViewer.Tests;

public class RawLoaderTests
{
    public static IEnumerable<object[]> AllCombinations()
    {
        foreach (int depth in new[] { 8, 10, 12, 14, 16 })
        {
            foreach (BitPacking packing in new[] { BitPacking.Lsb, BitPacking.Msb })
            {
                foreach (Endianness endian in new[] { Endianness.Little, Endianness.Big })
                {
                    foreach (bool memoryMapped in new[] { false, true })
                    {
                        yield return new object[] { depth, packing, endian, memoryMapped };
                    }
                }
            }
        }
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
            Hdr = HdrMode.Dol,
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

    [Fact]
    public void GuessDimensions_FullHd12Bit_FindsCandidate()
    {
        var format = new RawFormat { Width = 1, Height = 1, BitDepth = 12, HeaderOffset = 64 };
        long fileSize = 64 + 1920L * 1080 * 2;
        var candidates = RawLoader.GuessDimensions(fileSize, format);
        Assert.Contains(new DimensionCandidate(1920, 1080), candidates);
    }

    [Fact]
    public void GuessDimensions_Vga8Bit_FindsCandidate()
    {
        var format = new RawFormat { Width = 1, Height = 1, BitDepth = 8 };
        var candidates = RawLoader.GuessDimensions(640L * 480, format);
        Assert.Contains(new DimensionCandidate(640, 480), candidates);
    }

    [Fact]
    public void GuessDimensions_TwoFrameDol_FindsCandidate()
    {
        var format = new RawFormat
        {
            Width = 1,
            Height = 1,
            BitDepth = 12,
            FrameCount = 2,
            Hdr = HdrMode.Dol,
        };
        long fileSize = 2L * 4056 * 3040 * 2;
        var candidates = RawLoader.GuessDimensions(fileSize, format);
        Assert.Contains(new DimensionCandidate(4056, 3040), candidates);
    }

    [Fact]
    public void GuessDimensions_NoMatch_ReturnsEmpty()
    {
        var format = new RawFormat { Width = 1, Height = 1, BitDepth = 16 };
        Assert.Empty(RawLoader.GuessDimensions(12346, format));
    }

    [Fact]
    public void GuessDimensions_NotDivisible_ReturnsEmpty()
    {
        var format = new RawFormat { Width = 1, Height = 1, BitDepth = 16 };
        Assert.Empty(RawLoader.GuessDimensions(1920L * 1080 * 2 + 1, format));
    }

    [Fact]
    public void GuessDimensions_SmallerThanHeader_ReturnsEmpty()
    {
        var format = new RawFormat { Width = 1, Height = 1, HeaderOffset = 1024 };
        Assert.Empty(RawLoader.GuessDimensions(512, format));
    }
}
