using System.Windows.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 右パネルのカラーマトリクスの 9 個の入力欄の解釈(MainWindow の OnMatrixChanged が使う)。
/// </summary>
[Collection("WPF UI")]
public class ColorMatrixInputTests
{
    [Fact]
    public Task TryRead_ReadsRowMajorValuesIncludingFullWidth() => WpfTestHost.Run(() =>
    {
        TextBox[] boxes = Boxes("1.2", "-0.1", "-0.1", "0", "１", "0", "0", "－０．２", "1.2");

        Assert.True(ColorMatrixInput.TryRead(boxes, out ColorMatrix? matrix));
        Assert.Equal(new ColorMatrix(1.2, -0.1, -0.1, 0, 1, 0, 0, -0.2, 1.2), matrix);
        Assert.All(boxes, FieldFeedback.AssertValid);
    });

    [Fact]
    public Task TryRead_InvalidBoxes_AreEachMarkedWithReason() => WpfTestHost.Run(() =>
    {
        // 以前は行列の下に「入力エラー」と出すだけで、9 個のどの欄が悪いのかは欄の見た目で分からなかった。
        // ファイル一覧の絞り込み欄と同じく、悪い欄をすべて赤枠にしてツールチップに理由を出す
        TextBox[] boxes = Boxes("1", "0", "0", "0", "abc", "0", "0", "0", "NaN");

        Assert.False(ColorMatrixInput.TryRead(boxes, out _));
        Assert.Equal(ColorMatrixInput.InvalidReason, FieldFeedback.AssertInvalid(boxes[4]));
        Assert.Equal(ColorMatrixInput.InvalidReason, FieldFeedback.AssertInvalid(boxes[8]));
        FieldFeedback.AssertValid(boxes[0]);

        boxes[4].Text = "1";
        boxes[8].Text = "1";
        Assert.True(ColorMatrixInput.TryRead(boxes, out ColorMatrix? matrix));
        Assert.Equal(ColorMatrix.Identity, matrix);
        Assert.All(boxes, FieldFeedback.AssertValid);
    });

    private static TextBox[] Boxes(params string[] texts) =>
        texts.Select(text => new TextBox { Text = text }).ToArray();
}
