using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RawAnalyzer.App.Controls;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 数値入力付きスライダー行(NumericSliderRow)の操作。WPF の実体を作り、入力イベントを送って確かめる。
/// </summary>
[Collection("WPF UI")]
public class NumericSliderRowTests
{
    [Theory]
    [InlineData(0, 1.0, 4095.0, 137.6, 138.0)] // 黒レベル: 表示 "138" のまま 137 コードで適用されていた
    [InlineData(0, 1.0, 4095.0, 4094.6, 4095.0)] // 白レベル: 表示 "4095" のまま 4094 の上端で適用されていた
    [InlineData(2, 0.05, 3.0, 1.23456, 1.23)]
    public Task SliderDrag_QuantizesValueToDisplayedDecimals(
        int decimals, double step, double maximum, double dragged, double expected) => WpfTestHost.Run(() =>
    {
        // ドラッグで決まるスライダー位置は端数を持つ。値は入力欄の表示桁にそろえる
        // (表示は四捨五入、黒点・白点への換算は切り捨てなので、端数が残ると1コードずれる)
        var row = NewRow(decimals, step, maximum);
        row.ValueSlider.Value = dragged;
        Assert.Equal(expected, row.Value, 10);
        Assert.Equal(expected, row.ValueSlider.Value, 10);
        Assert.Equal(expected.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture),
            row.ValueBox.Text);
    });

    [Fact]
    public Task Wheel_StepsFromDisplayedValue() => WpfTestHost.Run(() =>
    {
        // 表示桁より細かい値(以前のドラッグやバインド元の値)からの加算は、表示中の値を起点にする
        var row = NewRow(decimals: 0, step: 1, maximum: 4095);
        row.Value = 137.6; // 表示は "138"
        Wheel(row.ValueSlider, up: true);
        Assert.Equal(139, row.Value, 10);
        Assert.Equal("139", row.ValueBox.Text);

        row.Value = 4094.6; // 表示は "4095"。上限を超えない
        Wheel(row.ValueSlider, up: true);
        Assert.Equal(4095, row.Value, 10);
    });

    private static NumericSliderRow NewRow(int decimals, double step, double maximum) => new()
    {
        Minimum = 0,
        Maximum = maximum,
        Step = step,
        Decimals = decimals,
    };

    private static void Wheel(Slider slider, bool up)
    {
        slider.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, up ? 120 : -120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        });
    }
}
