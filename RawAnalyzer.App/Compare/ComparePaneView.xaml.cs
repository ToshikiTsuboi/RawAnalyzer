using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Compare;

/// <summary>
/// 比較モードの1ペイン(ヘッダ + ビューポート)。
/// 資源(<see cref="ComparePane"/>)の装着と、ペイン単位の表示調整操作を担当する。
/// </summary>
public partial class ComparePaneView : UserControl
{
    private bool _adjusting;

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

        Viewport.SizeChanged += (_, e) =>
        {
            if (_autoFit && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                Viewport.FitToView();
            }
        };

        // ズーム/パンの手動操作が入ったら追従をやめ、同期を発火する。
        // ImageViewport自身のハンドラが状態を更新した「後」に読みたいので
        // BeginInvokeで一拍置く(Previewイベントは処理前に来る)
        Viewport.PreviewMouseWheel += (_, _) =>
        {
            _autoFit = false;
            ScheduleViewChanged();
        };
        Viewport.PreviewMouseDown += (_, _) => _autoFit = false;
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
        Viewport.MouseLeave += (_, _) => CursorLeft?.Invoke(this);
    }

    private void ScheduleViewChanged()
    {
        Dispatcher.BeginInvoke(
            new Action(() => ViewChanged?.Invoke(this)), DispatcherPriority.Input);
    }

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

    /// <summary>調整リンク(🔗)がONか。</summary>
    internal bool IsLinked => LinkToggle.IsChecked == true;

    /// <summary>
    /// リンク/揃える操作による表示調整の適用。
    /// <see cref="DisplayChanged"/> は発火しない(ブロードキャストのループ防止)。
    /// </summary>
    /// <param name="settings">適用する表示調整。</param>
    internal void ApplyDisplay(DisplaySettings settings)
    {
        if (Pane is null)
        {
            return;
        }

        Pane.Display = settings;
        Viewport.SetLut(Pane.BuildLut());
    }

    // 不一致チップの強調色(ゴーストカーソルと同系の暖色)
    private static readonly Brush ChipDiffForeground = CreateFrozen(0xFF, 0xE8, 0xA3, 0x4B);
    private static readonly Brush ChipDiffBackground = CreateFrozen(0x28, 0xE8, 0xA3, 0x4B);

    private static Brush CreateFrozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

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
                    FontSize = 11,
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

    /// <summary>
    /// 資源を装着して表示を開始する。所有権はこのビューに移る
    /// (<see cref="DetachAndDisposeAsync"/> で破棄する)。
    /// </summary>
    /// <param name="pane">装着する資源。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>初期表示の完了を表すタスク。</returns>
    internal async Task AttachAsync(ComparePane pane, CancellationToken cancellationToken = default)
    {
        Pane = pane;
        FileNameText.Text = pane.FileName;
        FileNameText.ToolTip = pane.Path;
        ChipsBar.Visibility = Visibility.Visible;

        Viewport.SetImage(pane.Image, pane.Format);
        Viewport.SetColorImage(pane.Color);
        Viewport.SetLut(pane.BuildLut());
        _autoFit = true;
        if (Viewport.ActualWidth > 0 && Viewport.ActualHeight > 0)
        {
            Viewport.FitToView();
        }

        // 縮小表示用ピラミッド。失敗しても等倍表示はできるので落とさない
        try
        {
            await pane.EnsureTilePyramidAsync(frame: 0, cancellationToken);
            if (ReferenceEquals(pane, Pane) && pane.Pyramid is not null)
            {
                Viewport.SetPyramid(pane.Pyramid, pane.PyramidFrame);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
        ComparePane? pane = Pane;
        Pane = null;
        await Viewport.ClearImageAsync();
        pane?.Dispose();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this);
    }

    private async void OnAutoClick(object sender, RoutedEventArgs e)
    {
        ComparePane? pane = Pane;
        if (pane is null || _adjusting)
        {
            return;
        }

        _adjusting = true;
        try
        {
            // カラー画像でも輝度ヒストグラムからのクリップで実用上十分
            HistogramResult histogram = await Task.Run(
                () => ImageAnalysis.ComputeHistogram(pane.Image, frame: 0));
            if (!ReferenceEquals(pane, Pane))
            {
                return; // 計算中に閉じられた
            }

            if (HistogramTools.ComputeAutoLevels(histogram.Bins) is { } levels)
            {
                pane.Display = pane.Display with
                {
                    BlackCode = levels.BlackCode,
                    WhiteCode = levels.WhiteCode,
                };
                Viewport.SetLut(pane.BuildLut());
                DisplayChanged?.Invoke(this);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"比較ペインの自動コントラストに失敗: {ex.Message}");
        }
        finally
        {
            _adjusting = false;
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (Pane is null)
        {
            return;
        }

        Pane.Display = DisplaySettings.CreateDefault(Pane.Format.BitDepth);
        Viewport.SetLut(Pane.BuildLut());
        DisplayChanged?.Invoke(this);
    }
}
