using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 表示中の画像に画像演算(ダーク減算・フラット補正・差分)を掛けられるかの判定。
/// </summary>
public class ImageCalculationAvailabilityTests
{
    [Fact]
    public void ColorImage_IsRefused()
    {
        // 演算は輝度で行い、結果はカラーを持たない(輝度だけのグレー画像)へ差し替わる。以前は断らず、
        // RGB の画像が黙ってグレーになり、そのまま保存するとグレーの画像が出力された
        string? reason = ImageCalculationAvailability.Refusal(
            compareMode: false, derivedViewShown: false, colorImage: true);

        Assert.NotNull(reason);
        Assert.Contains("カラー", reason);
    }

    [Fact]
    public void CompareMode_IsRefused()
    {
        // 比較表示中は見えていないメイン画像を差し替えてしまう(ビニング・フィルタと同じく断る)
        string? reason = ImageCalculationAvailability.Refusal(
            compareMode: true, derivedViewShown: false, colorImage: false);

        Assert.NotNull(reason);
        Assert.Contains("比較", reason);
    }

    [Fact]
    public void DerivedView_IsRefused_GrayRawViewIsAllowed()
    {
        Assert.NotNull(ImageCalculationAvailability.Refusal(
            compareMode: false, derivedViewShown: true, colorImage: false));
        Assert.Null(ImageCalculationAvailability.Refusal(
            compareMode: false, derivedViewShown: false, colorImage: false));
    }
}
