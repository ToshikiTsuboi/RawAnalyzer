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
