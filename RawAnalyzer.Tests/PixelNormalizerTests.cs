using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class PixelNormalizerTests
{
    [Theory]
    [InlineData(12, BitPacking.Lsb, 0x0FFF, 0xFFF0)]
    [InlineData(12, BitPacking.Msb, 0x123F, 0x1230)] // MSB詰めは上位を保ち、下位4bitのパディングを落とす
    public void NormalizeValue_AlignsCodeToUpperBits(
        int bitDepth, BitPacking packing, int container, int expected)
    {
        Assert.Equal((ushort)expected,
            PixelNormalizer.NormalizeValue((ushort)container, bitDepth, packing));
    }

    [Theory]
    [InlineData(BitPacking.Lsb)]
    [InlineData(BitPacking.Msb)]
    public void Normalize_LongSpan_UsesVectorAndScalarPaths(BitPacking packing)
    {
        // ベクトル幅(16画素)を超える長さ+端数でSIMDパスとスカラー端数処理の両方を通す。
        // Lsb は左シフト、Msb は下位4bitのマスク。Msb の埋めビットには0でない値を入れ、
        // ベクトル部・端数部のどちらでマスクを落としても値が変わるようにする
        const int count = 67;
        var raw = new ushort[count];
        var source = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            raw[i] = (ushort)(i * 61 % 4096);
            ushort container = packing == BitPacking.Lsb ? raw[i] : (ushort)((raw[i] << 4) | (i & 0xF));
            source[i * 2] = (byte)(container & 0xFF);
            source[i * 2 + 1] = (byte)(container >> 8);
        }

        var destination = new ushort[count];
        PixelNormalizer.Normalize(source, destination, 12, packing, Endianness.Little);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal((ushort)(raw[i] << 4), destination[i]);
        }
    }
}
