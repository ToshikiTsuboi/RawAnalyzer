using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class RawImportDisclosureTests
{
    [Theory]
    [InlineData(true, false, BitPacking.Lsb, Endianness.Little, false, false, false)]
    [InlineData(true, false, BitPacking.Msb, Endianness.Little, false, false, true)]
    [InlineData(true, false, BitPacking.Lsb, Endianness.Big, false, false, true)]
    [InlineData(true, false, BitPacking.Lsb, Endianness.Little, true, false, true)]
    [InlineData(true, false, BitPacking.Lsb, Endianness.Little, false, true, true)]
    [InlineData(false, false, BitPacking.Msb, Endianness.Big, true, true, false)]
    [InlineData(false, true, BitPacking.Lsb, Endianness.Little, false, false, true)]
    public void Details_OpenOnAppliedNonDefaultsOrInvalidDetails(bool applying, bool invalid,
        BitPacking packing, Endianness endianness, bool header, bool frames, bool expected)
    {
        Assert.Equal(expected, RawImportDisclosure.ShouldExpandDetails(applying, invalid, packing, endianness, header, frames));
    }

    [Theory]
    [InlineData(HdrMode.None, false, false)]
    [InlineData(HdrMode.None, true, true)]
    [InlineData(HdrMode.LineInterleaved, false, true)]
    [InlineData(HdrMode.FrameSequential, false, true)]
    [InlineData(HdrMode.Auto, false, true)]
    public void HdrDetails_ShowForHdrOrInvalidDetails(HdrMode mode, bool invalid, bool expected)
    {
        Assert.Equal(expected, RawImportDisclosure.ShowHdrDetails(mode, invalid));
    }
}
