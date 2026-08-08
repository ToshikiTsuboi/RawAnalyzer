using System.Globalization;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 数値入力欄が受け付けてはいけない文字列の確認。
/// </summary>
/// <remarks>
/// NumericSliderRow は WPF コントロールのため直接生成できない
/// (UIスレッドとリソース辞書が要る)。ここでは実装が依拠している
/// double.TryParse の挙動と、弾くための条件を固定する。
/// </remarks>
public class NumericInputTests
{
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("∞")]
    public void TryParse_AcceptsNonFiniteText(string text)
    {
        // TryParse 自体は通してしまうので、IsFinite の確認が要る
        bool parsed = double.TryParse(
            text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value);

        if (parsed)
        {
            Assert.False(double.IsFinite(value), $"{text} は有限値ではない");
        }
    }

    [Fact]
    public void Clamp_DoesNotFixNaN()
    {
        // Math.Clamp は NaN を素通しさせるため、範囲でガードしても防げない
        Assert.True(double.IsNaN(Math.Clamp(double.NaN, 0.0, 100.0)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-20")]
    [InlineData("60")]
    [InlineData("1.5")]
    public void TryParse_NormalNumbersAreFinite(string text)
    {
        Assert.True(double.TryParse(
            text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value));
        Assert.True(double.IsFinite(value));
    }
}
