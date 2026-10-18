using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.App.Views;

/// <summary>
/// 軸の書式: 縦軸・横軸の範囲(自動・手動)、主目盛り・補助目盛りの間隔(自動・指定)、目盛線と、目盛りの描画。
/// </summary>
/// <remarks>
/// 範囲・間隔の自動と手動の切り替えはすぐ反映し(手動へ切り替えたときは表示中の値で固定する)、値の入力は「適用」か
/// Enter で反映する。自動の欄は描いている範囲・間隔を示す。
/// </remarks>
public partial class ProfilePlotView
{
    /// <summary>主目盛りの目盛線の印(テストで目盛線を見分ける)。</summary>
    internal const string MajorGridTag = "major-grid";

    /// <summary>補助目盛りの目盛線の印。</summary>
    internal const string MinorGridTag = "minor-grid";

    // 自動の主目盛りの画面上の間隔の目安。横軸はラベルが長いので広く取る
    private const double HorizontalTickSpacing = 100;
    private const double VerticalTickSpacing = 40;
    private const double MajorTickLength = 4;
    private const double MinorTickLength = 2;
    private const string YAxisHelp = "右クリック: 縦軸の範囲・軸の書式\nホイール: 縦軸のみ拡大・縮小";
    private const string XAxisHelp = "右クリック: 横軸の範囲・軸の書式";

