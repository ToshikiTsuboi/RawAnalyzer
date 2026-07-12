using System.Buffers;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RawViewer.App.Rendering;
using RawViewer.Core;

namespace RawViewer.App.Controls;

/// <summary>ビューポート状態(ズーム率・使用ピラミッドレベル)の通知引数。</summary>
public sealed class ViewportStateEventArgs : EventArgs
{
    /// <summary>ズーム率(表示px / 元画像px)。</summary>
    public double Zoom { get; init; }

    /// <summary>直近の描画に使った縮小率(1=元画像)。</summary>
    public int RenderedFactor { get; init; }
}

/// <summary>カーソル下の画素情報の通知引数。</summary>
public sealed class CursorPixelEventArgs : EventArgs
{
    /// <summary>画素X座標。</summary>
    public int X { get; init; }

    /// <summary>画素Y座標。</summary>
    public int Y { get; init; }

    /// <summary>カーソルが画像内にあるか。</summary>
    public bool IsInsideImage { get; init; }
}

/// <summary>
/// WriteableBitmapベースの画像ビューポートコントロール。
/// ホイールでカーソル中心ズーム、ドラッグでパン、ダブルクリックで全体表示。
/// ズーム率からピラミッドレベルを自動選択し、操作中は低解像度・静止後に高解像度で再描画する。
/// 描画はビューポート領域のみで、CancellationTokenで再入制御する。
/// </summary>
public sealed class ImageViewport : FrameworkElement
{
    private const double MinZoom = 1.0 / 512;
    private const double MaxZoom = 128;
    private const double ZoomStep = 1.25;
    private const double RawOverlayMinZoom = 32;
    private const int MaxOverlayCells = 8192;

