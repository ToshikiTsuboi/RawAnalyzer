using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 表示モード(ツールバーの選択とビューポート)の検証。差し替えた画像(ファイル連番・TIFF のページ送り・
/// 処理結果)に適用するモードと、選択肢から選ばれたモードを表示中の画像に使えるかの判定。
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

    [Theory]
    [InlineData(BayerPattern.None)]
    public void SelectedRawItem_OnColorImage_ShowsTrueColor(BayerPattern bayer)
    {
        // カラー画像の「Raw表示」は RGB のままの表示(開いたとき・送りと同じ)。
        // 以前はメニューから Bayer 系を選んで断られた後の戻り先が Raw 表示で、カラー画像がグレーになった
        DisplayModeSelection.Selected selected =
            DisplayModeSelection.ForSelectedMode(0, isColor: true, bayer);

        Assert.Equal(DisplayModeSelection.Refusal.None, selected.Refusal);
        Assert.Equal(ViewportDisplayMode.TrueColor, selected.ViewportMode);
    }

    [Theory]
    [InlineData(1, BayerPattern.Rggb)] // Bayer を指定していても Bayer 系の表示にしない
    [InlineData(3, BayerPattern.None)] // Bayer なしの断り(NoBayer)よりカラー画像の断りを優先する
    public void SelectedBayerMode_OnColorImage_IsRefusedAndStaysTrueColor(int index, BayerPattern bayer)
    {
        // カラー画像には右パネルで Bayer を指定していても Bayer 系の表示を使わない
        // (表示モード選択はカラー画像で操作不可。メニューから選んでも RGB のまま表示する)
        DisplayModeSelection.Selected selected =
            DisplayModeSelection.ForSelectedMode(index, isColor: true, bayer);

        Assert.Equal(DisplayModeSelection.Refusal.ColorImage, selected.Refusal);
        Assert.Equal(ViewportDisplayMode.TrueColor, selected.ViewportMode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SelectedBayerMode_OnGrayImageWithoutBayer_IsRefused(int index)
    {
        // Bayer なしのグレー画像ではカラー・現像・分割は成立しない。戻り先は Raw 表示
        DisplayModeSelection.Selected selected =
            DisplayModeSelection.ForSelectedMode(index, isColor: false, BayerPattern.None);

        Assert.Equal(DisplayModeSelection.Refusal.NoBayer, selected.Refusal);
        Assert.Equal(ViewportDisplayMode.Raw, selected.ViewportMode);
    }

    [Theory]
    [InlineData(0, BayerPattern.None, ViewportDisplayMode.Raw)]
    [InlineData(1, BayerPattern.Rggb, ViewportDisplayMode.BayerColor)]
    [InlineData(2, BayerPattern.Bggr, ViewportDisplayMode.ColorDevelop)]
    [InlineData(3, BayerPattern.Gbrg, ViewportDisplayMode.ChannelSplit)]
    public void SelectedMode_OnGrayImage_IsShown(
        int index, BayerPattern bayer, ViewportDisplayMode expected)
    {
        DisplayModeSelection.Selected selected =
            DisplayModeSelection.ForSelectedMode(index, isColor: false, bayer);

        Assert.Equal(DisplayModeSelection.Refusal.None, selected.Refusal);
        Assert.Equal(expected, selected.ViewportMode);
    }

    [Fact]
    public void HdrMode_OnRawWithoutHdr_PointsToFormatChange()
    {
        // raw は「フォーマット変更…」(読み込みダイアログ)で HDR 方式を指定して開き直せる
        DisplayModeSelection.Refusal refusal =
            DisplayModeSelection.ForHdrMode(HdrMode.None, isColor: false, isRawFile: true);

        Assert.Equal(DisplayModeSelection.Refusal.NoHdr, refusal);
        Assert.Contains("フォーマット変更", DisplayModeSelection.Explain(refusal));
    }

    [Theory]
    [InlineData(true, "カラー画像(RGB)はカラーのまま表示します。", "HDR分割・合成")] // カラー画像(常に画像ファイル)
    [InlineData(false, "画像ファイル(TIFF等)にはHDR方式を指定できません。", "rawで保存")] // グレーの画像ファイル
    public void HdrMode_OnImageFile_ExplainsWhyWithoutPointingToFormatChange(
        bool isColor, string expectedStart, string expectedMeans)
    {
        // 「フォーマット変更…」は raw でしか開き直さず、画像ファイルでは何も起きない。
        // 以前はカラー画像・画像ファイルでも「フォーマット変更…から設定」と案内していた。
        // カラー画像は RGB のまま表示する(Bayer 系の表示を断るときと同じ説明)。グレーの画像ファイルには
        // HDR 方式を指定する手段がないので、raw として保存して開き直す手段を示す
        DisplayModeSelection.Refusal refusal =
            DisplayModeSelection.ForHdrMode(HdrMode.None, isColor, isRawFile: false);
        string message = DisplayModeSelection.Explain(refusal);

        Assert.Equal(
            isColor
                ? DisplayModeSelection.Refusal.HdrOnColorImage
                : DisplayModeSelection.Refusal.HdrOnImageFile,
            refusal);
        Assert.StartsWith(expectedStart, message);
        Assert.Contains(expectedMeans, message);
        Assert.DoesNotContain("フォーマット変更", message);
        Assert.Contains("raw(.raw/.bin)", message);
    }

    [Theory]
    [InlineData(HdrMode.Auto)]
    public void HdrMode_WithHdrFormat_IsAllowed(HdrMode hdr)
    {
        Assert.Equal(
            DisplayModeSelection.Refusal.None,
            DisplayModeSelection.ForHdrMode(hdr, isColor: false, isRawFile: true));
    }

    [Fact]
    public void BayerModeRefusals_KeepTheirExplanations()
    {
        // Bayer 系の表示を断るときの説明も同じ仕組みで出す(文言は従来どおり)
        Assert.StartsWith(
            "カラー画像(RGB)はカラーのまま表示します。",
            DisplayModeSelection.Explain(DisplayModeSelection.Refusal.ColorImage));
        Assert.Contains(
            "Bayerカラー・カラー現像・チャネル分割",
            DisplayModeSelection.Explain(DisplayModeSelection.Refusal.ColorImage));
        Assert.Contains(
            "右パネルの「フォーマット」→「Bayer」",
            DisplayModeSelection.Explain(DisplayModeSelection.Refusal.NoBayer));
    }
}