    private static readonly Brush TickBrush = Frozen(Brushes.Gray);
    private static readonly Brush MajorGridBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x28, 0x9A, 0x9A, 0x95)));
    private static readonly Brush MinorGridBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x12, 0x9A, 0x9A, 0x95)));

    // 手動の横軸の範囲(表示する座標で)。自動へ戻しても覚えておき、手動へ戻したときに使う
    private ProfileAxisRange? _manualHorizontal;
    private readonly StepSetting _yMajor = new();
    private readonly StepSetting _yMinor = new();
    private readonly StepSetting _xMajor = new();
    private readonly StepSetting _xMinor = new();
    private StepRow[] _stepRows = Array.Empty<StepRow>();
    private bool _majorGrid = true;
    private bool _minorGrid;
    private ProfileAxisTicks _horizontalTicks = ProfileAxisTicks.Empty;
    private ProfileAxisTicks _verticalTicks = ProfileAxisTicks.Empty;
    private string? _horizontalNote;
    private string? _verticalNote;
    private string _xAxisToolTip = "元画像上の画素座標。ホイールで横軸だけ拡大・縮小できます。";
    private double _axisLabelHeight;

    /// <summary>目盛りの間隔の設定(自動か、指定の値か)。指定の値は自動へ戻しても覚えておく。</summary>
    private sealed class StepSetting
    {
        internal bool Auto { get; set; } = true;

        internal double? Value { get; set; }

        /// <summary>描くときに使う指定の値(自動なら null)。</summary>
        internal double? Manual => Auto ? null : Value;
    }

    /// <summary>軸の書式の間隔の欄1行(設定・入力欄・自動のチェック)。</summary>
    private sealed record StepRow(StepSetting Setting, TextBox Box, CheckBox AutoCheck, bool Integer,
        StepRow? MajorRow, Func<double> Effective);

    /// <summary>表示中の横軸の目盛り。</summary>
    internal ProfileAxisTicks HorizontalTicks => _horizontalTicks;

    /// <summary>表示中の縦軸の目盛り。</summary>
    internal ProfileAxisTicks VerticalTicks => _verticalTicks;

    private bool ManualHorizontal => XScaleCombo?.SelectedIndex == 1;

    // 表示中の横軸の範囲(表示する座標で)
    private ProfileAxisRange HorizontalCoordinates => Shift(HorizontalRange, _data.CoordinateOffset);

    private static ProfileAxisRange Shift(ProfileAxisRange range, double offset) =>
        new(range.Minimum + offset, range.Maximum + offset);

    private static Brush Frozen(Brush brush)
    {
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    private void InitializeAxisFormat()
    {
        StepRow yMajor = new(_yMajor, YMajorStepBox, YMajorAutoCheck, false, null, () => _verticalTicks.MajorStep);
        StepRow xMajor = new(_xMajor, XMajorStepBox, XMajorAutoCheck, true, null, () => _horizontalTicks.MajorStep);
        _stepRows = new[]
        {
            yMajor,
            new StepRow(_yMinor, YMinorStepBox, YMinorAutoCheck, false, yMajor, () => _verticalTicks.MinorStep),
            xMajor,
            new StepRow(_xMinor, XMinorStepBox, XMinorAutoCheck, true, xMajor, () => _horizontalTicks.MinorStep),
        };
    }

    // ---- 範囲の切り替え ----

    private void OnYScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingScaleControls) return;
        // 初回の手動切替は現在の表示範囲を固定。その後は最後に適用した値を復元する。
        if (ManualScale) _manualRange ??= _axisRange;
        Redraw();
        UpdateScaleInputs();
    }

    private void OnXScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingScaleControls) return;
        EndPan();
        // 縦軸と同じく、初回の手動切替は表示中の範囲(ホイールでの拡大を含む)を固定する。自動へ戻すとデータ全体
        if (ManualHorizontal) _manualHorizontal ??= Shift(_horizontalRange ?? FullHorizontalRange, _data.CoordinateOffset);
        else _horizontalRange = null;
        Redraw();
        UpdateHorizontalInputs();
    }

    private ProfileAxisRange FullHorizontalRange => ProfilePlotNavigation.FullHorizontal(_data.Values.Length);

    /// <summary>横軸の手動設定を、表示中の範囲を引き継いで始める(ポップアップは開かない)。</summary>
    internal void PrepareXRangeEditor()
    {
        EndPan();
        _manualHorizontal = HorizontalCoordinates;
        SetHorizontalMode(manual: true);
        Redraw();
        UpdateHorizontalInputs();
    }

    // 横軸の範囲の切り替えを、選択の変更の処理を通さずに行う(呼ぶ側で描き直す)
    private void SetHorizontalMode(bool manual)
    {
        _updatingScaleControls = true;
        XScaleCombo.SelectedIndex = manual ? 1 : 0;
        _updatingScaleControls = false;
    }

    private void OnXAxisContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        XAutoRangeMenu.IsChecked = !ManualHorizontal;
        XManualRangeMenu.IsChecked = ManualHorizontal;
    }

    private void OnXAutoRangeClick(object sender, RoutedEventArgs e)
    {
        // 自動のままホイールで拡大していても、データ全体へ戻す
        EndPan();
        SetHorizontalMode(manual: false);
        _horizontalRange = null;
        Redraw();
        UpdateHorizontalInputs();
    }

    private void OnXManualRangeClick(object sender, RoutedEventArgs e)
    {
        PrepareXRangeEditor();
        OpenAxisFormat(XMinimumBox);
    }

    private void OnAxisFormatClick(object sender, RoutedEventArgs e)
    {
        EndPan();
        OpenAxisFormat(null);
    }

    private void OpenAxisFormat(TextBox? focus)
    {
        RefreshAllInputs();
        AxisFormatPopup.IsOpen = true;
        if (focus is null) return;
        focus.Focus();
        focus.SelectAll();
    }

    private void OnCloseAxisFormatClick(object sender, RoutedEventArgs e) => AxisFormatPopup.IsOpen = false;

    private void OnAxisFormatPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        AxisFormatPopup.IsOpen = false;
        e.Handled = true;
    }

    // ---- 目盛りの間隔・目盛線 ----

    private void OnStepAutoClick(object sender, RoutedEventArgs e)
    {
        if (!_ready || _stepRows.FirstOrDefault(row => ReferenceEquals(row.AutoCheck, sender)) is not { } row) return;
        bool auto = row.AutoCheck.IsChecked == true;
        row.Setting.Auto = auto;
        // 指定へ切り替えたときは、いま描いている間隔で固定する(補助目盛りを描いていなければ 0 = なし)
        if (!auto && row.Setting.Value is null)
        {
            double effective = row.Effective();
            if (effective > 0 || row.MajorRow is not null) row.Setting.Value = effective;
        }

        // 自動の欄は描き直しで示す。指定にした欄だけ固定した値を出す(ほかの欄の打ちかけの値は消さない)
        Redraw();
        if (auto) return;
        _updatingScaleControls = true;
        row.Box.Text = row.Setting.Value is { } value ? FormatStep(value) : "";
        _updatingScaleControls = false;
        ValidateAxisFormatInputs();
        row.Box.Focus();
        row.Box.SelectAll();
    }

    private void OnGridCheckClick(object sender, RoutedEventArgs e)
    {
        _majorGrid = MajorGridCheck.IsChecked == true;
        _minorGrid = MinorGridCheck.IsChecked == true;
        Redraw();
    }

    /// <summary>主目盛り・補助目盛りの目盛線を描くか。</summary>
    internal (bool Major, bool Minor) GridLinesVisible => (_majorGrid, _minorGrid);

    // ---- 入力欄 ----

    private void UpdateScaleInputs()
    {
        WriteVerticalRangeInputs();
        ValidateAxisFormatInputs();
    }

    private void UpdateHorizontalInputs()
    {
        WriteHorizontalRangeInputs();
        ValidateAxisFormatInputs();
    }

    // 描き直しのたびに、自動の欄へ描いた範囲・間隔を示す(手動の欄は打ちかけの値を消さない)
    private void RefreshAutomaticInputs()
    {
        if (!ManualScale) WriteVerticalRangeInputs();
        if (!ManualHorizontal) WriteHorizontalRangeInputs();
        WriteStepInputs(includeManual: false);
        AxisFormatNote.Text = string.Join("\n", new[] { _verticalNote, _horizontalNote }.Where(n => n is not null));
        AxisFormatNote.Visibility = AxisFormatNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ValidateAxisFormatInputs();
    }

    // 設定を開くときは、打ちかけで適用していない値を捨てて、いまの設定を示す
    private void RefreshAllInputs()
    {
        WriteVerticalRangeInputs();
        WriteHorizontalRangeInputs();
        WriteStepInputs(includeManual: true);
        ValidateAxisFormatInputs();
    }

    private void WriteVerticalRangeInputs()
    {
        _updatingScaleControls = true;
        // 自動表示は読みやすく、手動入力へ移すときは丸めず値を引き継ぐ。
        string format = ManualScale ? "G17" : "G8";
        YMinimumBox.Text = _axisRange.Minimum.ToString(format, CultureInfo.CurrentCulture);
        YMaximumBox.Text = _axisRange.Maximum.ToString(format, CultureInfo.CurrentCulture);
        YMinimumBox.IsEnabled = ManualScale;
        YMaximumBox.IsEnabled = ManualScale;
        _updatingScaleControls = false;
    }

    private void WriteHorizontalRangeInputs()
    {
        _updatingScaleControls = true;
        string format = ManualHorizontal ? "G17" : "G8";
        ProfileAxisRange coordinates = HorizontalCoordinates;
        XMinimumBox.Text = coordinates.Minimum.ToString(format, CultureInfo.CurrentCulture);
        XMaximumBox.Text = coordinates.Maximum.ToString(format, CultureInfo.CurrentCulture);
        XMinimumBox.IsEnabled = ManualHorizontal;
        XMaximumBox.IsEnabled = ManualHorizontal;
        _updatingScaleControls = false;
    }

    private void WriteStepInputs(bool includeManual)
    {
        _updatingScaleControls = true;
        foreach (StepRow row in _stepRows)
        {
            row.AutoCheck.IsChecked = row.Setting.Auto;
            row.Box.IsEnabled = !row.Setting.Auto;
            if (row.Setting.Auto)
            {
                double effective = row.Effective();
                row.Box.Text = effective > 0 ? FormatStep(effective)
                    : row.MajorRow is not null && _data.Values.Length > 0 ? "なし" : "";
            }
            else if (includeManual)
            {
                row.Box.Text = row.Setting.Value is { } value ? FormatStep(value) : "";
            }
        }

        _updatingScaleControls = false;
    }

    private static string FormatStep(double step) => step.ToString("G10", CultureInfo.CurrentCulture);

    /// <summary>
    /// 軸の書式の欄を確かめ、悪い欄を赤枠とツールチップの理由で示し、すべて正しいときだけ「適用」を押せるようにする。
    /// 自動の欄(使わない欄)は知らせない。
    /// </summary>
    /// <returns>すべて正しいなら true。</returns>
    private bool ValidateAxisFormatInputs()
    {
        bool verticalValid = !ManualScale || ProfileAxisRange.TryParse(YMinimumBox.Text, YMaximumBox.Text,
            CultureInfo.CurrentCulture, out _);
        YScaleErrorText.Visibility = verticalValid ? Visibility.Collapsed : Visibility.Visible;
        MarkInvalidRangeInputs(YMinimumBox, YMaximumBox, ManualScale);
        bool horizontalValid = !ManualHorizontal || ProfileAxisRange.TryParse(XMinimumBox.Text, XMaximumBox.Text,
            CultureInfo.CurrentCulture, out _);
        MarkInvalidRangeInputs(XMinimumBox, XMaximumBox, ManualHorizontal);
        bool stepsValid = true;
        foreach (StepRow row in _stepRows)
        {
            string? error = StepError(row, out _);
            InputFeedback.SetError(row.Box, error);
            stepsValid &= error is null;
        }

        bool valid = verticalValid && horizontalValid && stepsValid;
        ApplyAxisFormatButton.IsEnabled = valid;
        return valid;
    }

    /// <summary>
    /// 範囲の欄のうち悪いほうを、ファイル一覧の絞り込み欄と同じく赤枠とツールチップの理由で示す。
    /// </summary>
    /// <remarks>
    /// 以前は欄の下の説明だけで、どちらの欄が悪いのかは欄の見た目で分からなかった。読めない欄はその欄に、
    /// 「最小 ＜ 最大」になっていないときは最大の欄に出す。手動でないときは欄を使わない(無効)ので知らせない。
    /// </remarks>
    private static void MarkInvalidRangeInputs(TextBox minimumBox, TextBox maximumBox, bool manual)
    {
        const string NotNumber = "有限の数値で指定してください。現在の表示範囲は変更していません。";
        bool minValid = ProfileAxisRange.TryParseLimit(minimumBox.Text, CultureInfo.CurrentCulture, out double min);
        bool maxValid = ProfileAxisRange.TryParseLimit(maximumBox.Text, CultureInfo.CurrentCulture, out double max);
        string? maxError = !maxValid ? NotNumber
            : !minValid ? null
            : !(min < max) ? "最小値より大きい値を指定してください。現在の表示範囲は変更していません。"
            : !double.IsFinite(max - min) ? "最小と最大の差が大きすぎます。現在の表示範囲は変更していません。"
            : null;
        InputFeedback.SetError(minimumBox, manual && !minValid ? NotNumber : null);
        InputFeedback.SetError(maximumBox, manual ? maxError : null);
    }

    // 指定の間隔の欄の理由(自動・正しければ null)。補助目盛りは、主目盛りも指定ならそれより小さい値に限る
    private static string? StepError(StepRow row, out double step)
    {
        step = 0;
        if (row.AutoCheck.IsChecked == true) return null;
        string? error = ProfileAxisTicks.ParseStep(row.Box.Text, CultureInfo.CurrentCulture, row.Integer,
            allowZero: row.MajorRow is not null, out step);
        if (error is null && step > 0 && row.MajorRow is { } major && major.AutoCheck.IsChecked != true
            && ProfileAxisTicks.ParseStep(major.Box.Text, CultureInfo.CurrentCulture, major.Integer, false,
                out double majorStep) is null
            && step >= majorStep)
        {
            return "目盛間隔より小さい値を指定してください(0 で補助目盛りなし)。";
        }

        return error;
    }

    private void OnAxisFormatTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready && !_updatingScaleControls) ValidateAxisFormatInputs();
    }

    private void OnAxisFormatKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyAxisFormat();
        e.Handled = true;
    }

    private void OnApplyAxisFormatClick(object sender, RoutedEventArgs e) => ApplyAxisFormat();

    private void ApplyAxisFormat()
    {
        if (!ValidateAxisFormatInputs()) return;
        if (ManualScale && ProfileAxisRange.TryParse(YMinimumBox.Text, YMaximumBox.Text,
                CultureInfo.CurrentCulture, out ProfileAxisRange vertical))
        {
            _manualRange = vertical;
        }

        if (ManualHorizontal && ProfileAxisRange.TryParse(XMinimumBox.Text, XMaximumBox.Text,
                CultureInfo.CurrentCulture, out ProfileAxisRange horizontal))
        {
            _manualHorizontal = horizontal;
        }

        foreach (StepRow row in _stepRows)
        {
            if (!row.Setting.Auto && StepError(row, out double step) is null) row.Setting.Value = step;
        }

        Redraw();
    }

    // ---- 目盛りの描画 ----

    private void DrawHorizontalAxis(double width, double height)
    {
        XAxisCanvas.Children.Clear();
        long offset = _data.CoordinateOffset;
        ProfileAxisRange horizontal = HorizontalRange;
        _horizontalTicks = _data.Values.Length == 0 || width < 4
            ? ProfileAxisTicks.Empty
            : ProfileAxisTicks.Layout(Shift(horizontal, offset), width, HorizontalTickSpacing, integerSteps: true,
                _xMajor.Manual, _xMinor.Manual);
        ProfileAxisTicks ticks = _horizontalTicks;
        bool grid = height >= 4;
        foreach (double coordinate in ticks.Minor)
        {
            double x = ProfilePlotNavigation.ToCanvasX(coordinate - offset, horizontal, width);
            XAxisCanvas.Children.Add(TickMark(x, 0, x, MinorTickLength));
            if (grid && _minorGrid) PlotCanvas.Children.Add(GridLine(x, 0, x, height, minor: true));
        }

        // ラベルが重ならないよう、両端(いちばん長くなる)のラベルの幅から何本おきに書くかを決める
        long every = ticks.Major.Count == 0 ? 1 : ProfileAxisTicks.LabelEvery(ticks.MajorSpacing,
            Math.Max(LabelSize(ticks, ticks.Major[0]).Width, LabelSize(ticks, ticks.Major[^1]).Width) + 8);
        foreach (double coordinate in ticks.Major)
        {
            double x = ProfilePlotNavigation.ToCanvasX(coordinate - offset, horizontal, width);
            XAxisCanvas.Children.Add(TickMark(x, 0, x, MajorTickLength));
            if (grid && _majorGrid) PlotCanvas.Children.Add(GridLine(x, 0, x, height, minor: false));
            if (!ticks.IsLabeled(coordinate, every)) continue;
            TextBlock label = AxisLabel(ticks, coordinate);
            Canvas.SetLeft(label, Math.Clamp(x - label.DesiredSize.Width / 2, 0, Math.Max(0, width - label.DesiredSize.Width)));
            Canvas.SetTop(label, 5);
            XAxisCanvas.Children.Add(label);
        }

        _horizontalNote = StepNote("横軸", ticks, _xMajor, _xMinor);
        XAxisCanvas.ToolTip = HorizontalAxisToolTip();
    }

    // 横軸のツールチップ(窓が決める座標の説明・操作・指定の間隔で描けなかったことの知らせ)
    private string HorizontalAxisToolTip() =>
        _xAxisToolTip + "\n" + XAxisHelp + (_horizontalNote is null ? "" : "\n\n" + _horizontalNote);

    private void DrawVerticalAxis(double width, double height)
    {
        YAxisCanvas.Children.Clear();
        // 縦の座標はグラフの上下 1px の内側(ProfileAxisRange.ToCanvasY)
        _verticalTicks = height < 4
            ? ProfileAxisTicks.Empty
            : ProfileAxisTicks.Layout(_axisRange, height - 2, VerticalTickSpacing, integerSteps: false,
                _yMajor.Manual, _yMinor.Manual);
        ProfileAxisTicks ticks = _verticalTicks;
        double axisWidth = YAxisCanvas.ActualWidth;
        bool grid = width >= 4;
        foreach (double value in ticks.Minor)
        {
            double y = _axisRange.ToCanvasY(value, height);
            YAxisCanvas.Children.Add(TickMark(axisWidth - MinorTickLength, y, axisWidth, y));
            if (grid && _minorGrid) PlotCanvas.Children.Add(GridLine(0, y, width, y, minor: true));
        }

        double labelHeight = AxisLabelHeight();
        long every = ProfileAxisTicks.LabelEvery(ticks.MajorSpacing, labelHeight + 4);
        foreach (double value in ticks.Major)
        {
            double y = _axisRange.ToCanvasY(value, height);
            YAxisCanvas.Children.Add(TickMark(axisWidth - MajorTickLength, y, axisWidth, y));
            if (grid && _majorGrid) PlotCanvas.Children.Add(GridLine(0, y, width, y, minor: false));
            // 上下の端には範囲の値(MaxLabel・MinLabel)を出すので、それと重なる目盛りのラベルは出さない
            if (!ticks.IsLabeled(value, every)
                || y - labelHeight / 2 < labelHeight + 1 || y + labelHeight / 2 > height - labelHeight - 1) continue;
            TextBlock label = AxisLabel(ticks, value);
            Canvas.SetRight(label, MajorTickLength + 2);
            Canvas.SetTop(label, y - label.DesiredSize.Height / 2);
            YAxisCanvas.Children.Add(label);
        }

        _verticalNote = StepNote("縦軸", ticks, _yMajor, _yMinor);
        YAxisArea.ToolTip = YAxisHelp + (_verticalNote is null ? "" : "\n\n" + _verticalNote);
    }

    // 指定の間隔で描けなかったことの知らせ(なければ null)
    private static string? StepNote(string axis, ProfileAxisTicks ticks, StepSetting major, StepSetting minor)
    {
        var notes = new List<string>();
        if (ticks.ManualMajorTooFine && major.Manual is { } majorStep)
        {
            notes.Add($"{axis}: 指定の目盛間隔 {FormatStep(majorStep)} はこの拡大率では細かすぎるため、" +
                      $"自動の間隔 {FormatStep(ticks.MajorStep)} で描いています(拡大すると指定の間隔になります)。");
        }

        if (ticks.ManualMinorOmitted && minor.Manual is { } minorStep)
        {
            notes.Add($"{axis}: 指定の補助目盛間隔 {FormatStep(minorStep)} は、この拡大率では細かすぎるか" +
                      "目盛間隔以上のため描いていません。");
        }

        return notes.Count == 0 ? null : string.Join("\n", notes);
    }

    private static Line TickMark(double x1, double y1, double x2, double y2) => new()
    {
        X1 = x1,
        X2 = x2,
        Y1 = y1,
        Y2 = y2,
        Stroke = TickBrush,
        StrokeThickness = 1,
    };

    private static Line GridLine(double x1, double y1, double x2, double y2, bool minor) => new()
    {
        X1 = x1,
        X2 = x2,
        Y1 = y1,
        Y2 = y2,
        Stroke = minor ? MinorGridBrush : MajorGridBrush,
        StrokeThickness = 1,
        Tag = minor ? MinorGridTag : MajorGridTag,
        IsHitTestVisible = false,
    };

    private static TextBlock AxisLabel(ProfileAxisTicks ticks, double value)
    {
        var label = new TextBlock
        {
            Text = ticks.Format(value, CultureInfo.CurrentCulture),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Foreground = Brushes.DarkGray,
            Tag = value,
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return label;
    }

    private static Size LabelSize(ProfileAxisTicks ticks, double value) => AxisLabel(ticks, value).DesiredSize;

    private double AxisLabelHeight()
    {
        if (_axisLabelHeight <= 0) _axisLabelHeight = LabelSize(ProfileAxisTicks.Empty, 0).Height;
        return _axisLabelHeight;
    }
}
