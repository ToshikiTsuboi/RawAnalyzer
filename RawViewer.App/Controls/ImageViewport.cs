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

/// <summary>ビューポートのマウス操作モード。</summary>
public enum ViewportInteractionMode
{
    /// <summary>ドラッグでパン(既定)。</summary>
    Pan,

    /// <summary>ドラッグでROI矩形選択。</summary>
    RoiSelect,

    /// <summary>クリックでラインプロファイル位置指定。</summary>
    LineProfile,

    /// <summary>クリックでホワイトバランス基準点指定(スポイト)。</summary>
    WhiteBalancePick,
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
    private DevelopLuts _developLuts = DevelopLuts.Create(new DevelopParameters());
    private ViewportDisplayMode _displayMode = ViewportDisplayMode.Raw;
    private DisplayLut[]? _segmentLuts;
    private int _segmentWidth;
    private bool _zebraEnabled;
    private IReadOnlyList<DefectPixel>? _defectMarkers;

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

    private bool _roiDragging;
    private int _roiStartX;
    private int _roiStartY;
    private RegionOfInterest? _roi;

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

    /// <summary>ROIが確定・解除されたときに発火する。</summary>
    public event EventHandler? RoiChanged;

    /// <summary>ラインプロファイルモードで画素がクリックされたときに発火する。</summary>
    public event EventHandler<CursorPixelEventArgs>? ProfilePointClicked;

    /// <summary>スポイトモードで画素がクリックされたときに発火する。</summary>
    public event EventHandler<CursorPixelEventArgs>? WhiteBalancePicked;

    /// <summary>現在の表示モード。</summary>
    public ViewportDisplayMode DisplayMode => _displayMode;

    /// <summary>マウス操作モード。</summary>
    public ViewportInteractionMode InteractionMode { get; set; } = ViewportInteractionMode.Pan;

    /// <summary>現在のROI(未選択ならnull)。</summary>
    public RegionOfInterest? Roi => _roi;

    /// <summary>現在のズーム率。</summary>
    public double Zoom => _zoom;

    /// <summary>表示中の画像(未設定ならnull)。</summary>
    public RawImage? Image => _image;

