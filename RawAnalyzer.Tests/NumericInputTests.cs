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

    [Theory]
    [InlineData("１２８", 128)] // IME がオンのまま打った全角数字
    [InlineData("　１２８　", 128)] // 全角スペース
    [InlineData("１．５", 1.5)]
    [InlineData("１。５", 1.5)] // かな入力で "." キーは句点になる
    [InlineData("－５", -5)] // 全角ハイフンマイナス
    [InlineData("ー５", -5)] // かな入力で "-" キーは長音符になる
    [InlineData("−0.25", -0.25)] // U+2212 MINUS SIGN
    [InlineData("4,095", 4095)] // 3桁区切り
    [InlineData("１，０００", 1000)]
    [InlineData("-1,234,567.5", -1234567.5)]
    public void TryParseFinite_AcceptsFullWidthAndThousandsSeparators(string text, double expected)
    {
        Assert.True(NumericInput.TryParseFinite(text, out double value));
        Assert.Equal(expected, value, 10);
    }

    [Theory]
    [InlineData("1,5")] // 小数点のつもりかもしれない。15 と誤読しない
    [InlineData("12,34")]
    [InlineData("1,0000")]
    [InlineData(",100")]
    public void TryParseFinite_RejectsMisplacedSeparators(string text)
    {
        Assert.False(NumericInput.TryParseFinite(text, out _));
    }

    [Fact]
    public void TryParseFinite_AcceptsNegativeMatrixCoefficients()
    {
        // カラーマトリクスは負の係数を取り得るので、有限なら通す
        Assert.True(NumericInput.TryParseFinite("-0.25", out double value));
        Assert.Equal(-0.25, value, 10);
    }
}
