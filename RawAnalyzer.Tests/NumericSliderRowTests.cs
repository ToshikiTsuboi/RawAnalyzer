using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    [Fact]
    public Task ValueBox_UpDownKeysStepValue() => WpfTestHost.Run(() =>
    {
        // TextBox は↑↓をキャレット移動のコマンドで処理済みにするので、KeyDown では届かない
        var row = NewRow(decimals: 0, step: 1, maximum: 4095);
        row.Value = 100;
        Assert.True(PressKey(row.ValueBox, Key.Up));
        Assert.Equal(101, row.Value, 10);
        Assert.Equal("101", row.ValueBox.Text);
        Assert.True(PressKey(row.ValueBox, Key.Down));
        Assert.True(PressKey(row.ValueBox, Key.Down));
        Assert.Equal(99, row.Value, 10);

        // 打ちかけの値はその値を起点にする(打った値を捨てて元の値から動かさない)
        row.ValueBox.Text = "250";
        Assert.True(PressKey(row.ValueBox, Key.Up));
        Assert.Equal(251, row.Value, 10);
        Assert.Equal("251", row.ValueBox.Text);
    });

    [Fact]
    public Task ValueBox_LosingKeyboardFocusCommitsTypedValue() => WpfTestHost.Run(() =>
    {
        // メインメニュー・パレット・ダイアログへはキーボードフォーカスだけが移り、論理フォーカスの
        // LostFocus は来ない。確定しないと、欄に 256 と見えたまま旧値で保存や合成が走る
        var row = NewRow(decimals: 0, step: 1, maximum: 4095);
        row.Value = 100;
        row.ValueBox.Text = "256";
        LoseKeyboardFocus(row.ValueBox, newFocus: new MenuItem());
        Assert.Equal(256, row.Value, 10);

        // 入力欄自身のコンテキストメニュー(貼り付けなど)へ移るときは、編集の途中なので確定しない
        row.ValueBox.Text = "300";
        var menuItem = new MenuItem();
        _ = new ContextMenu { PlacementTarget = row.ValueBox, Items = { menuItem } };
        LoseKeyboardFocus(row.ValueBox, newFocus: menuItem);
        Assert.Equal(256, row.Value, 10);
        Assert.Equal("300", row.ValueBox.Text);
    });

    [Fact]
    public Task CommitPendingEdit_CommitsTypedValueOfFocusedBoxOnly() => WpfTestHost.Run(() =>
    {
        // ショートカット(Ctrl+S など)はフォーカスを動かさないので、コマンドの実行前に確定させる
        var row = NewRow(decimals: 0, step: 1, maximum: 4095);
        row.Value = 100;
        row.ValueBox.Text = "256";
        NumericSliderRow.CommitPendingEdit(row.ValueSlider);
        NumericSliderRow.CommitPendingEdit(null);
        Assert.Equal(100, row.Value, 10);

        NumericSliderRow.CommitPendingEdit(row.ValueBox);
        Assert.Equal(256, row.Value, 10);
    });

    [Fact]
    public Task ValueBox_AcceptsFullWidthDigits() => WpfTestHost.Run(() =>
    {
        // IME がオンのままだと数字は全角で入る。黙って元の値へ戻さずに受け付ける
        var row = NewRow(decimals: 0, step: 1, maximum: 4095);
        row.Value = 100;
        row.ValueBox.Text = "１２８";
        Assert.True(PressKey(row.ValueBox, Key.Enter));
        Assert.Equal(128, row.Value, 10);
        Assert.Equal("128", row.ValueBox.Text);
    });

    private static NumericSliderRow NewRow(int decimals, double step, double maximum) => new()
    {
        Minimum = 0,
        Maximum = maximum,
        Step = step,
        Decimals = decimals,
    };

    /// <summary>
    /// 実際の入力と同じく PreviewKeyDown(トンネル)→ 未処理なら KeyDown(バブル)の順にキーを送る。
    /// </summary>
    /// <returns>どちらかで処理済みになったら true。</returns>
    private static bool PressKey(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new TestInputSource(), Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        target.RaiseEvent(args);
        if (!args.Handled)
        {
            args.RoutedEvent = Keyboard.KeyDownEvent;
            target.RaiseEvent(args);
        }

        return args.Handled;
    }

    private static void LoseKeyboardFocus(UIElement target, IInputElement? newFocus)
    {
        target.RaiseEvent(new KeyboardFocusChangedEventArgs(
            Keyboard.PrimaryDevice, Environment.TickCount, (IInputElement)target, newFocus)
        {
            RoutedEvent = Keyboard.LostKeyboardFocusEvent,
        });
    }

    private static void Wheel(Slider slider, bool up)
    {
        slider.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, up ? 120 : -120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        });
    }

    /// <summary>ウィンドウを表示せずにキー入力イベントを作るための入力元。</summary>
    private sealed class TestInputSource : PresentationSource
    {
        private Visual? _root;

        public override Visual RootVisual
        {
            get => _root!;
            set => _root = value;
        }

        public override bool IsDisposed => false;

        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
