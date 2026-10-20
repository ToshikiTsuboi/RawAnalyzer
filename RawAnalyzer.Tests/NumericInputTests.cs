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
    [InlineData("0,125")]  // 3桁区切りの数は "0," で始まらない。小数点のつもりのカンマとみなし 125 と誤読しない
    [InlineData("0,001")]
    [InlineData("-0,250")]
    [InlineData("000,123")]
    [InlineData("０，１２５")]
    public void TryParseFinite_RejectsMisplacedSeparators(string text)
    {
        Assert.False(NumericInput.TryParseFinite(text, out _));
    }

    [Theory]
    [InlineData("６４０", 640)]
    [InlineData("１，９２０", 1920)]
    [InlineData("ー２", -2)]
    [InlineData(" 12 ", 12)]
    public void TryParseInteger_AcceptsFullWidthAndThousandsSeparators(string text, long expected)
    {
        Assert.True(NumericInput.TryParseInteger(text, out long value));
        Assert.Equal(expected, value);
        Assert.True(NumericInput.TryParseInteger(text, out int narrow));
        Assert.Equal(expected, narrow);
    }

    [Theory]
    [InlineData("1.5")]   // 整数でない
    [InlineData("1,5")]   // 3桁区切りでないカンマ
    [InlineData("0,125")] // 先頭の群が 0 の3桁区切りはない(125 と誤読しない)
    [InlineData("000,123")]
    [InlineData("-0,250")]
    [InlineData("1e3")]
    [InlineData("abc")]
    [InlineData("")]
    public void TryParseInteger_RejectsNonIntegers(string text)
    {
        Assert.False(NumericInput.TryParseInteger(text, out long _));
        Assert.False(NumericInput.TryParseInteger(text, out int _));
    }

    [Fact]
    public void TryParseInteger_Int32RejectsOutOfRange()
    {
        Assert.False(NumericInput.TryParseInteger("3,000,000,000", out int _));
        Assert.True(NumericInput.TryParseInteger("3,000,000,000", out long wide));
        Assert.Equal(3_000_000_000L, wide);
    }

    /// <summary>
    /// 対になっていないサロゲートを含む文字列(壊れた UTF-16 のクリップボードの内容など)は、"abc" と同じく
    /// 読めない入力として false を返す。以前は NFKC の正規化が ArgumentException を投げ、TextChanged から
    /// 「予期しないエラー」のダイアログまで漏れた。
    /// </summary>
    /// <remarks>
    /// InlineData に書くとテストの列挙で文字列が直列化され、単独サロゲートが U+FFFD に置き換わって再現しないので、
    /// テストの中で組み立てる。
    /// </remarks>
    [Fact]
    public void TryParse_LoneSurrogate_ReturnsFalse()
    {
        foreach (string text in LoneSurrogateTexts)
        {
            Assert.False(NumericInput.TryParseFinite(text, out _));
            Assert.False(NumericInput.TryParsePositive(text, out _));
            Assert.False(NumericInput.TryParseInteger(text, out long _));
            Assert.False(NumericInput.TryParseInteger(text, out int _));
        }
    }

    /// <summary>対になっていないサロゲートを含む文字列("1"+U+D800、U+DC00 単体、"12"+U+D83D)。</summary>
    internal static readonly string[] LoneSurrogateTexts =
    {
        "1" + (char)0xD800,
        ((char)0xDC00).ToString(),
        "12" + (char)0xD83D,
    };

    [Fact]
    public void TryParseFinite_AcceptsNegativeMatrixCoefficients()
    {
        // カラーマトリクスは負の係数を取り得るので、有限なら通す
        Assert.True(NumericInput.TryParseFinite("-0.25", out double value));
        Assert.Equal(-0.25, value, 10);
    }
}