    /// <summary>表示中のフレーム番号。</summary>
    public int Frame => _frame;

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
        _segmentLuts = null;
        ClearRoi();
        FitToView();
    }

    /// <summary>ROI選択を解除する。</summary>
    public void ClearRoi()
    {
        bool had = _roi is not null;
        _roi = null;
        _roiDragging = false;
        if (had)
        {
            RoiChanged?.Invoke(this, EventArgs.Empty);
        }

        InvalidateVisual();
    }

    /// <summary>生成完了したピラミッドを取り付け、高解像度で再描画する。</summary>
    /// <param name="pyramid">ピラミッド。</param>
    public void SetPyramid(TilePyramid? pyramid)
    {
        _pyramid = pyramid;
        RequestRender(fast: false);
    }

    /// <summary>
    /// 表示フレームを切り替える(ズーム/位置は維持)。
    /// ピラミッドはフレーム0のみ有効なため、他フレームは等倍データから描画する。
    /// </summary>
    /// <param name="frame">フレーム番号。</param>
    public void SetFrame(int frame)
    {
        if (_image is null || frame == _frame || (uint)frame >= (uint)_image.FrameCount)
        {
            return;
        }

        _frame = frame;
        _overlay = null;
        RequestRender(fast: false);
    }

    /// <summary>
    /// 表示画像をズーム/位置を維持したまま差し替える(シーケンス再生用)。
    /// 新しい描画が確定するまで旧ビットマップを表示し続けるためチラつかない。
    /// </summary>
    /// <param name="image">新しい画像(同一サイズであること)。</param>
    /// <param name="format">フォーマット。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <returns>差し替え前の画像。呼び出し側でDisposeすること。</returns>
    public async Task<RawImage?> ReplaceImageAsync(RawImage image, RawFormat format, int frame = 0)
    {
        RawImage? old = _image;
        _renderCts?.Cancel();
        Task pending = _renderTask;
        _image = image;
        _format = format;
        _frame = frame;
        _pyramid = null;
        _overlay = null;
        RequestRender(fast: false);
        try
        {
            await pending;
        }
        catch (OperationCanceledException)
        {
        }

        return old;
    }

    /// <summary>表示LUTを差し替えて再描画する。</summary>
    /// <param name="lut">LUT。</param>
    public void SetLut(DisplayLut lut)
    {
        _lut = lut;
        RequestRender(fast: true);
        RestartIdleTimer();
    }

    /// <summary>表示モードを切り替えて再描画する。</summary>
    /// <param name="mode">表示モード。</param>
    public void SetDisplayMode(ViewportDisplayMode mode)
    {
        _displayMode = mode;
        _overlay = null;
        RequestRender(fast: false);
    }

    /// <summary>カラー現像LUTを差し替え、現像モードなら再描画する。</summary>
    /// <param name="luts">現像LUT。</param>
    public void SetDevelopLuts(DevelopLuts luts)
    {
        _developLuts = luts;
        if (_displayMode == ViewportDisplayMode.ColorDevelop)
        {
            RequestRender(fast: true);
            RestartIdleTimer();
        }
    }

    /// <summary>ゼブラ(飽和/黒潰れ警告)の表示を切り替えて再描画する。</summary>
    /// <param name="enabled">表示するかどうか。</param>
    public void SetZebra(bool enabled)
    {
        _zebraEnabled = enabled;
        RequestRender(fast: false);
    }

    /// <summary>欠陥画素マーカーを設定する(nullで解除)。白点=赤/黒点=青の丸で表示。</summary>
    /// <param name="defects">欠陥画素リスト。</param>
    public void SetDefectMarkers(IReadOnlyList<DefectPixel>? defects)
    {
        _defectMarkers = defects;
        InvalidateVisual();
    }

    /// <summary>
    /// 指定画素がビュー中央に来るようにズーム・位置を設定する。
    /// </summary>
    /// <param name="x">画素X座標。</param>
    /// <param name="y">画素Y座標。</param>
    /// <param name="zoom">ズーム率。</param>
    public void CenterOn(int x, int y, double zoom)
    {
        if (_image is null || ActualWidth < 1 || ActualHeight < 1)
        {
            return;
        }

        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _originX = x + 0.5 - ActualWidth / (2 * _zoom);
        _originY = y + 0.5 - ActualHeight / (2 * _zoom);
        ClampOrigin();
        RequestRender(fast: false);
    }

    /// <summary>
    /// HDR分割表示用のフレーム別LUTを設定する(nullで解除)。
    /// X座標をsegmentWidthで区切ってセグメントごとに適用する。
    /// </summary>
    /// <param name="luts">フレーム別LUT(左から順)。</param>
    /// <param name="segmentWidth">セグメント幅(元画像px)。</param>
    public void SetSplitLuts(DisplayLut[]? luts, int segmentWidth)
    {
        _segmentLuts = luts;
        _segmentWidth = segmentWidth;
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
        DrawRoi(dc);
        DrawDefectMarkers(dc);
    }

    private static readonly Pen HotMarkerPen = CreateMarkerPen(Color.FromRgb(0xE6, 0x50, 0x3C));
    private static readonly Pen DeadMarkerPen = CreateMarkerPen(Color.FromRgb(0x3C, 0x78, 0xE6));

    private static Pen CreateMarkerPen(Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1.5);
        pen.Freeze();
        return pen;
    }

    private void DrawDefectMarkers(DrawingContext dc)
    {
        if (_defectMarkers is null || _image is null)
        {
            return;
        }

        double radius = Math.Max(5, _zoom * 0.7);
        foreach (DefectPixel defect in _defectMarkers)
        {
            double cx = (defect.X + 0.5 - _originX) * _zoom;
            double cy = (defect.Y + 0.5 - _originY) * _zoom;
            if (cx < -radius || cy < -radius
                || cx > ActualWidth + radius || cy > ActualHeight + radius)
            {
                continue;
            }

            dc.DrawEllipse(
                null,
                defect.Type == DefectType.Hot ? HotMarkerPen : DeadMarkerPen,
                new Point(cx, cy), radius, radius);
        }
    }

    private static readonly Pen RoiPen = CreateRoiPen();
    private static readonly Brush RoiFill = CreateRoiFill();

    private static Pen CreateRoiPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x5B, 0x9D, 0xD9)), 1.5);
        pen.Freeze();
        return pen;
    }

    private static Brush CreateRoiFill()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x20, 0x5B, 0x9D, 0xD9));
        brush.Freeze();
        return brush;
    }

    private void DrawRoi(DrawingContext dc)
    {
        if (_roi is not { PixelCount: > 0 } roi)
        {
            return;
        }

        double x = (roi.X - _originX) * _zoom;
        double y = (roi.Y - _originY) * _zoom;
        dc.DrawRectangle(RoiFill, RoiPen, new Rect(x, y, roi.Width * _zoom, roi.Height * _zoom));
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

        Point pos = e.GetPosition(this);
        if (InteractionMode == ViewportInteractionMode.RoiSelect)
        {
            _roiStartX = (int)Math.Floor(_originX + pos.X / _zoom);
            _roiStartY = (int)Math.Floor(_originY + pos.Y / _zoom);
            _roiDragging = true;
            _roi = null;
            CaptureMouse();
            return;
        }

        if (InteractionMode is ViewportInteractionMode.LineProfile
            or ViewportInteractionMode.WhiteBalancePick)
        {
            int px = (int)Math.Floor(_originX + pos.X / _zoom);
            int py = (int)Math.Floor(_originY + pos.Y / _zoom);
            if (px >= 0 && py >= 0 && px < _image.Width && py < _image.Height)
            {
                var args = new CursorPixelEventArgs { X = px, Y = py, IsInsideImage = true };
                if (InteractionMode == ViewportInteractionMode.LineProfile)
                {
                    ProfilePointClicked?.Invoke(this, args);
                }
                else
                {
                    WhiteBalancePicked?.Invoke(this, args);
                }
            }

            return;
        }

        _panning = true;
        _panStartPoint = pos;
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

        if (_roiDragging)
        {
            _roiDragging = false;
            ReleaseMouseCapture();
            if (_roi is { PixelCount: > 0 })
            {
                RoiChanged?.Invoke(this, EventArgs.Empty);
            }
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

        if (_roiDragging)
        {
            int cx = (int)Math.Floor(_originX + pos.X / _zoom);
            int cy = (int)Math.Floor(_originY + pos.Y / _zoom);
            int x0 = Math.Min(_roiStartX, cx);
            int y0 = Math.Min(_roiStartY, cy);
            int x1 = Math.Max(_roiStartX, cx);
            int y1 = Math.Max(_roiStartY, cy);
            _roi = new RegionOfInterest(x0, y0, x1 - x0 + 1, y1 - y0 + 1)
                .Clamp(_image.Width, _image.Height);
            InvalidateVisual();
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

        if (_displayMode == ViewportDisplayMode.ChannelSplit
            && _format?.Bayer != BayerPattern.None)
        {
            return new ChannelSplitRenderSource(_image, _frame);
        }

        if (_displayMode is ViewportDisplayMode.BayerColor or ViewportDisplayMode.ColorDevelop
            && _format?.Bayer != BayerPattern.None)
        {
            // カラー系はBayer位相が必要なため常に等倍データから描画する
            return new RawImageRenderSource(_image, _frame);
        }

        // ピラミッドはフレーム0のデータから生成されるため他フレームでは使わない
        TilePyramid? pyramid = _frame == 0 ? _pyramid : null;
        int factor = pyramid?.SelectFactor(_zoom) ?? 1;
        if (fast && pyramid is not null)
        {
            // 操作中は1段粗いレベルで軽く描く
            PyramidLevel? coarser = pyramid.GetLevel(factor * 2);
            if (coarser is not null)
            {
                factor *= 2;
            }
        }

        if (factor <= 1)
        {
            return new RawImageRenderSource(_image, _frame);
        }

        PyramidLevel level = pyramid!.GetLevel(factor)!;
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
        var request = new RenderRequest
        {
            Source = source,
            Lut = _lut,
            Mode = _displayMode,
            Pattern = _format?.Bayer ?? BayerPattern.None,
            DevelopLuts = _developLuts,
            SegmentLuts = _segmentLuts,
            SegmentWidth = _segmentWidth,
            ZebraEnabled = _zebraEnabled,
        };

        _renderTask = Task.Run(() =>
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(destW * destH * 4);
            try
            {
                ViewportRenderer.Render(
                    request, zoom, originX, originY, destW, destH, buffer, cts.Token);
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
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, width * 4, 0);
        _renderedFactor = factor;
        _overlay = !fast && zoom >= RawOverlayMinZoom
            && _displayMode != ViewportDisplayMode.ChannelSplit
            ? FetchOverlayData()
            : null;
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
