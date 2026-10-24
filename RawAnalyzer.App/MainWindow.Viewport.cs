using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Globalization;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    private int _lastCursorX;
    private int _lastCursorY;
    private bool _lastCursorInside;

    // ---- ビューポートイベント ----

    private void OnViewportStateChanged(object? sender, ViewportStateEventArgs e)
    {
        // 倍率は画面のデバイス画素あたり(等倍で 100%。表示倍率 100% 以外でも元画像 1 画素 = 1 デバイス画素)
        string percent = e.DeviceZoom >= 0.1
            ? $"{e.DeviceZoom * 100:F0}%"
            : $"{e.DeviceZoom * 100:F2}%";
        int levelIndex = (int)Math.Round(Math.Log2(e.RenderedFactor));
        _vm.ZoomPercentText = percent;
        _vm.ZoomStatusText = $"Zoom {percent} · L{levelIndex}";

        // HDR派生ビューの説明・フルスクリーンの案内を上書きで消さず、間引きレベルをその下に出す
        _overlayRenderedFactor = e.RenderedFactor;
        UpdateLevelOverlay();
    }

    private void OnCursorPixelChanged(object? sender, CursorPixelEventArgs e)
    {
        RawImage? image = ActiveImage;
        RawFormat? format = ActiveFormat;
        if (image is null || format is null || !e.IsInsideImage)
        {
            _lastCursorInside = false;
            _vm.CursorStatusText = "";
            _vm.CursorOverlayText = "";
            return;
        }

        // チャネル分割表示ではタイル座標を元画像座標へ写像する。奇数寸法の端の行・列(どのタイルにも並ばない)
        // など、表示されている画素に対応しない位置は範囲外として表示を消す。前の画素の値と位置を残すと、
        // Ctrl+C(画素値・座標のコピー)がカーソルの下にない前の画素をコピーする
        if (!TryMapToSourceCoordinates(image, e.X, e.Y, out int sourceX, out int sourceY))
        {
            ClearCursorReadout();
            return;
        }

        if (CursorReadout.Compose(image, format, _colorImage, sourceX, sourceY, Viewport.Frame)
            is not { } text)
        {
            ClearCursorReadout();
            return;
        }

        _lastCursorX = sourceX;
        _lastCursorY = sourceY;
        _lastCursorInside = true;
        _vm.CursorStatusText = text.Status;
        _vm.CursorOverlayText = text.Overlay;
    }

    /// <summary>
    /// カーソル位置を保ったまま表示中の画像・フレーム・フォーマットが替わった後に、カーソル位置の画素値の表示を
    /// 表示中の画像・フレームから作り直す(読めなければ消す)。
    /// </summary>
    /// <remarks>
    /// 表示は <see cref="OnCursorPixelChanged"/>(マウス移動・キーボードの画素カーソル)でしか作られないので、
    /// フレーム・ページ・ファイルの送り、再生、右パネルの Bayer の変更の後に呼ばないと、前の画像・フレームの値と
    /// 前のパターンのチャネル名を出し続ける。座標の意味が変わる差し替え(HDR表示の出入り、別ファイルを開く、
    /// 寸法の変わる差し替え)では読み直さず <see cref="ClearCursorReadout"/> で消す。
    /// </remarks>
    private void RefreshCursorReadout()
    {
        if (_lastCursorInside && ActiveImage is { } image && ActiveFormat is { } format
            && CursorReadout.Compose(image, format, _colorImage, _lastCursorX, _lastCursorY, Viewport.Frame)
                is { } text)
        {
            _vm.CursorStatusText = text.Status;
            _vm.CursorOverlayText = text.Overlay;
            return;
        }

        ClearCursorReadout();
    }

    /// <summary>
    /// カーソル位置の画素値の表示を消し、カーソル位置を持たない状態にする。
    /// </summary>
    /// <remarks>
    /// 座標の意味が変わる差し替え(HDR表示の出入り、別ファイルを開く、寸法の変わる差し替え)で呼ぶ。位置を残すと、
    /// 前の画像の値を出し続けるうえ、Ctrl+C(カーソル位置の画素値・座標のコピー)が新しい画像の同じ数値座標、
    /// つまり別の画素を黙ってコピーする。次にマウス・画素カーソルを動かせば表示し直す。
    /// </remarks>
    private void ClearCursorReadout()
    {
        _lastCursorInside = false;
        _vm.CursorStatusText = "";
        _vm.CursorOverlayText = "";
    }

    // ---- ズーム操作 ----

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        Viewport.ZoomIn();
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        Viewport.ZoomOut();
    }

    private void OnActualSizeClick(object sender, RoutedEventArgs e)
    {
        Viewport.ActualSize();
    }

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        Viewport.FitToView();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ---- 右クリックメニュー ----

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        return source as T;
    }

    private void OnZoomHereClick(object sender, RoutedEventArgs e)
    {
        if (_lastCursorInside)
        {
            // カーソル位置は元画像の座標で持っている(分割表示ではタイル上の位置へ戻す)。
            // 画素値が重ねて見えるズーム(デバイス基準の 32 倍)まで拡大する
            Viewport.CenterOnSourcePixel(
                _lastCursorX, _lastCursorY, Math.Max(Viewport.Zoom, Viewport.RawOverlayZoom));
        }
    }

    private void OnCopyPixelValueClick(object sender, RoutedEventArgs e)
    {
        if (ActiveImage is null || !_lastCursorInside)
        {
            return;
        }

        ushort value;
        try
        {
            value = ActiveImage.GetPixel(_lastCursorX, _lastCursorY, Viewport.Frame);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or ObjectDisposedException)
        {
            return;
        }

        ClipboardHelper.TrySetText((value >> CurrentShift).ToString(CultureInfo.InvariantCulture));
    }

    private void OnCopyPixelPosClick(object sender, RoutedEventArgs e)
    {
        if (_lastCursorInside)
        {
            ClipboardHelper.TrySetText($"{_lastCursorX}\t{_lastCursorY}");
        }
    }

    private void OnCopyViewClick(object sender, RoutedEventArgs e)
    {
        // 高さのガードが無いと ActualHeight=0 で ArgumentOutOfRangeException になる
        if (!_vm.HasImage || Viewport.ActualWidth < 1 || Viewport.ActualHeight < 1)
        {
            return;
        }

        RenderTargetBitmap bitmap;
        try
        {
            // 画面と同じデバイス解像度で作る(DIP の大きさで作ると、高DPIでは表示が間引かれる)
            bitmap = Viewport.CaptureView();
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException)
        {
            AppLog.Warn($"表示のコピー用ビットマップ生成に失敗: {ex.Message}");
            return;
        }

        _vm.ImageInfoText = ClipboardHelper.TrySetImage(bitmap)
            ? "表示をクリップボードへコピーしました"
            : "クリップボードを使用できませんでした(他のアプリが使用中の可能性があります)";
    }

    private void OnClearRoiClick(object sender, RoutedEventArgs e)
    {
        Viewport.ClearRoi();
    }
}
