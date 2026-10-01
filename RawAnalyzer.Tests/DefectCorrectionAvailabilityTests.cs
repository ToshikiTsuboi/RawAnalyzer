using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 表示中の画像の欠陥を補正できるか(欠陥ウィンドウの「この欠陥を補正」と補正の要求の両方で使う)の判定。
/// </summary>
public class DefectCorrectionAvailabilityTests
{
    private static RawFormat Format(HdrMode hdr, int frameCount = 1) => new()
    {
        Width = 8, Height = 8, BitDepth = 12, Bayer = BayerPattern.Rggb, Hdr = hdr, FrameCount = frameCount,
    };

    [Theory]
    [InlineData(HdrMode.LineInterleaved, 1)]
    [InlineData(HdrMode.Auto, 1)]
    [InlineData(HdrMode.FrameSequential, 2)]
    public void HdrMaterialInRawView_IsRefused(HdrMode hdr, int frameCount)
    {
        // 行交互HDRでは Bayer の同色近傍(±2行)が別の露光の行になり、長秒の画素を短秒の値で置き換えて
        // 新しい暗点を作る。結果は Hdr=None・1フレームになり、以後 HDR 分割・合成もできなくなる
        // (フレーム連結では表示中の露光だけが残る)。以前は派生ビューの表示中しか断らず、断る文言も
        // 「Raw表示に戻してから補正」へ誘導していた
        string? reason = DefectCorrectionAvailability.Refusal(Format(hdr, frameCount), derivedViewShown: false);

        Assert.NotNull(reason);
        Assert.Contains("露光ごとに分割", reason);
        Assert.DoesNotContain("Raw表示に戻して", reason);
    }

    [Fact]
    public void DerivedView_IsRefusedWithSameGuidance()
    {
        // 派生ビューは HDR 素材の表示なので、Raw表示へ戻しても補正できない。戻すよう誘導しない
        string? reason = DefectCorrectionAvailability.Refusal(
            Format(HdrMode.LineInterleaved), derivedViewShown: true);

        Assert.Equal(DefectCorrectionAvailability.HdrRefusal, reason);
    }

    [Fact]
    public void OrdinaryRaw_CanBeCorrected()
    {
        Assert.Null(DefectCorrectionAvailability.Refusal(Format(HdrMode.None), derivedViewShown: false));
        Assert.Null(DefectCorrectionAvailability.Refusal(
            Format(HdrMode.None, frameCount: 4), derivedViewShown: false));
    }
}
