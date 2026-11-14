using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using RawAnalyzer.App.Themes;

namespace RawAnalyzer.App.Compare;

/// <summary>
/// 比較モードの1ペイン(ヘッダ + ビューポート)。
/// 資源(<see cref="ComparePane"/>)の装着と、ペイン単位の表示調整操作を担当する。
/// </summary>
public partial class ComparePaneView : UserControl
{
    private CancellationTokenSource? _autoContrast;
    internal Func<ComparePane, CancellationToken, Task<AutoLevels?>> CalculateAutoLevelsAsync { get; set; } =
        static (pane, token) => Task.Run(() => HistogramTools.ComputeAutoLevels(
            ImageAnalysis.ComputeHistogram(pane.Image, frame: 0, cancellationToken: token).Bins), token);
    private PaneNameDisplay _nameDisplay = new("", "", "");
    private double _fullNameWidth;
    private PaneNameDisplay? _shownName;

    // ユーザーがズーム/パン操作をするまでは全体表示を維持する。
    // Attach時点はレイアウト前でサイズが決まっておらず、その後もペインの
    // 追加・削除で自分のサイズが変わるため、「1回だけFit」では
    // 中間サイズで確定してしまう。未操作の間はサイズ変化へ追従させる
    private bool _autoFit;

    /// <summary>ビューを生成する。</summary>
    public ComparePaneView()
    {
        InitializeComponent();

        // ペイン内のどこをクリックしてもアクティブ化の対象にする
        PreviewMouseDown += (_, _) => ActivateRequested?.Invoke(this);
        FileNameText.SizeChanged += (_, _) => RefreshFileName();

        Viewport.SizeChanged += (_, e) =>
        {
            if (_autoFit && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                Viewport.FitToView();
            }
        };

        // ズーム/パンの手動操作でビューが変わったら追従をやめ、同期を発火する
        // (判定は ScheduleViewChanged。ビューを変えない入力は操作に数えない)
        Viewport.PreviewMouseWheel += (_, _) => ScheduleViewChanged();

        // ダブルクリックは全体表示(FitToView)。ドラッグと違い移動が
        // 一度で終わるので、押下でも確かめないと他ペインが取り残される
        Viewport.PreviewMouseDown += (_, _) => ScheduleViewChanged();

        // 矢印キーのパン(Ctrl+矢印の画素カーソルも、見える位置へのスクロールで)もビューを変える
        Viewport.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                ScheduleViewChanged();
            }
        };
        Viewport.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                ScheduleViewChanged();
            }
        };

        // カーソル連動(他ペインへのゴーストカーソル表示)用
        Viewport.CursorPixelChanged += (_, e) =>
        {
            if (e.IsInsideImage)
            {
                CursorMoved?.Invoke(this, e.X, e.Y);
            }
            else
            {
                CursorLeft?.Invoke(this);
            }
        };
        Viewport.KeyboardCursorCleared += () => CursorLeft?.Invoke(this);
        Viewport.MouseLeave += (_, _) => CursorLeft?.Invoke(this);
    }

    /// <summary>
    /// 入力の処理後にビューが変わっていたら、全体表示への追従をやめて同期を発火する。
    /// </summary>
    /// <remarks>
    /// ImageViewport自身のハンドラが状態を更新した「後」に読みたいので
    /// BeginInvokeで一拍置く(Previewイベントは処理前に来る)。
    /// ビューの変わらない入力(アクティブ化の単クリック・右/中クリック・画面内での画素カーソル移動・
    /// 全体表示中のダブルクリック)は操作に数えない。数えると未操作のまま追従が外れ、次のペイン増減や
    /// リサイズでこのペインだけ全体表示に戻らず(新しいペインもこれに揃い)、視野同期がずれる。
    /// </remarks>
    private void ScheduleViewChanged()
    {
        (double Zoom, double OriginX, double OriginY) before = CurrentView;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (CurrentView == before)
                {
                    return;
                }

                _autoFit = false;
                ViewChanged?.Invoke(this);
            }),
            DispatcherPriority.Input);
    }

    private (double Zoom, double OriginX, double OriginY) CurrentView =>
        (Viewport.Zoom, Viewport.OriginX, Viewport.OriginY);

    /// <summary>「✕」が押されたときに発火する。</summary>
    public event Action<ComparePaneView>? CloseRequested;

    /// <summary>ペインがクリックされたときに発火する(アクティブ化要求)。</summary>
    public event Action<ComparePaneView>? ActivateRequested;

    /// <summary>ユーザー操作でズーム/パンが変わったときに発火する(同期の起点)。</summary>
    public event Action<ComparePaneView>? ViewChanged;

    /// <summary>カーソルが画像上を動いたときに発火する(画素座標)。</summary>
    public event Action<ComparePaneView, int, int>? CursorMoved;

    /// <summary>カーソルが画像から離れたときに発火する。</summary>
    public event Action<ComparePaneView>? CursorLeft;

    /// <summary>ユーザー操作(自動/リセット)で表示調整が変わったときに発火する。</summary>
    public event Action<ComparePaneView>? DisplayChanged;

    /// <summary>装着中の資源。未装着ならnull。</summary>
    internal ComparePane? Pane { get; private set; }

    /// <summary>ビューポート(同期計算がビュー状態とサイズを読むために公開)。</summary>
    internal ImageViewport ViewportControl => Viewport;

    internal Size ImageAreaInsets => new(
        ActiveBorder.Margin.Left + ActiveBorder.Margin.Right
            + ActiveBorder.BorderThickness.Left + ActiveBorder.BorderThickness.Right,
        ActiveBorder.Margin.Top + ActiveBorder.Margin.Bottom
            + ActiveBorder.BorderThickness.Top + ActiveBorder.BorderThickness.Bottom
            + PaneHeader.Height + ChipsBar.Height);

    internal string Label => LabelText.Text;
    internal event Action? PaneAttached;

    /// <summary>
    /// 未操作で、全体表示をサイズ変化へ追従させている間はtrue
    /// (ズーム/パン操作や同期の適用で解除され、<see cref="ResumeAutoFit"/> で再開する)。
    /// </summary>
    internal bool IsAutoFit => _autoFit;

    /// <summary>
    /// 同期によるビュー変換の適用。ユーザー操作扱いにならず、
    /// <see cref="ViewChanged"/> は発火しない(入力イベント由来でないため)。
    /// 以後は同期に従うので全体表示の自動追従は解除する。
    /// </summary>
    /// <param name="zoom">ズーム倍率。</param>
    /// <param name="originX">表示原点X。</param>
    /// <param name="originY">表示原点Y。</param>
    internal void ApplyView(double zoom, double originX, double originY)
    {
        _autoFit = false;
        Viewport.SetViewTransform(zoom, originX, originY);
    }

    /// <summary>
    /// 全体表示へ戻し、未操作のときと同じくサイズ変化への追従を再開する
    /// (視野同期で他ペインを基準の追従に揃えるときや、等倍同期の基準を引き継ぐときに使う)。
    /// </summary>
    internal void ResumeAutoFit()
    {
        if (_autoFit)
        {
            return; // 追従中は常に全体表示なので描き直さない
        }

        _autoFit = true;
        Viewport.FitToView();
    }

    /// <summary>他ペインのカーソル位置をゴースト表示する。</summary>
    /// <param name="imageX">画像X座標。</param>
    /// <param name="imageY">画像Y座標。</param>
    internal void ShowGhostCursor(double imageX, double imageY)
    {
        Viewport.SetGhostCursor(imageX, imageY);
    }

    /// <summary>ゴーストカーソルを消す。</summary>
    internal void HideGhostCursor()
    {
        Viewport.ClearGhostCursor();
    }

    /// <summary>調整リンクがONか。</summary>
    internal bool IsLinked => LinkToggle.IsChecked == true;

    /// <summary>
    /// リンク/揃える操作による表示調整の適用。
    /// <see cref="DisplayChanged"/> は発火しない(ブロードキャストのループ防止)。
    /// </summary>
    /// <param name="settings">適用する表示調整。</param>
    internal void ApplyDisplay(DisplaySettings settings)
    {
        if (Pane is null || Pane.Display == settings)
        {
            return;
        }

        CancelAutoContrast();
        Pane.Display = settings;
        Viewport.SetLut(Pane.BuildLut());
    }

    // 不一致チップの強調色(ゴーストカーソルと同系の暖色)
    private static readonly Brush ChipDiffForeground = ThemeColors.Brush(ThemeColors.Warning);
    private static readonly Brush ChipDiffBackground = ThemeColors.Brush(ThemeColors.CompareDifferenceFill);

    /// <summary>条件チップを表示し直す(不一致判定の計算はCompareView側)。</summary>
    /// <param name="chips">テキストと不一致フラグの列。</param>
    internal void SetChips(IReadOnlyList<(string Text, bool Differs)> chips)
    {
        ChipsPanel.Children.Clear();
        foreach ((string text, bool differs) in chips)
        {
            ChipsPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Background = differs ? ChipDiffBackground : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = ThemeTypography.FontSizeCaption,
                    Foreground = differs
                        ? ChipDiffForeground
                        : (Brush)FindResource("TextDim"),
                },
            });
        }
    }

    /// <summary>アクティブ表示(枠の強調)を切り替える。</summary>
    public bool IsActive
    {
        set => ActiveBorder.BorderBrush = value
            ? (Brush)FindResource("AccentBrush")
            : Brushes.Transparent;
    }

    /// <summary>ヘッダのペインラベル(A/B/C/D)を設定する。</summary>
    /// <param name="label">表示するラベル。</param>
    public void SetLabel(string label)
    {
        LabelText.Text = label;
    }

    internal void SetFileNameDisplay(PaneNameDisplay display)
    {
        _nameDisplay = display;
        _fullNameWidth = MeasureFileName(display.FullName);
        RefreshFileName();
    }

    private double MeasureFileName(string text) =>
        new FormattedText(text, CultureInfo.CurrentUICulture,
            FileNameText.FlowDirection, new Typeface(FileNameText.FontFamily, FileNameText.FontStyle,
                FileNameText.FontWeight, FileNameText.FontStretch), FileNameText.FontSize,
            FileNameText.Foreground, VisualTreeHelper.GetDpi(FileNameText).PixelsPerDip).WidthIncludingTrailingWhitespace;

    private void RefreshFileName()
    {
        PaneNameDisplay display = _nameDisplay.ForWidth(FileNameText.ActualWidth, _fullNameWidth, MeasureFileName);
        if (_shownName == display)
        {
            return;
        }

        _shownName = display;
        FileNameText.Inlines.Clear();
        FileNameText.Inlines.Add(new Run(display.Prefix));
        FileNameText.Inlines.Add(new Run(display.Difference) { Foreground = (Brush)FindResource("AccentBrush") });
        FileNameText.Inlines.Add(new Run(display.Suffix));
    }

    /// <summary>
    /// 資源を装着して表示を開始する。所有権はこのビューに移る
    /// (<see cref="DetachAndDisposeAsync"/> で破棄する)。
    /// 縮小表示用ピラミッドの生成完了は待たない。
    /// </summary>
    /// <param name="pane">装着する資源。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    internal void Attach(ComparePane pane, CancellationToken cancellationToken = default)
    {
        CancelAutoContrast();
        Pane = pane;
        FileNameText.ToolTip = pane.Path;
        SetFileNameDisplay(new PaneNameDisplay(pane.FileName, "", ""));
        ChipsBar.Visibility = Visibility.Visible;

        Viewport.SetImage(pane.Image, pane.Format);
        Viewport.SetColorImage(pane.Color);
        Viewport.SetLut(pane.BuildLut());
        _autoFit = true;
        if (Viewport.ActualWidth > 0 && Viewport.ActualHeight > 0)
        {
            Viewport.FitToView();
        }

        // 縮小表示用ピラミッドは表示開始を待たせない。
        // 待つと次のファイルの読み込み開始まで止まり、複数枚ドロップで待ち時間が累積する
        _ = BuildPyramidAsync(pane, cancellationToken);
        PaneAttached?.Invoke();
    }

    /// <summary>縮小表示用ピラミッドを裏で用意する(失敗しても等倍表示はできる)。</summary>
    /// <param name="pane">対象の資源。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>生成の完了を表すタスク。</returns>
    private async Task BuildPyramidAsync(ComparePane pane, CancellationToken cancellationToken)
    {
        try
        {
            await pane.EnsureTilePyramidAsync(frame: 0, cancellationToken);
            if (ReferenceEquals(pane, Pane) && pane.Pyramid is not null)
            {
                Viewport.SetPyramid(pane.Pyramid, pane.PyramidFrame);
            }
        }
        catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
        {
            // ペインが閉じられた/比較モードを抜けた
        }
        catch (Exception ex)
        {
            AppLog.Warn($"比較ペインのピラミッド生成に失敗: {ex.Message}");
        }
    }

    /// <summary>
    /// ビューポートの描画を止めて資源を破棄する。
    /// </summary>
    /// <returns>破棄完了を表すタスク。</returns>
    internal async Task DetachAndDisposeAsync()
    {
        CancelAutoContrast();
        ComparePane? pane = Pane;
        Pane = null;
        await Viewport.ClearImageAsync();
        pane?.Dispose();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this);
    }

    private void OnLinkChanged(object sender, RoutedEventArgs e) => ActivateRequested?.Invoke(this);

    private async void OnAutoClick(object sender, RoutedEventArgs e)
    {
        ActivateRequested?.Invoke(this);
        await AutoContrastAsync();
    }

    internal void CancelAutoContrast()
    {
        CancellationTokenSource? job = _autoContrast;
        _autoContrast = null;
        job?.Cancel();
    }

    internal async Task AutoContrastAsync()
    {
        if (Pane is not { } pane || _autoContrast is not null)
        {
            return;
        }

        using var job = new CancellationTokenSource();
        _autoContrast = job;
        try
        {
            // 同じ画像に対して、計算開始後に表示調整が変わっていなければ適用する。
            AutoLevels? result = await CalculateAutoLevelsAsync(pane, job.Token);
            if (job.IsCancellationRequested || !ReferenceEquals(pane, Pane))
            {
                return;
            }

            if (result is { } levels)
            {
                _autoContrast = null;
                EditDisplay(pane.Display with
                {
                    BlackCode = levels.BlackCode,
                    WhiteCode = levels.WhiteCode,
                });
            }
        }
        catch (OperationCanceledException) when (job.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warn($"比較ペインの自動コントラストに失敗: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_autoContrast, job))
            {
                _autoContrast = null;
            }
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        ActivateRequested?.Invoke(this);
        ResetDisplay();
    }

    internal void ResetDisplay()
    {
        if (Pane is { } pane)
        {
            EditDisplay(DisplaySettings.CreateDefault(pane.Format.BitDepth));
        }
    }

    internal void EditDisplay(DisplaySettings settings)
    {
        if (Pane is not null)
        {
            ApplyDisplay(settings);
            DisplayChanged?.Invoke(this);
        }
    }
}
