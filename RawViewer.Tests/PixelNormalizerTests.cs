using RawViewer.Core;
using Xunit;

namespace RawViewer.Tests;

public class PixelNormalizerTests
{
    [Theory]
    [InlineData(10, BitPacking.Lsb, 0x03FF, 0xFFC0)]
    [InlineData(12, BitPacking.Lsb, 0x0FFF, 0xFFF0)]
    [InlineData(14, BitPacking.Lsb, 0x3FFF, 0xFFFC)]
    [InlineData(16, BitPacking.Lsb, 0xFFFF, 0xFFFF)]
    [InlineData(12, BitPacking.Msb, 0xFFFF, 0xFFF0)]
    public void NormalizeValue_MaxCode_ReachesFullScale(
        int bitDepth, BitPacking packing, int container, int expected)
    {
        Assert.Equal((ushort)expected,
            PixelNormalizer.NormalizeValue((ushort)container, bitDepth, packing));
    }

    [Fact]
    public void NormalizeValue_MsbPacking_MasksPaddingBits()
    {
        // 12bit MSB詰め: 下位4bitはパディングとして落とす
        Assert.Equal((ushort)0x1230,
            PixelNormalizer.NormalizeValue(0x123F, 12, BitPacking.Msb));
    }

    [Fact]
    public void Normalize_LongSpan_UsesVectorAndScalarPaths()
    {
        // ベクトル幅(16画素)を超える長さ+端数でSIMDパスとスカラー端数処理の両方を通す
        const int count = 67;
        var raw = new ushort[count];
        var source = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            raw[i] = (ushort)(i * 61 % 4096);
            source[i * 2] = (byte)(raw[i] & 0xFF);
            source[i * 2 + 1] = (byte)(raw[i] >> 8);
        }

        var destination = new ushort[count];
        PixelNormalizer.Normalize(source, destination, 12, BitPacking.Lsb, Endianness.Little);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal((ushort)(raw[i] << 4), destination[i]);
        }
    }

    [Fact]
    public void Normalize_DestinationTooShort_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            var source = new byte[8];
            var destination = new ushort[3];
            PixelNormalizer.Normalize(source, destination, 16, BitPacking.Lsb, Endianness.Little);
        });
    }
}
