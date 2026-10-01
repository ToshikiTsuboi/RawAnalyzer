using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RawAnalyzer.App.Controls;

/// <summary>
/// 数値入力・目盛・値域表示を備えた調整行。
/// スライダー、直接入力(Enter/フォーカス外れで確定)、ホイール操作、
/// ラベルのダブルクリックで既定値へリセットに対応する。
/// </summary>
public partial class NumericSliderRow : UserControl
{
    private bool _updating;

    // 入力欄がユーザー操作で書き換えられたか(フォーカスが外れただけでの丸めを防ぐ)
    private bool _textEdited;

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

    /// <summary>単位("dB" / "LSB" など)。入力欄の右に表示する。</summary>
    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(
            nameof(Unit), typeof(string), typeof(NumericSliderRow),
            new PropertyMetadata("", OnRangeChanged));

    /// <summary>補助表示(換算値など)。目盛行の中央に出す。</summary>
    public static readonly DependencyProperty NoteProperty =
        DependencyProperty.Register(
            nameof(Note), typeof(string), typeof(NumericSliderRow),
            new PropertyMetadata("", OnRangeChanged));

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

    /// <summary>単位("dB" / "LSB" など)。</summary>
    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    /// <summary>補助表示(換算値など)。</summary>
    public string Note
    {
        get => (string)GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
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

    /// <summary>
    /// 操作で決まった値を表示桁へ丸めて値域に収める。
    /// </summary>
    /// <remarks>
    /// スライダーのドラッグは 137.6 のような端数を生む。入力欄は表示桁へ四捨五入して "138" と
    /// 見せる一方、黒/白レベルの換算(DisplayLevels.ToPoints)は切り捨てるので、端数が残ると
    /// 表示と実際に使われるコード値が1コードずれる。値を表示どおりにしておく。
    /// </remarks>
    private double Quantize(double value)
    {
        double rounded = Math.Round(value, Math.Clamp(Decimals, 0, 15), MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, Minimum, Maximum);
    }

    /// <summary>表示中の値から1ステップ動かす(ホイール・↑↓キー)。</summary>
    private void StepBy(int direction)
    {
        Value = Quantize(Quantize(Value) + direction * Step);
    }

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

        // NaN/±∞ が来ると Math.Clamp は素通しさせ、スライダーの描画が壊れる。
        // 表示だけは必ず有限値にしておく(Valueの値域はバインド元の責任)
        double displayed = double.IsFinite(Value)
            ? Math.Clamp(Value, Minimum, Maximum)
            : Minimum;
        ValueSlider.Value = displayed;
        ValueBox.Text = displayed.ToString(Format, CultureInfo.InvariantCulture);
        UnitText.Text = Unit;

        // 目盛は幅が限られるので末尾の0を落として詰める(-20.0 → -20)
        MinText.Text = Minimum.ToString("0.###", CultureInfo.InvariantCulture);
        MaxText.Text = Maximum.ToString("0.###", CultureInfo.InvariantCulture);

        // 換算値は目盛行に入れるとパネルが狭いとき数値と重なるため、ツールチップへ出す
        string note = Note.Length > 0 ? Note + "\n" : "";
        LabelText.ToolTip =
            note +
            $"範囲 {MinText.Text} 〜 {MaxText.Text} {Unit} / 既定 " +
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

        Value = Quantize(e.NewValue);

        // 同じ表示値の中での移動では Value が変わらず Refresh が走らないので、つまみを表示値へ戻す
        if (ValueSlider.Value != Value)
        {
            Refresh();
        }
    }

    private void OnSliderWheel(object sender, MouseWheelEventArgs e)
    {
        StepBy(e.Delta > 0 ? 1 : -1);
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
            _updating = true;
            ValueBox.Text = Value.ToString(Format, CultureInfo.InvariantCulture);
            _updating = false;
            _textEdited = false;
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            StepBy(e.Key == Key.Up ? 1 : -1);
            e.Handled = true;
        }
    }

    private void OnValueBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating)
        {
            _textEdited = true;
        }
    }

    private void OnValueBoxLostFocus(object sender, RoutedEventArgs e)
    {
        // 編集していないのにCommitすると、バインド元が入れた表示桁より細かい値
        // (線形ゲインから換算した dB など)が表示桁へ丸められて動く
        if (_textEdited)
        {
            CommitText();
        }
    }

    private void CommitText()
    {
        // TryParse は "NaN" / "Infinity" も通す。Math.Clamp(NaN,..) は NaN のままなので
        // そのまま Value に入るとスライダーとLUTが壊れる
        if (double.TryParse(
                ValueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed))
        {
            Value = Quantize(parsed);
        }

        // 範囲外・不正入力は現在値へ戻す
        _updating = true;
        ValueBox.Text = Value.ToString(Format, CultureInfo.InvariantCulture);
        _updating = false;
        _textEdited = false;
    }

    private void OnLabelClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Value = Math.Clamp(DefaultValue, Minimum, Maximum);
        }
    }
}
