using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RawViewer.App.Controls;

/// <summary>
/// 数値入力・目盛・値域表示を備えた調整行。
/// スライダー、直接入力(Enter/フォーカス外れで確定)、ホイール操作、
/// ラベルのダブルクリックで既定値へリセットに対応する。
/// </summary>
public partial class NumericSliderRow : UserControl
{
    private bool _updating;

    /// <summary>ラベル文字列。</summary>
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(NumericSliderRow),
            new PropertyMetadata("", OnLabelChanged));

    /// <summary>現在値(双方向バインド既定)。</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(NumericSliderRow),
            new FrameworkPropertyMetadata(
                0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    /// <summary>最小値。</summary>
    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(
            nameof(Minimum), typeof(double), typeof(NumericSliderRow),
            new PropertyMetadata(0.0, OnRangeChanged));

    /// <summary>最大値。</summary>
    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(
            nameof(Maximum), typeof(double), typeof(NumericSliderRow),
            new PropertyMetadata(1.0, OnRangeChanged));

    /// <summary>1ステップの変化量(ホイール・矢印キー)。</summary>
    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(
            nameof(Step), typeof(double), typeof(NumericSliderRow),
            new PropertyMetadata(0.01, OnRangeChanged));

    /// <summary>表示小数桁数。</summary>
    public static readonly DependencyProperty DecimalsProperty =
        DependencyProperty.Register(
            nameof(Decimals), typeof(int), typeof(NumericSliderRow),
            new PropertyMetadata(2, OnRangeChanged));

    /// <summary>既定値(ラベルのダブルクリックで復帰)。</summary>
    public static readonly DependencyProperty DefaultValueProperty =
        DependencyProperty.Register(
            nameof(DefaultValue), typeof(double), typeof(NumericSliderRow),
            new PropertyMetadata(0.0, OnRangeChanged));

    /// <summary>目盛の間隔(0で目盛なし)。</summary>
    public static readonly DependencyProperty TickFrequencyProperty =
        DependencyProperty.Register(
            nameof(TickFrequency), typeof(double), typeof(NumericSliderRow),
            new PropertyMetadata(0.0, OnRangeChanged));

    /// <summary>コントロールを生成する。</summary>
    public NumericSliderRow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    /// <summary>ラベル文字列。</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>現在値。</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>最小値。</summary>
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>最大値。</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>1ステップの変化量。</summary>
    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>表示小数桁数。</summary>
    public int Decimals
    {
        get => (int)GetValue(DecimalsProperty);
        set => SetValue(DecimalsProperty, value);
    }

    /// <summary>既定値。</summary>
    public double DefaultValue
    {
        get => (double)GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    /// <summary>目盛の間隔。</summary>
    public double TickFrequency
    {
        get => (double)GetValue(TickFrequencyProperty);
        set => SetValue(TickFrequencyProperty, value);
    }

    private string Format => "F" + Decimals.ToString(CultureInfo.InvariantCulture);

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((NumericSliderRow)d).LabelText.Text = (string)e.NewValue;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((NumericSliderRow)d).Refresh();
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((NumericSliderRow)d).Refresh();
    }

    private void Refresh()
    {
        if (_updating || ValueSlider is null)
        {
            return;
        }

        _updating = true;
        ValueSlider.Minimum = Minimum;
        ValueSlider.Maximum = Maximum;
        ValueSlider.SmallChange = Step;
        ValueSlider.LargeChange = Step * 10;
        ValueSlider.TickFrequency = TickFrequency > 0 ? TickFrequency : (Maximum - Minimum) / 10;
        ValueSlider.Value = Math.Clamp(Value, Minimum, Maximum);
        ValueBox.Text = Value.ToString(Format, CultureInfo.InvariantCulture);
        MinText.Text = Minimum.ToString(Format, CultureInfo.InvariantCulture);
        MaxText.Text = Maximum.ToString(Format, CultureInfo.InvariantCulture);
        LabelText.ToolTip =
            $"範囲 {MinText.Text} 〜 {MaxText.Text} / 既定 " +
            DefaultValue.ToString(Format, CultureInfo.InvariantCulture) +
            "\nラベルをダブルクリックで既定値に戻す / スライダー上でホイール・入力欄で↑↓キー: " +
            Step.ToString(Format, CultureInfo.InvariantCulture) + " 刻み";
        ValueBox.ToolTip = LabelText.ToolTip;
        _updating = false;
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating)
        {
            return;
        }

        Value = e.NewValue;
    }

    private void OnSliderWheel(object sender, MouseWheelEventArgs e)
    {
        double next = Value + (e.Delta > 0 ? Step : -Step);
        Value = Math.Clamp(next, Minimum, Maximum);
        e.Handled = true;
    }

    private void OnValueBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ValueBox.Text = Value.ToString(Format, CultureInfo.InvariantCulture);
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            Value = Math.Clamp(
                Value + (e.Key == Key.Up ? Step : -Step), Minimum, Maximum);
            e.Handled = true;
        }
    }

    private void OnValueBoxLostFocus(object sender, RoutedEventArgs e)
    {
        CommitText();
    }

    private void CommitText()
    {
        if (double.TryParse(
                ValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            Value = Math.Clamp(parsed, Minimum, Maximum);
        }

        // 範囲外・不正入力は現在値へ戻す
        ValueBox.Text = Value.ToString(Format, CultureInfo.InvariantCulture);
    }

    private void OnLabelClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Value = Math.Clamp(DefaultValue, Minimum, Maximum);
        }
    }
}
