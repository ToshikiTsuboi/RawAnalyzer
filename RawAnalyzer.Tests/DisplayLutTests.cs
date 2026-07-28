using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class DisplayLutTests
{
    [Fact]
    public void Create_DefaultParameters_MapsEndpoints()
    {
        var lut = DisplayLut.Create(new DisplayParameters());
        Assert.Equal(0, lut.Map(0));
        Assert.Equal(255, lut.Map(65535));
        Assert.InRange(lut.Map(32768), (byte)127, (byte)128);
    }

    [Fact]
    public void Create_DefaultParameters_IsMonotonic()
    {
        var lut = DisplayLut.Create(new DisplayParameters());
        byte previous = 0;
        foreach (byte value in lut.Table)
        {
            Assert.True(value >= previous, "LUTは単調非減少である必要があります。");
            previous = value;
        }
    }

    [Fact]
    public void Create_BlackWhitePoints_ClipOutsideRange()
    {
        var lut = DisplayLut.Create(new DisplayParameters(BlackPoint: 1000, WhitePoint: 60000));
        Assert.Equal(0, lut.Map(0));
        Assert.Equal(0, lut.Map(999));
        Assert.Equal(0, lut.Map(1000));
        Assert.Equal(255, lut.Map(60000));
        Assert.Equal(255, lut.Map(65535));
        Assert.InRange(lut.Map(30500), (byte)127, (byte)128);
    }

    [Fact]
    public void Create_GainTwo_SaturatesAtMidScale()
    {
        var lut = DisplayLut.Create(new DisplayParameters(Gain: 2.0));
        Assert.Equal(0, lut.Map(0));
        Assert.Equal(255, lut.Map(32768));
        Assert.Equal(255, lut.Map(65535));
        Assert.InRange(lut.Map(16384), (byte)127, (byte)128);
    }

    [Fact]
    public void Create_Gamma_BrightensMidtones()
    {
        var lut = DisplayLut.Create(new DisplayParameters(Gamma: 2.2));
        Assert.Equal(0, lut.Map(0));
        Assert.Equal(255, lut.Map(65535));

        // pow(0.5, 1/2.2) ≈ 0.7297 → 186
        Assert.InRange(lut.Map(32768), (byte)185, (byte)187);
    }

    [Fact]
    public void Create_ZeroContrast_MapsEverythingToMidGray()
    {
        var lut = DisplayLut.Create(new DisplayParameters(Contrast: 0.0));
        Assert.Equal(128, lut.Map(0));
        Assert.Equal(128, lut.Map(32768));
        Assert.Equal(128, lut.Map(65535));
    }

    [Fact]
    public void Create_DegenerateRange_ProducesStepFunction()
    {
        var lut = DisplayLut.Create(new DisplayParameters(BlackPoint: 100, WhitePoint: 100));
        Assert.Equal(0, lut.Map(100));
        Assert.Equal(255, lut.Map(101));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Create_InvalidGamma_Throws(double gamma)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DisplayLut.Create(new DisplayParameters(Gamma: gamma)));
    }

    [Fact]
    public void Apply_MapsEveryPixelThroughTable()
    {
        var lut = DisplayLut.Create(new DisplayParameters());
        ushort[] source = { 0, 256, 32768, 65535 };
        var destination = new byte[source.Length];
        lut.Apply(source, destination);
        for (int i = 0; i < source.Length; i++)
        {
            Assert.Equal(lut.Map(source[i]), destination[i]);
        }
    }

    [Fact]
    public void Apply_DestinationTooShort_Throws()
    {
        var lut = DisplayLut.Create(new DisplayParameters());
        Assert.Throws<ArgumentException>(() => lut.Apply(new ushort[4], new byte[3]));
    }

    [Fact]
    public void Table_Has65536Entries()
    {
        var lut = DisplayLut.Create(new DisplayParameters());
        Assert.Equal(65536, lut.Table.Length);
    }
}
