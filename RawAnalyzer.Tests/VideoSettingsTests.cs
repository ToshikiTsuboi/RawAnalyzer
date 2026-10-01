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

    [Theory]
    [InlineData("-5")]  // 以前は符号を無視して 5 fps と読んだ
    [InlineData("－５")]
    [InlineData("ー５")] // かな入力で "-" キーは長音符になる
    public void Parse_NegativeFallsBackLikeZero(string text)
    {
        Assert.Equal(15, FpsInput.Parse(text, fallback: 15), 10);
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
