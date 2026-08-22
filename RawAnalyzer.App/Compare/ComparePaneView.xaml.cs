using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        // ズーム/パンの手動操作が入ったら追従をやめる
        Viewport.PreviewMouseWheel += (_, _) => _autoFit = false;
        Viewport.PreviewMouseDown += (_, _) => _autoFit = false;
    }

    /// <summary>「✕」が押されたときに発火する。</summary>
    public event Action<ComparePaneView>? CloseRequested;

    /// <summary>ペインがクリックされたときに発火する(アクティブ化要求)。</summary>
    public event Action<ComparePaneView>? ActivateRequested;

    /// <summary>装着中の資源。未装着ならnull。</summary>
    internal ComparePane? Pane { get; private set; }

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
    /// <returns>初期表示の完了を表すタスク。</returns>
    internal async Task AttachAsync(ComparePane pane)
    {
        Pane = pane;
        FileNameText.Text = pane.FileName;
        FileNameText.ToolTip = pane.Path;

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
            await pane.EnsureTilePyramidAsync();
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
    }
}
