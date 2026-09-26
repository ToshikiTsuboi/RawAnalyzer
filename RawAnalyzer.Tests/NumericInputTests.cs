using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 数値入力欄の解釈(NumericInput)。
/// double.TryParse は "NaN" や "Infinity" も受け付け、NaN は大小比較をすり抜けるため、
/// 有限値だけを通すことを確認する(Codexレビュー 2026-08-23 #9 の回帰)。
/// </summary>
public class NumericInputTests
{
    [Theory]
    [InlineData("NaN")] // 非有限(Infinity / -Infinity も同じ IsFinite 分岐)
    [InlineData("0")]   // 正でない(> 0 の境界)
    [InlineData("abc")] // 解析失敗
    public void TryParsePositive_RejectsNonFiniteAndNonPositive(string text)
    {
        Assert.False(NumericInput.TryParsePositive(text, out _));
    }

    [Fact]
    public void TryParseFinite_RejectsNonFinite()
    {
        Assert.False(NumericInput.TryParseFinite("NaN", out _));
    }

    [Fact]
    public void TryParseFinite_AcceptsNegativeMatrixCoefficients()
    {
        // カラーマトリクスは負の係数を取り得るので、有限なら通す
        Assert.True(NumericInput.TryParseFinite("-0.25", out double value));
        Assert.Equal(-0.25, value, 10);
    }
}
