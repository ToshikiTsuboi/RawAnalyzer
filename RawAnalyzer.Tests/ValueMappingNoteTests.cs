using System.Globalization;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 処理結果へ差し替えたときの、32bit TIFF などの値の対応の説明(<see cref="ValueMappingNote"/>)。
/// </summary>
/// <remarks>
/// 残課題 2026-10-02 A2。以前は処理結果(ビニング・フィルタ・欠陥補正・画像演算)へ差し替えると説明を消し、
/// 画像情報欄から 1code が表す値が分からなくなっていた。対応が変わらない処理では引き継ぎ、変わる処理では
/// 新しい対応で説明し直す。
/// </remarks>
public class ValueMappingNoteTests
{
    // 32bit実数 0〜1 → 16bit(Offset 0、1code = 1/65535)
    private static readonly SampleScaling Normalized = new(0, 1, new SampleRange(0, 1, 0));

    // 負値を含む実スケール値 -10〜100 → 16bit(Offset −10、1code = 110/65535 ≈ 0.00168)
    private static readonly SampleScaling WithOffset = new(-10, 100, new SampleRange(-10, 100, 0));

    private static ValueMappingNote Note(SampleScaling scaling) =>
        ValueMappingNote.FromFile(scaling.Describe(), scaling)!;

    private static T InvariantCulture<T>(Func<T> action)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void FromFile_WithoutScaling_HasNoNote()
    {
        // 等倍で読めた画像(16bit TIFF・raw)には説明を出さない
        Assert.Null(ValueMappingNote.FromFile(null, null));
        Assert.Null(ValueMappingNote.FromFile("32bit実数 0〜1 → 16bit", null));
        Assert.Equal("32bit実数 0〜1 → 16bit", ValueMappingNote.FromFile("32bit実数 0〜1 → 16bit", Normalized)!.Text);
    }

    [Theory]
    [InlineData(ImageFilterKind.Mean)]
    [InlineData(ImageFilterKind.Gaussian)]
    [InlineData(ImageFilterKind.Median)]
    [InlineData(ImageFilterKind.UnsharpMask)]
    [InlineData(ImageFilterKind.Minimum)]
    [InlineData(ImageFilterKind.Maximum)]
    public void FiltersKeepingCodeScale_KeepFileNote(ImageFilterKind kind)
    {
        // 重みの和が1の平滑化・鮮鋭化と順序統計は、コードと値の対応を変えない(負値を含む対応でも)
        ValueMappingNote note = Note(WithOffset);

        Assert.Same(note, note.After(ValueMappingChange.ForProcessing(new ImageFilterOptions(kind), 2, BinningMode.Average)));
    }

    [Fact]
    public void AverageBinningAndDefectCorrection_KeepFileNote()
    {
        ValueMappingNote note = Note(WithOffset);

        Assert.Same(note, note.After(ValueMappingChange.ForProcessing(null, 4, BinningMode.Average)));
        Assert.Same(note, note.After(ValueMappingChange.None));
    }

    [Fact]
    public void SameScaleCalculation_WithoutOffset_KeepsFileNote()
    {
        // Offset 0 の対応では、差・ゲイン補正のコードも同じ刻みで元の値(の差・補正値)を表す
        ValueMappingNote note = Note(Normalized);

        Assert.Same(note, note.After(ValueMappingChange.ForCalculation(ImageOperation.Subtract)));
        Assert.Same(note, note.After(ValueMappingChange.ForCalculation(ImageOperation.AbsoluteDifference)));
        Assert.Same(note, note.After(ValueMappingChange.ForCalculation(ImageOperation.DivideGain)));
        Assert.Same(note, note.After(ValueMappingChange.ForProcessing(null, 2, BinningMode.Sum)));
    }

    [Fact]
    public void Difference_WithOffset_DescribesDifferenceFromZero()
    {
        // 負値を含む対応では、差のコード0は Offset(−10)ではなく差なし(0)を表す
        ValueMappingNote result = InvariantCulture(
            () => Note(WithOffset).After(ValueMappingChange.ForCalculation(ImageOperation.Subtract)));

        Assert.Equal("差≈code×0.00168", result.Text);
        Assert.Equal(0, result.Offset);
        Assert.Equal(WithOffset.ValuePerCode, result.PerCode);
    }

    [Fact]
    public void SumBinning_WithOffset_DescribesSumOfValues()
    {
        // 2×2 の加算ビニングは4画素の和。Offset も4画素分(−40)になり、刻みは変わらない
        ValueMappingNote result = InvariantCulture(
            () => Note(WithOffset).After(ValueMappingChange.ForProcessing(null, 2, BinningMode.Sum)));

        Assert.Equal("4画素の和≈-40+code×0.00168", result.Text);
        Assert.Equal(-40, result.Offset);
    }

    [Fact]
    public void Sobel_DescribesQuarterScaledGradient()
    {
        // Sobel の結果は勾配の大きさの1/4。1code は元の刻みの4倍を表し、Offset は微分で消える
        ValueMappingNote result = InvariantCulture(() => Note(Normalized).After(
            ValueMappingChange.ForProcessing(new ImageFilterOptions(ImageFilterKind.Sobel), 2, BinningMode.Average)));

        Assert.Equal("Sobel勾配≈code×6.1E-05", result.Text);
        Assert.Equal(4.0 / 65535, result.PerCode, 12);
    }

    [Fact]
    public void GainCorrection_WithOffset_IsNotConvertibleAndStaysSo()
    {
        // コードの比で補正するので、Offset のある対応では元の値へ戻せない。後の処理でも戻らない
        ValueMappingNote result = Note(WithOffset).After(ValueMappingChange.ForCalculation(ImageOperation.DivideGain));

        Assert.False(result.IsConvertible);
        Assert.Contains("換算できません", result.Text);
        Assert.Same(result, result.After(ValueMappingChange.Difference));
    }

    [Fact]
    public void ChainedProcessing_UsesCurrentMapping()
    {
        // 差(Offset 0)の後の加算ビニングは、元の Offset ではなく差の対応で求める(Offset 0 のままなので引き継ぐ)
        ValueMappingNote difference = InvariantCulture(
            () => Note(WithOffset).After(ValueMappingChange.Difference));

        Assert.Same(difference, difference.After(ValueMappingChange.Sum(4)));
    }
}
