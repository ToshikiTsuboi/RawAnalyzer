using RawAnalyzer.App.Compare;
using Xunit;

namespace RawAnalyzer.Tests;

public class DisplayConditionsTests
{
    [Fact]
    public void Transfer_ScalesLevelsToTargetBitDepth()
    {
        var source = new DisplaySettings
        {
            GainDb = 6.0,
            Gamma = 2.2,
            Contrast = 1.5,
            BlackCode = 137,
            WhiteCode = 4000,
        };

        DisplaySettings mapped = DisplayConditions.Transfer(source, 12, 8);

        // 137/4095*255 = 8.53 → 9、4000/4095*255 = 249.08 → 249
        Assert.Equal(9, mapped.BlackCode, 10);
        Assert.Equal(249, mapped.WhiteCode, 10);
        // 無次元の値はそのままコピー
        Assert.Equal(6.0, mapped.GainDb, 10);
        Assert.Equal(2.2, mapped.Gamma, 10);
        Assert.Equal(1.5, mapped.Contrast, 10);
    }

    [Fact]
    public void AreEqual_DefaultsAcrossBitDepths_AllKeysEqual()
    {
        DisplaySettings a = DisplaySettings.CreateDefault(12);
        DisplaySettings b = DisplaySettings.CreateDefault(8);

        foreach (ConditionKey key in DisplayConditions.Keys)
        {
            Assert.True(DisplayConditions.AreEqual(key, a, 12, b, 8));
        }
    }

    [Fact]
    public void AreEqual_TransferredLevels_EqualWithinRounding()
    {
        // 転写の丸め誤差(0.5LSB以内)は「一致」と判定される
        var source = new DisplaySettings { BlackCode = 2048, WhiteCode = 3000 };
        DisplaySettings mapped = DisplayConditions.Transfer(source, 12, 8);

        Assert.True(DisplayConditions.AreEqual(ConditionKey.Black, source, 12, mapped, 8));
        Assert.True(DisplayConditions.AreEqual(ConditionKey.White, source, 12, mapped, 8));
    }

    [Fact]
    public void AreEqual_DetectsDifferences()
    {
        var a = new DisplaySettings { WhiteCode = 4095 };
        var gain = a with { GainDb = 6 };
        var black = a with { BlackCode = 100 };

        Assert.False(DisplayConditions.AreEqual(ConditionKey.Gain, a, 12, gain, 12));
        Assert.False(DisplayConditions.AreEqual(ConditionKey.Black, a, 12, black, 12));
        Assert.True(DisplayConditions.AreEqual(ConditionKey.White, a, 12, gain, 12));
    }

    [Theory]
    [InlineData(6.0, "+6.0dB")]
    [InlineData(0.0, "0.0dB")]   // 0には符号を付けない("+0.0;-0.0;0.0" の第3区画)
    [InlineData(-3.5, "-3.5dB")]
    public void Format_Gain_SignsNonZeroValuesOnly(double gainDb, string expected)
    {
        var settings = new DisplaySettings { GainDb = gainDb };
        Assert.Equal(expected, DisplayConditions.Format(ConditionKey.Gain, settings, 12));
    }
}
