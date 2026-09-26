using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>32bitサンプルから内部16bitコードへの写像。</summary>
public class SampleScalingTests
{
    private static int[] FloatBits(params float[] values)
    {
        var bits = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            bits[i] = BitConverter.SingleToInt32Bits(values[i]);
        }

        return bits;
    }

    [Fact]
    public void ToValue_InterpretsBitsPerSampleFormat()
    {
        int bits = BitConverter.SingleToInt32Bits(1234.5f);
        Assert.Equal(1234.5, SampleScaling.ToValue(bits, SampleInterpretation.Float), 4);

        // 同じビット列でも整数として読めば別の値になる(WICはこの区別をしない)
        Assert.Equal(bits, SampleScaling.ToValue(bits, SampleInterpretation.SignedInteger), 0);
        Assert.Equal(4_000_000_000d,
            SampleScaling.ToValue(unchecked((int)4_000_000_000u), SampleInterpretation.UnsignedInteger), 0);
        Assert.Equal(-5, SampleScaling.ToValue(-5, SampleInterpretation.SignedInteger), 0);
    }

    [Fact]
    public void Normalized_ZeroToOne_FillsFullRange()
    {
        int[] bits = FloatBits(0f, 0.5f, 1f);
        SampleRange range = SampleScaling.Scan(bits, bits.Length, SampleInterpretation.Float);
        SampleScaling scaling = SampleScaling.FromRange(range);

        Assert.True(scaling.IsNormalized);
        Assert.Equal(0, scaling.ToCode(0));
        Assert.Equal(32768, scaling.ToCode(0.5));
        Assert.Equal(65535, scaling.ToCode(1.0));
    }

    [Fact]
    public void AduScale_UsesActualMaximum()
    {
        // 12bit相当の実数(平均フレーム等)。最大がフルスケールになる
        int[] bits = FloatBits(0f, 2047.5f, 4095f);
        SampleScaling scaling = SampleScaling.FromRange(
            SampleScaling.Scan(bits, bits.Length, SampleInterpretation.Float));

        Assert.False(scaling.IsNormalized);
        Assert.Equal(0, scaling.ToCode(0));
        Assert.Equal(65535, scaling.ToCode(4095));
        Assert.Equal(4095.0 / 65535.0, scaling.ValuePerCode, 10);
        Assert.Contains("1code", scaling.Describe());
    }

    [Fact]
    public void Negative_KeepsTailByOffsetting()
    {
        // 暗電流減算後の負のすそを切り捨てるとノイズ評価が狂うため、下端へ寄せて残す
        int[] bits = FloatBits(-100f, 0f, 300f);
        SampleScaling scaling = SampleScaling.FromRange(
            SampleScaling.Scan(bits, bits.Length, SampleInterpretation.Float));

        Assert.Equal(-100, scaling.Offset, 6);
        Assert.Equal(400, scaling.Span, 6);
        Assert.Equal(0, scaling.ToCode(-100));
        Assert.Equal(65535, scaling.ToCode(300));
        Assert.True(scaling.ToCode(0) > 0, "0は下端より上に来ること");
    }

    [Fact]
    public void NonFinite_CountedAndMappedToZero()
    {
        int[] bits = FloatBits(float.NaN, 10f, float.PositiveInfinity, 20f);
        SampleRange range = SampleScaling.Scan(bits, bits.Length, SampleInterpretation.Float);

        Assert.Equal(2, range.NonFiniteCount);
        Assert.Equal(10, range.Minimum, 6);
        Assert.Equal(20, range.Maximum, 6);

        SampleScaling scaling = SampleScaling.FromRange(range);
        Assert.Equal(0, scaling.ToCode(double.NaN));
        Assert.Contains("非数", scaling.Describe());
    }
}
