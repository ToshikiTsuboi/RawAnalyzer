using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// Raw表示のHDR素材で、露光の違う行を1つの母集団として扱う解析(欠陥検出・ノイズ測定)を断る規約。
/// </summary>
public class HdrExposureMixTests
{
    private static RawFormat Format(HdrMode hdr, int frameCount = 1, int stages = 2) => new()
    {
        Width = 8, Height = 8, BitDepth = 12, Bayer = BayerPattern.Rggb, Hdr = hdr,
        FrameCount = frameCount, HdrStages = stages,
    };

    [Theory]
    [InlineData(HdrMode.LineInterleaved, 1, true)]
    [InlineData(HdrMode.LineInterleaved, 4, true)]   // 行交互の連続撮影も1フレームに全露光
    [InlineData(HdrMode.Auto, 1, true)]              // Auto でフレーム数1は行交互
    [InlineData(HdrMode.Auto, 2, false)]             // Auto でフレーム数=段数はフレーム連結(1フレーム=1露光)
    [InlineData(HdrMode.FrameSequential, 2, false)]
    [InlineData(HdrMode.None, 1, false)]
    [InlineData(HdrMode.Auto, 5, false)]             // レイアウトを決められない指定は従来どおり(分割もできない)
    public void InFrame_IsTrueOnlyForLineInterleaved(HdrMode hdr, int frameCount, bool expected)
    {
        Assert.Equal(expected, HdrExposureMix.InFrame(Format(hdr, frameCount)));
    }

    [Fact]
    public void DefectDetection_LineInterleavedRawView_IsRefusedAndGuidedToSplitView()
    {
        // 行交互HDRの Raw 表示では同じ Bayer チャネルに長秒と短秒の行が交互に入り、チャネル別の mean±Nσ が
        // 混合分布から求まる(露光比4のフラットで σ≈750)。閾値が値域の外へ出て、黒点が0件になっていた
        string? reason = HdrExposureMix.DefectDetectionRefusal(
            Format(HdrMode.LineInterleaved), derivedViewShown: false);

        Assert.NotNull(reason);
        Assert.Contains("分割ビュー", reason);

        // 分割ビュー(段ごとに統計を取る)・合成ビュー・フレーム連結・HDRでない raw は検出できる
        Assert.Null(HdrExposureMix.DefectDetectionRefusal(Format(HdrMode.LineInterleaved), derivedViewShown: true));
        Assert.Null(HdrExposureMix.DefectDetectionRefusal(Format(HdrMode.FrameSequential, 2), derivedViewShown: false));
        Assert.Null(HdrExposureMix.DefectDetectionRefusal(Format(HdrMode.None), derivedViewShown: false));
    }
}
