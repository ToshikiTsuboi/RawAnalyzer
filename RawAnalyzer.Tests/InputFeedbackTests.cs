using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RawAnalyzer.App.Controls;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 入力欄の「不正」の見せ方(InputFeedback。絞り込み欄と数値入力欄で共通の赤枠とツールチップの理由)。
/// </summary>
[Collection("WPF UI")]
public class InputFeedbackTests
{
    [Fact]
    public Task Error_ReplacesDirectToolTipAndRestoresIt() => WpfTestHost.Run(() =>
    {
        // XAML に直接書いたツールチップ(ToolTip="…")はスタイルのトリガーより優先されて差し替わらないので、
        // 理由に差し替えて覚えておき、正常へ戻したら戻す
        var box = new TextBox { ToolTip = "説明" };
        FieldFeedback.AssertValid(box);

        InputFeedback.SetError(box, "理由1");
        Assert.True(InputFeedback.GetHasError(box));
        Assert.Equal("理由1", FieldFeedback.AssertInvalid(box));

        InputFeedback.SetError(box, "理由2"); // 理由が変わっても、戻す先は元の説明のまま
        Assert.Equal("理由2", FieldFeedback.AssertInvalid(box));

        InputFeedback.SetError(box, "");
        Assert.False(InputFeedback.GetHasError(box));
        FieldFeedback.AssertValid(box);
        Assert.Equal("説明", box.ToolTip);
    });

    [Fact]
    public Task Error_OnComboBox_RestoresStyleToolTipAndBinding() => WpfTestHost.Run(() =>
    {
        // スタイルが与えていたツールチップは直接の値を外して戻し、バインドしていたツールチップはバインドし直す
        var styled = new ComboBox
        {
            IsEditable = true,
            Style = new Style(typeof(ComboBox), (Style)Application.Current.FindResource(typeof(ComboBox)))
            {
                Setters = { new Setter(FrameworkElement.ToolTipProperty, "スタイルの説明") },
            },
        };
        InputFeedback.SetError(styled, "理由");
        Assert.Equal("理由", FieldFeedback.AssertInvalid(styled));
        InputFeedback.SetError(styled, null);
        FieldFeedback.AssertValid(styled);
        Assert.Equal("スタイルの説明", styled.ToolTip);
        Assert.Equal(DependencyProperty.UnsetValue, styled.ReadLocalValue(FrameworkElement.ToolTipProperty));

        var source = new HelpSource { Help = "バインドの説明" };
        var bound = new TextBox { DataContext = source };
        bound.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(HelpSource.Help)));
        InputFeedback.SetError(bound, "理由");
        Assert.Equal("理由", FieldFeedback.AssertInvalid(bound));
        InputFeedback.SetError(bound, null);
        Assert.Equal("バインドの説明", bound.ToolTip);
        Assert.NotNull(BindingOperations.GetBindingExpression(bound, FrameworkElement.ToolTipProperty));
    });

    [Fact]
    public Task Error_KeepsToolTipRewrittenWhileInvalid() => WpfTestHost.Run(() =>
    {
        // 不正の間に呼び出し側が説明を書き換えたら、正常へ戻したときに古い説明へ巻き戻さない
        var box = new TextBox { ToolTip = "古い説明" };
        InputFeedback.SetError(box, "理由");
        box.ToolTip = "新しい説明";
        InputFeedback.SetError(box, null);
        Assert.Equal("新しい説明", box.ToolTip);
    });

    [Fact]
    public Task Check_SetsOrClearsErrorAndReturnsValidity() => WpfTestHost.Run(() =>
    {
        var box = new TextBox();
        Assert.False(InputFeedback.Check(box, valid: false, "理由"));
        Assert.Equal("理由", InputFeedback.GetError(box));
        Assert.True(InputFeedback.Check(box, valid: true, "理由"));
        Assert.Null(InputFeedback.GetError(box));
        Assert.Null(box.ToolTip);
    });

    private sealed class HelpSource
    {
        public string Help { get; set; } = "";
    }
}
