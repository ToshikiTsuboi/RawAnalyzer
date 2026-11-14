using System.Buffers.Binary;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class RawPreviewReaderTests
{
    [Theory]
    [InlineData(BayerPattern.None, 3, 4, 3)]
    [InlineData(BayerPattern.Rggb, 4, 3, 2)]
    public void ReadDecimated_SamplesOriginAndUsesEvenBayerStep(
        BayerPattern bayer, int step, int width, int height)
    {
        using var stream = new MemoryStream(Enumerable.Range(0, 88).Select(i => (byte)i).ToArray());
        var format = new RawFormat { Width = 11, Height = 8, BitDepth = 8, Bayer = bayer };
        RawPreview preview = RawPreviewReader.ReadDecimated(stream, format, 4, 3);
        Assert.Equal((width, height, height, (long)step), (preview.Width, preview.Height, preview.RowsRead, preview.Step));
        ushort[] expected = (from y in Enumerable.Range(0, height)
                             from x in Enumerable.Range(0, width)
                             select (ushort)((y * step * 11 + x * step) << 8)).ToArray();
        Assert.Equal(expected, preview.Pixels);
    }

    [Fact]
    public void ReadDecimated_PreservesSmallBayerMosaicAndInterleavedHdrOrder()
    {
        var format = new RawFormat { Width = 3, Height = 4, BitDepth = 8,
            Bayer = BayerPattern.Bggr, Hdr = HdrMode.LineInterleaved, HdrLineBlock = 1 };
        byte[] data = [1, 10, 2, 31, 41, 32, 3, 11, 4, 33, 43, 34];
        using var stream = new MemoryStream(data);
        RawPreview preview = RawPreviewReader.ReadDecimated(stream, format, 256, 256);
        Assert.Equal(1, preview.Step);
        Assert.Equal(data.Select(value => (ushort)(value << 8)), preview.Pixels);
    }

    [Theory]
    [InlineData(8, BitPacking.Lsb, Endianness.Little)]
    [InlineData(10, BitPacking.Lsb, Endianness.Little)]
    [InlineData(10, BitPacking.Msb, Endianness.Big)]
    [InlineData(12, BitPacking.Lsb, Endianness.Big)]
    [InlineData(12, BitPacking.Msb, Endianness.Little)]
    [InlineData(14, BitPacking.Lsb, Endianness.Big)]
    [InlineData(14, BitPacking.Msb, Endianness.Little)]
    [InlineData(16, BitPacking.Lsb, Endianness.Big)]
    public void ReadDecimated_NormalizesAfterHeaderAndReadsOnlyFirstFrame(int bits, BitPacking packing, Endianness endian)
    {
        var format = new RawFormat { Width = 2, Height = 2, BitDepth = bits, Packing = packing,
            Endianness = endian, HeaderOffset = 3, FrameCount = 2 };
        byte[] data = new byte[format.RequiredBytes()];
        Array.Fill(data, (byte)0xff);
        int shift = 16 - bits;
        ushort[] codes = [0, 1, 0x95, (ushort)((1 << bits) - 1)];
        for (int i = 0; i < codes.Length; i++)
        {
            Span<byte> destination = data.AsSpan(3 + i * format.BytesPerPixel, format.BytesPerPixel);
            if (bits == 8)
            {
                destination[0] = (byte)codes[i];
                continue;
            }

            ushort container = packing == BitPacking.Lsb
                ? (ushort)(codes[i] | (0xffff << bits))
                : (ushort)((codes[i] << shift) | ((1 << shift) - 1));
            if (endian == Endianness.Big)
            {
                BinaryPrimitives.WriteUInt16BigEndian(destination, container);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(destination, container);
            }
        }

        using var stream = new MemoryStream(data);
        RawPreview preview = RawPreviewReader.ReadDecimated(stream, format, 2, 2);
        Assert.Equal(2, preview.RowsRead);
        Assert.Equal(codes.Select(code => (ushort)(code << shift)), preview.Pixels);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(1000, 1)]
    [InlineData(1, int.MaxValue)]
    public void ReadDecimated_HandlesNarrowShapes(int width, int height)
    {
        var format = new RawFormat { Width = width, Height = height, BitDepth = 8, Bayer = BayerPattern.Grbg };
        using var stream = new CountingStream(format.RequiredBytes());
        RawPreview preview = RawPreviewReader.ReadDecimated(stream, format, 64, 64);
        Assert.InRange(preview.Width, 1, 64);
        Assert.InRange(preview.Height, 1, 64);
        Assert.Equal(preview.Height, preview.RowsRead);
        Assert.Equal(preview.RowsRead * width, stream.BytesRead);
        Assert.Equal(preview.RowsRead, stream.ReadCalls);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(10, 1)]
    [InlineData(14, 2)]
    public void ReadDecimated_ReturnsOnlyCompleteRowsFromShortFiles(int length, int rows)
    {
        using var stream = new MemoryStream(new byte[length]);
        var format = new RawFormat { Width = 2, Height = 4, BitDepth = 12, HeaderOffset = 3 };
        RawPreview preview = RawPreviewReader.ReadDecimated(stream, format, 256, 256);
        Assert.Equal((2, 4, rows), (preview.Width, preview.Height, preview.RowsRead));
        Assert.Equal(rows * 2, preview.Pixels.Length);
    }

    [Fact]
    public void ReadDecimated_StopsIfStreamShortensAfterMeasuringLength()
    {
        using var stream = new CountingStream(16) { ReadLimit = 7 };
        var format = new RawFormat { Width = 4, Height = 4, BitDepth = 8 };
        RawPreview preview = RawPreviewReader.ReadDecimated(stream, format, 4, 4);
        Assert.Equal(1, preview.RowsRead);
        Assert.Equal(4, preview.Pixels.Length);
        Assert.Equal(7, stream.BytesRead);
    }

    [Fact]
    public void ReadDecimated_LargeStreamReadsEachSampledRowInOneCall()
    {
        var format = new RawFormat { Width = 32768, Height = 32768, BitDepth = 12, HeaderOffset = 512 };
        using var stream = new CountingStream(format.RequiredBytes());
        RawPreview preview = RawPreviewReader.ReadDecimated(stream, format, 256, 256);
        Assert.Equal((256, 256, 256, 128L), (preview.Width, preview.Height, preview.RowsRead, preview.Step));
        Assert.Equal(preview.RowsRead * format.Width * format.BytesPerPixel, stream.BytesRead);
        Assert.Equal(preview.RowsRead, stream.ReadCalls);
        Assert.Equal(preview.RowsRead, stream.SeekCalls);
    }

    [Fact]
    public void ReadDecimated_FillsARowFromPartialStreamReadsBeforeDecimating()
    {
        var format = new RawFormat { Width = 8, Height = 4, BitDepth = 12 };
        using var stream = new CountingStream(format.RequiredBytes()) { MaxReadSize = 3 };
        RawPreview preview = RawPreviewReader.ReadDecimated(stream, format, 4, 2);
        Assert.Equal((4, 2, 2, 2L), (preview.Width, preview.Height, preview.RowsRead, preview.Step));
        Assert.Equal(32, stream.BytesRead);
        Assert.Equal(2, stream.SeekCalls);
    }

    [Fact]
    public void ReadDecimated_VeryWideRowUsesBoundedConsecutiveReads()
    {
        var format = new RawFormat { Width = 20_000_003, Height = 1, BitDepth = 8 };
        using var stream = new CountingStream(format.RequiredBytes());
        RawPreview preview = RawPreviewReader.ReadDecimated(stream, format, 2, 1);
        Assert.Equal((2, 1, 1, 10_000_002L), (preview.Width, preview.Height, preview.RowsRead, preview.Step));
        Assert.Equal(format.Width, stream.BytesRead);
        Assert.Equal(2, stream.ReadCalls);
        Assert.Equal(1, stream.SeekCalls);
    }

    [Fact]
    public void ReadDecimated_ChecksCancellationBeforeAndDuringReading()
    {
        var format = new RawFormat { Width = 100, Height = 100 };
        using var cts = new CancellationTokenSource();
        using var stream = new CountingStream(format.RequiredBytes()) { OnRead = cts.Cancel };
        Assert.Throws<OperationCanceledException>(() =>
            RawPreviewReader.ReadDecimated(stream, format, 8, 8, cts.Token));
        Assert.Equal(format.Width * format.BytesPerPixel, stream.BytesRead);
        Assert.Equal(1, stream.ReadCalls);
        Assert.Throws<OperationCanceledException>(() =>
            RawPreviewReader.ReadDecimated(stream, format, 8, 8, cts.Token));
        Assert.Equal(1, stream.ReadCalls);
    }

    private sealed class CountingStream(long length) : Stream
    {
        internal int BytesRead { get; private set; }
        internal int ReadCalls { get; private set; }
        internal int SeekCalls { get; private set; }
        internal long ReadLimit { get; init; } = length;
        internal int MaxReadSize { get; init; } = int.MaxValue;
        internal Action? OnRead { get; init; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position { get; set; }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin != SeekOrigin.Begin)
            {
                throw new NotSupportedException();
            }

            SeekCalls++;
            return Position = offset;
        }

        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            int count = (int)Math.Clamp(ReadLimit - Position, 0, Math.Min(buffer.Length, MaxReadSize));
            buffer[..count].Clear();
            Position += count;
            BytesRead += count;
            OnRead?.Invoke();
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
