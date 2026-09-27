using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// ファイル連番の送りで新しい画像に適用する表示モード(ツールバーの選択とビューポート)の検証。
/// </summary>
public class DisplayModeSelectionTests
{
    [Theory]
    [InlineData(0, ViewportDisplayMode.Raw)]
    [InlineData(1, ViewportDisplayMode.BayerColor)]
    [InlineData(2, ViewportDisplayMode.ColorDevelop)]
    [InlineData(3, ViewportDisplayMode.ChannelSplit)]
    public void GrayBayerImage_KeepsSelectedMode(int selected, ViewportDisplayMode expected)
    {
        // 送った先でも成立するモードは選択を保つ
        // (以前はビューポートだけ Raw 表示へ戻り、選択はカラーや分割のまま食い違った)
        DisplayModeSelection.Choice choice =
            DisplayModeSelection.ForSequenceImage(selected, isColor: false, BayerPattern.Rggb);

        Assert.Equal(selected, choice.ComboIndex);
        Assert.True(choice.ComboEnabled);
        Assert.Equal(expected, choice.ViewportMode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ColorImage_ShowsTrueColorAndResetsSelection(int selected)
    {
        // デコード済みのカラー画像は RGB のまま表示し、Bayer 系の表示は選ばせない
        // (開いたときと同じく、選択は Raw 表示で操作不可)
        DisplayModeSelection.Choice choice =
            DisplayModeSelection.ForSequenceImage(selected, isColor: true, BayerPattern.None);

        Assert.Equal(0, choice.ComboIndex);
        Assert.False(choice.ComboEnabled);
        Assert.Equal(ViewportDisplayMode.TrueColor, choice.ViewportMode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void GrayImageWithoutBayer_ResetsBayerModesToRaw(int selected)
    {
        // Bayer なしではカラー・現像・分割は成立しない。選択も Raw 表示へ戻して表示とそろえる
        DisplayModeSelection.Choice choice =
            DisplayModeSelection.ForSequenceImage(selected, isColor: false, BayerPattern.None);

        Assert.Equal(0, choice.ComboIndex);
        Assert.True(choice.ComboEnabled);
        Assert.Equal(ViewportDisplayMode.Raw, choice.ViewportMode);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(-1)]
    public void HdrOrNoSelection_FallsBackToRaw(int selected)
    {
        // HDR 分割・合成は元画像から作る派生ビューなので、送った先の画像へは持ち越さない
        DisplayModeSelection.Choice choice =
            DisplayModeSelection.ForSequenceImage(selected, isColor: false, BayerPattern.Rggb);

        Assert.Equal(0, choice.ComboIndex);
        Assert.True(choice.ComboEnabled);
        Assert.Equal(ViewportDisplayMode.Raw, choice.ViewportMode);
    }
}
