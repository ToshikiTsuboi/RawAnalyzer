using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// キャンバス右下のオーバーレイの文字列(MainWindow の OnViewportStateChanged が描画のたびに作る)。
/// </summary>
public class LevelOverlayTests
{
    [Fact]
    public void Describe_KeepsViewNotesAboveZoomLevel()
    {
        // 回帰テスト: 描画のたびに間引きレベルの表示で上書きしていたので、HDR合成ビューの量子化損失の注意や
        // HDR分割ビューの並びの説明、フルスクリーンの案内が描画の直後に消え、実質的に表示されなかった。
        // 表示中の注記を残し、間引きレベルはその下の行に出す
        string split = LevelOverlay.DescribeSplit(2);

        Assert.Equal(split + "\n1/8 間引き表示 (ピラミッド L3)", LevelOverlay.Describe(8, split));
        Assert.Equal(
            split + "\n" + LevelOverlay.FullscreenHint + "\n等倍データ表示 (L0)",
            LevelOverlay.Describe(1, split, LevelOverlay.FullscreenHint));
        Assert.Equal("等倍データ表示 (L0)", LevelOverlay.Describe(1, null, ""));
    }

    [Fact]
    public void DescribeMerge_NotesQuantizationLossAndClampedBlack()
    {
        // 14bit・2段・露光比16は16bitへの量子化で約2bit落ちる。解析値はその精度で、黒点未満は0に切り詰めた値
        var format = new RawFormat { Width = 2, Height = 2, BitDepth = 14 };
        using RawImage longFrame = TestImages.FromCodes(new ushort[4], format);
        using RawImage shortFrame = TestImages.FromCodes(new ushort[4], format);
        HdrImage lossy = HdrMerger.Merge([longFrame, shortFrame], new HdrMergeParameters(16));
        var format12 = format with { BitDepth = 12 };
        using RawImage long12 = TestImages.FromCodes(new ushort[4], format12);
        using RawImage short12 = TestImages.FromCodes(new ushort[4], format12);
        HdrImage lossless = HdrMerger.Merge([long12, short12], new HdrMergeParameters(16));

        Assert.Equal(
            "HDR合成表示 (フルスケール 1048560, ゲイン=露出, 黒点未満は0, 表示・解析は16bit量子化後 " +
            "(1LSB=16.0, 元素材比 約2bit損失 / 無損失はfloat raw保存))",
            LevelOverlay.DescribeMerge(lossy));
        Assert.Equal("HDR合成表示 (フルスケール 1048560, ゲイン=露出, 黒点未満は0)", LevelOverlay.DescribeMerge(lossless));
    }
}
