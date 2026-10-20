using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 処理結果(画像演算・欠陥補正・ビニング・フィルタ)で表示中の画像を差し替えてよいかの判定と、
/// 差し替えなかったときに示す理由の検証。
/// </summary>
public class ProcessedImageReplacementTests
{
    [Fact]
    public void SameSource_WithoutDerivedView_Replaces()
    {
        using RawImage source = MakeImage();

        Assert.Equal(
            ProcessedImageReplacement.Refusal.None,
            ProcessedImageReplacement.Check(source, source, derivedViewShown: false));
    }

    [Fact]
    public void DerivedViewShown_DoesNotReplaceEvenIfSourceIsUnchanged()
    {
        // HDR分割・合成の計算中に始めた処理の結果。計算の完了で派生ビューが表示されても元画像は
        // 処理の元のまま(派生ビューは元画像を差し替えない)。以前はこの状態で元画像だけを差し替え、
        // 派生ビュー(ActiveImage)が残って表示と解析・保存の対象が食い違った
        using RawImage source = MakeImage();

        Assert.Equal(
            ProcessedImageReplacement.Refusal.DerivedViewShown,
            ProcessedImageReplacement.Check(source, source, derivedViewShown: true));
    }

    [Fact]
    public void SourceReplaced_DoesNotReplace()
    {
        using RawImage source = MakeImage();
        using RawImage other = MakeImage();

        Assert.Equal(
            ProcessedImageReplacement.Refusal.SourceReplaced,
            ProcessedImageReplacement.Check(source, other, derivedViewShown: false));
        Assert.Equal(
            ProcessedImageReplacement.Refusal.SourceReplaced,
            ProcessedImageReplacement.Check(source, null, derivedViewShown: false));
    }

    [Fact]
    public void Explain_NamesResultAndWhatToDo()
    {
        string derived = ProcessedImageReplacement.Explain(
            ProcessedImageReplacement.Refusal.DerivedViewShown, "欠陥補正 3px (メディアン)");
        string replaced = ProcessedImageReplacement.Explain(
            ProcessedImageReplacement.Refusal.SourceReplaced, "2×2 平均");

        Assert.Contains("欠陥補正 3px (メディアン)", derived);
        Assert.Contains("適用しませんでした", derived);
        Assert.Contains("Raw表示に戻して", derived);
        Assert.Contains("2×2 平均", replaced);
        Assert.Contains("適用しませんでした", replaced);
        Assert.NotEqual(derived, replaced);
        Assert.Equal("", ProcessedImageReplacement.Explain(ProcessedImageReplacement.Refusal.None, "x"));
    }

    [Fact]
    public void CarryLevels_MapsLevelsLikeSixteenBitProcessing()
    {
        // 12bit の既定(白 = 4095<<4 = 65520)で平均ビニングすると、結果は最大 code を 65535 へ写すので白点も 65535。
        // 黒/白レベルは処理の結果の画素と同じ倍率で写す(処理の前後で同じ code が同じ明るさ)
        using RawImage saturated = TestImages.FromCodes([4095, 4095, 4095, 4095], 2, 2, bitDepth: 12);
        using RawImage binned = ImageBinning.Apply(saturated, 2);
        (ushort black, ushort white) = ProcessedImageReplacement.CarryLevels(100 << 4, 4095 << 4, 12, 16);
        Assert.Equal(binned.GetPixel(0, 0), white);
        Assert.Equal(65535, white);
        Assert.Equal(1600, black); // 100×65535/4095 = 1600.4

        (_, ushort lowered) = ProcessedImageReplacement.CarryLevels(0, 2000 << 4, 12, 16);
        Assert.Equal(32007, lowered); // 2000×65535/4095 = 32007.3

        // ビット深度が変わらない処理(画像演算・欠陥補正)と16bitの素材はそのまま
        Assert.Equal(((ushort)1600, (ushort)65520), ProcessedImageReplacement.CarryLevels(1600, 65520, 12, 12));
        Assert.Equal(((ushort)1600, (ushort)65520), ProcessedImageReplacement.CarryLevels(1600, 65520, 16, 16));
    }

    private static RawImage MakeImage() => TestImages.FromCodes(new ushort[16], 4, 4, bitDepth: 12);
}