    private static readonly Brush CanvasBrush =
        new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18));

    private readonly DispatcherTimer _idleTimer;

    private RawImage? _image;
    private RawFormat? _format;
    private int _frame;
    private TilePyramid? _pyramid;
    private DisplayLut _lut = DisplayLut.Create(new DisplayParameters());

    private double _zoom = 1.0;
    private double _originX;
    private double _originY;

    private WriteableBitmap? _bitmap;
    private CancellationTokenSource? _renderCts;
    private Task _renderTask = Task.CompletedTask;
    private int _renderedFactor = 1;

    private bool _panning;
    private Point _panStartPoint;
    private double _panStartOriginX;
    private double _panStartOriginY;

    private OverlayData? _overlay;

    /// <summary>コントロールを生成する。</summary>
    public ImageViewport()
    {
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            RequestRender(fast: false);
        };
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, _) => RequestRender(fast: false);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    /// <summary>ズーム率・使用レベルが変化したときに発火する。</summary>
    public event EventHandler<ViewportStateEventArgs>? ViewportStateChanged;

    /// <summary>カーソル下の画素が変化したときに発火する。</summary>
    public event EventHandler<CursorPixelEventArgs>? CursorPixelChanged;

    /// <summary>現在のズーム率。</summary>
    public double Zoom => _zoom;

    /// <summary>表示中の画像(未設定ならnull)。</summary>
    public RawImage? Image => _image;

    /// <summary>
    /// 進行中の描画をキャンセルして画像参照を解除する。
    /// 返されたTaskの完了後、旧画像を安全にDisposeできる。
    /// </summary>
    public async Task ClearImageAsync()
    {
        _renderCts?.Cancel();
        _idleTimer.Stop();
        _image = null;
        _format = null;
        _pyramid = null;
        _overlay = null;
        _bitmap = null;
        try
        {
            await _renderTask;
        }
        catch (OperationCanceledException)
        {
        }

        InvalidateVisual();
    }

    /// <summary>
    /// 表示する画像を設定し、全体表示にリセットする。
    /// 以前の画像はClearImageAsyncで解除しておくこと。
    /// </summary>
    /// <param name="image">画像。</param>
    /// <param name="format">フォーマット(raw値表示に使用)。</param>
    /// <param name="frame">フレーム番号。</param>
    public void SetImage(RawImage image, RawFormat format, int frame = 0)
    {
        _image = image;
        _format = format;
        _frame = frame;
        _pyramid = null;
        _overlay = null;
        FitToView();
    }

    /// <summary>生成完了したピラミッドを取り付け、高解像度で再描画する。</summary>
    /// <param name="pyramid">ピラミッド。</param>
    public void SetPyramid(TilePyramid? pyramid)
    {
        _pyramid = pyramid;
        RequestRender(fast: false);
    }

    /// <summary>表示LUTを差し替えて再描画する。</summary>
    /// <param name="lut">LUT。</param>
    public void SetLut(DisplayLut lut)
    {
        _lut = lut;
        RequestRender(fast: true);
        RestartIdleTimer();
    }

    /// <summary>1段階ズームインする(ビュー中心基準)。</summary>
    public void ZoomIn()
    {
        ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), _zoom * ZoomStep);
    }

    /// <summary>1段階ズームアウトする(ビュー中心基準)。</summary>
    public void ZoomOut()
    {
        ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), _zoom / ZoomStep);
    }

    /// <summary>等倍(1画素=1px)表示にする。</summary>
    public void ActualSize()
    {
        ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), 1.0);
    }

    /// <summary>画像全体が収まるようにズーム・位置をリセットする。</summary>
    public void FitToView()
    {
        if (_image is null || ActualWidth < 1 || ActualHeight < 1)
        {
            return;
        }

        double fit = Math.Min(ActualWidth / _image.Width, ActualHeight / _image.Height);
        _zoom = Math.Clamp(fit, MinZoom, MaxZoom);
        _originX = (_image.Width - ActualWidth / _zoom) / 2;
        _originY = (_image.Height - ActualHeight / _zoom) / 2;
        RequestRender(fast: true);
        RestartIdleTimer();
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(CanvasBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_bitmap is not null)
        {
            dc.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight));
        }

        DrawRawValueOverlay(dc);
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_image is null)
        {
            return;
        }

        double newZoom = e.Delta > 0 ? _zoom * ZoomStep : _zoom / ZoomStep;
        ZoomAt(e.GetPosition(this), newZoom);
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (_image is null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            FitToView();
            return;
        }

        _panning = true;
        _panStartPoint = e.GetPosition(this);
        _panStartOriginX = _originX;
        _panStartOriginY = _originY;
        CaptureMouse();
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_panning)
        {
            _panning = false;
            ReleaseMouseCapture();
            RestartIdleTimer();
        }
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_image is null)
        {
            return;
        }

        Point pos = e.GetPosition(this);
        if (_panning)
        {
            _originX = _panStartOriginX - (pos.X - _panStartPoint.X) / _zoom;
            _originY = _panStartOriginY - (pos.Y - _panStartPoint.Y) / _zoom;
            ClampOrigin();
            RequestRender(fast: true);
            RestartIdleTimer();
        }

        int px = (int)Math.Floor(_originX + pos.X / _zoom);
        int py = (int)Math.Floor(_originY + pos.Y / _zoom);
        bool inside = px >= 0 && py >= 0 && px < _image.Width && py < _image.Height;
        CursorPixelChanged?.Invoke(this, new CursorPixelEventArgs
        {
            X = px,
            Y = py,
            IsInsideImage = inside,
        });
    }

    private void ZoomAt(Point anchor, double newZoom)
    {
        if (_image is null)
        {
            return;
        }

        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 1e-9)
        {
            return;
        }

        _originX += anchor.X / _zoom - anchor.X / newZoom;
        _originY += anchor.Y / _zoom - anchor.Y / newZoom;
        _zoom = newZoom;
        ClampOrigin();
        RequestRender(fast: true);
        RestartIdleTimer();
    }

    private void ClampOrigin()
    {
        if (_image is null)
        {
            return;
        }

        double viewW = ActualWidth / _zoom;
        double viewH = ActualHeight / _zoom;
        double margin = 32 / _zoom;
        _originX = Math.Clamp(_originX, -viewW + margin, _image.Width - margin);
        _originY = Math.Clamp(_originY, -viewH + margin, _image.Height - margin);
    }

    private void RestartIdleTimer()
    {
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private RenderSource? SelectSource(bool fast)
    {
        if (_image is null)
        {
            return null;
        }

        int factor = _pyramid?.SelectFactor(_zoom) ?? 1;
        if (fast && _pyramid is not null)
        {
            // 操作中は1段粗いレベルで軽く描く
            PyramidLevel? coarser = _pyramid.GetLevel(factor * 2);
            if (coarser is not null)
            {
                factor *= 2;
            }
        }

        if (factor <= 1)
        {
            return new RawImageRenderSource(_image, _frame);
        }

        PyramidLevel level = _pyramid!.GetLevel(factor)!;
        return new PyramidLevelRenderSource(level, _image.Width, _image.Height);
    }

    private void RequestRender(bool fast)
    {
        _renderCts?.Cancel();
        if (_image is null || ActualWidth < 1 || ActualHeight < 1)
        {
            _bitmap = null;
            InvalidateVisual();
            return;
        }

        RenderSource? source = SelectSource(fast);
        if (source is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _renderCts = cts;
        int destW = Math.Max(1, (int)Math.Round(ActualWidth));
        int destH = Math.Max(1, (int)Math.Round(ActualHeight));
        double zoom = _zoom;
        double originX = _originX;
        double originY = _originY;
        DisplayLut lut = _lut;

        _renderTask = Task.Run(() =>
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(destW * destH);
            try
            {
                ViewportRenderer.Render(
                    source, lut, zoom, originX, originY, destW, destH, buffer, cts.Token);
                Dispatcher.Invoke(() =>
                {
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    Present(buffer, destW, destH, source.Factor, zoom, fast);
                });
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        });
    }

    private void Present(byte[] buffer, int width, int height, int factor, double zoom, bool fast)
    {
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Gray8, null);
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, width, 0);
        _renderedFactor = factor;
        _overlay = !fast && zoom >= RawOverlayMinZoom ? FetchOverlayData() : null;
        InvalidateVisual();
        ViewportStateChanged?.Invoke(this, new ViewportStateEventArgs
        {
            Zoom = zoom,
            RenderedFactor = factor,
        });
    }

    private sealed record OverlayData(int X0, int Y0, int Cols, int Rows, ushort[] Values);

    private OverlayData? FetchOverlayData()
    {
        if (_image is null || _format is null)
        {
            return null;
        }

        int x0 = Math.Max(0, (int)Math.Floor(_originX));
        int y0 = Math.Max(0, (int)Math.Floor(_originY));
        int x1 = Math.Min(_image.Width - 1, (int)Math.Floor(_originX + ActualWidth / _zoom) + 1);
        int y1 = Math.Min(_image.Height - 1, (int)Math.Floor(_originY + ActualHeight / _zoom) + 1);
        int cols = x1 - x0 + 1;
        int rows = y1 - y0 + 1;
        if (cols <= 0 || rows <= 0 || (long)cols * rows > MaxOverlayCells)
        {
            return null;
        }

        var values = new ushort[cols * rows];
        _image.CopyRegion(_frame, x0, y0, cols, rows, values);
        return new OverlayData(x0, y0, cols, rows, values);
    }

    private void DrawRawValueOverlay(DrawingContext dc)
    {
        if (_overlay is null || _format is null || _zoom < RawOverlayMinZoom)
        {
            return;
        }

        OverlayData ov = _overlay;
        int shift = 16 - _format.BitDepth;
        double fontSize = Math.Clamp(_zoom / 4.5, 9, 15);
        var typeface = new Typeface("Consolas");
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Brush dark = Brushes.Black;
        Brush light = Brushes.White;

        for (int r = 0; r < ov.Rows; r++)
        {
            double top = (ov.Y0 + r - _originY) * _zoom;
            if (top + _zoom < 0 || top > ActualHeight)
            {
                continue;
            }

            for (int c = 0; c < ov.Cols; c++)
            {
                double left = (ov.X0 + c - _originX) * _zoom;
                if (left + _zoom < 0 || left > ActualWidth)
                {
                    continue;
                }

                ushort value = ov.Values[r * ov.Cols + c];
                int code = value >> shift;
                Brush brush = _lut.Map(value) > 140 ? dark : light;
                var text = new FormattedText(
                    code.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    brush,
                    pixelsPerDip);
                dc.DrawText(text, new Point(
                    left + (_zoom - text.Width) / 2,
                    top + (_zoom - text.Height) / 2));
            }
        }
    }
}
