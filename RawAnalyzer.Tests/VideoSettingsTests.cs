using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// フレームレート入力と動画品質設定。
/// </summary>
public class VideoSettingsTests
{
    [Theory]
    [InlineData("15 fps", 15)]
    [InlineData("30", 30)]
    [InlineData("7.5", 7.5)]
    public void Parse_ReadsNumberFromComboText(string text, double expected)
    {
        Assert.Equal(expected, FpsInput.Parse(text, fallback: 15), 10);
    }

    [Theory]
    [InlineData(null)]  // 空欄・空白と同じ IsNullOrWhiteSpace の分岐
    [InlineData("fps")] // 入力途中の単位だけ(数字なし)
    public void Parse_FallsBackWhenUnreadable(string? text)
    {
        // 入力途中で再生速度が飛ばないように、読めないときは既定値のまま
        Assert.Equal(15, FpsInput.Parse(text, fallback: 15), 10);
    }

    [Theory]
    [InlineData("0", 15)]     // 0は不正(ゼロ除算になる)ので既定値
    [InlineData("999", 240)]  // 上限へクランプ
    [InlineData("0.01", 0.1)] // 下限へクランプ
    public void Parse_ClampsToSupportedRange(string text, double expected)
    {
        Assert.Equal(expected, FpsInput.Parse(text, fallback: 15), 10);
    }

    [Fact]
    public void ParseInteger_RoundsAndKeepsAtLeastOne()
    {
        Assert.Equal(8, FpsInput.ParseInteger("7.5", fallback: 15));
        Assert.Equal(1, FpsInput.ParseInteger("0.4", fallback: 15));
        Assert.Equal(240, FpsInput.ParseInteger("1000", fallback: 15));
    }

    [Theory]
    [InlineData("３０", 30)]       // IME がオンのまま打った全角数字(以前は黙って既定値の 15 になった)
    [InlineData("２４ ｆｐｓ", 24)]
    [InlineData("７．５", 7.5)]
    [InlineData("７。５", 7.5)]    // かな入力で "." キーは句点になる
    [InlineData("30 f", 30)]       // 単位を打っている途中も数字を読む(再生速度が既定値へ飛ばない)
    [InlineData("1,000", 240)]     // 3桁区切り(以前は 1 と読んだ)
    public void Parse_ReadsFullWidthAndGroupedNumbers(string text, double expected)
    {
        Assert.Equal(expected, FpsInput.Parse(text, fallback: 15), 10);
    }

    /// <summary>
    /// 小数点のつもりかもしれないカンマ("7,5")は、他の数値入力欄(NumericInput)と同じく読めない入力として既定値へ
    /// 落とし、そのことを示す。以前はカンマの手前だけを読んで 7 fps と黙って誤読した。
    /// </summary>
    [Theory]
    [InlineData("7,5")]
    [InlineData("７，５ fps")]
    [InlineData("1,5000")]
    public void Parse_AmbiguousCommaIsNotMisread(string text)
    {
        Assert.Equal(15, FpsInput.Parse(text, fallback: 15, out string? notice), 10);
        Assert.Contains("読めない", notice);
    }

    [Theory]
    [InlineData("-5")]  // 以前は符号を無視して 5 fps と読んだ
    [InlineData("－５")]
    [InlineData("ー５")] // かな入力で "-" キーは長音符になる
    public void Parse_NegativeFallsBackLikeZero(string text)
    {
        Assert.Equal(15, FpsInput.Parse(text, fallback: 15), 10);
    }

    /// <summary>
    /// 既定値へ落としたこと・範囲へ収めたことを説明で返す(入力欄の赤枠とツールチップに出す)。
    /// 以前は黙って落としたので、打った値と違う速さで再生・書き出しになったことが見えなかった。
    /// </summary>
    [Theory]
    [InlineData(null, 15, "空のため、既定の 15 fps を使います")]
    [InlineData("abc", 15, "「abc」からフレームレートを読めないため、既定の 15 fps を使います")]
    [InlineData("-5", 15, "-5 fps は使えない")]
    [InlineData("0", 15, "0 fps は使えない")]
    [InlineData("999", 240, "999 fps は範囲 0.1〜240 fps の外のため、240 fps を使います")]
    [InlineData("0.01", 0.1, "0.01 fps は範囲 0.1〜240 fps の外のため、0.1 fps を使います")]
    public void Parse_ReportsFallbackAndClamping(string? text, double expected, string expectedNotice)
    {
        Assert.Equal(expected, FpsInput.Parse(text, fallback: 15, out string? notice), 10);
        Assert.NotNull(notice);
        Assert.Contains(expectedNotice, notice);
    }

    [Theory]
    [InlineData("15 fps")]
    [InlineData("7.5")]
    [InlineData("３０")]
    [InlineData("0.1")]
    [InlineData("240")]
    public void Parse_UsableInput_HasNoNotice(string text)
    {
        FpsInput.Parse(text, fallback: 15, out string? notice);
        Assert.Null(notice);
    }

    [Theory]
    [InlineData("0.4", 1, "範囲 1〜240 fps の外のため、1 fps を使います")] // 書き出しは整数の 1 fps から
    [InlineData("1000", 240, "範囲 1〜240 fps の外のため、240 fps を使います")]
    [InlineData("abc", 15, "既定の 15 fps を使います")]
    [InlineData("29.97", 30, null)] // 整数への丸めは知らせない(書き出しの仕様)
    [InlineData("1", 1, null)]
    public void ParseInteger_ReportsFallbackAndClampingButNotRounding(
        string text, int expected, string? expectedNotice)
    {
        Assert.Equal(expected, FpsInput.ParseInteger(text, fallback: 15, out string? notice));
        if (expectedNotice is null)
        {
            Assert.Null(notice);
        }
        else
        {
            Assert.Contains(expectedNotice, notice);
        }
    }

    [Theory]
    [InlineData("12.5", 13)] // 以前は銀行丸めで 12(13.5 は 14)と .5 の向きが値でそろわなかった
    [InlineData("13.5", 14)]
    [InlineData("24.5", 25)]
    public void ParseInteger_RoundsHalfAwayFromZero(string text, int expected)
    {
        Assert.Equal(expected, FpsInput.ParseInteger(text, fallback: 15));
    }

    [Fact]
    public void Quality_IncreasesBitrateAndJpegQuality()
    {
        VideoQuality[] ascending =
        {
            VideoQuality.Standard, VideoQuality.High,
            VideoQuality.Highest, VideoQuality.NearLossless,
        };

        for (int i = 1; i < ascending.Length; i++)
        {
            Assert.True(
                VideoQualitySettings.EncoderQuality(ascending[i])
                > VideoQualitySettings.EncoderQuality(ascending[i - 1]),
                $"encoder quality must increase at {ascending[i]}");
            Assert.True(
                VideoQualitySettings.BitsPerPixel(ascending[i])
                > VideoQualitySettings.BitsPerPixel(ascending[i - 1]),
                $"bpp must increase at {ascending[i]}");
            Assert.True(
                VideoQualitySettings.JpegQuality(ascending[i])
                > VideoQualitySettings.JpegQuality(ascending[i - 1]),
                $"JPEG quality must increase at {ascending[i]}");
        }

        // JPEG品質はエンコーダの有効範囲に収まっていること
        foreach (VideoQuality quality in ascending)
        {
            Assert.InRange(VideoQualitySettings.JpegQuality(quality), 1, 100);
            Assert.InRange(VideoQualitySettings.EncoderQuality(quality), 1, 100);
        }
    }
}
