using System.Buffers;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Controls;

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
    private BayerPyramid? _bayerPyramid;

    // ピラミッドがどのフレームから生成されたか(不一致なら使わない)
    private int _pyramidFrame;
    private int _bayerPyramidFrame;
    private DisplayLut _lut = DisplayLut.Create(new DisplayParameters());
    private DevelopLuts _developLuts = DevelopLuts.Create(new DevelopParameters());
    private ViewportDisplayMode _displayMode = ViewportDisplayMode.Raw;
    private DisplayLut[]? _segmentLuts;
    private int _segmentWidth;
    private bool _zebraEnabled;
    private ColorImage? _colorImage;
    private IReadOnlyList<DefectPixel>? _defectMarkers;

    private bool _profileMarkerVisible;
    private int _profileX;
    private int _profileY;
    private bool _profileHorizontalActive;

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

    private ViewportInteractionMode _interactionMode = ViewportInteractionMode.Pan;

    // キーボードで動かす画素カーソル(Ctrl+矢印で出現、Escで消える)
    private bool _keyCursorVisible;
    private int _keyCursorX;
    private int _keyCursorY;

    private bool _roiDragging;
    private int _roiStartX;
    private int _roiStartY;
    private RegionOfInterest? _roi;

    // 直近にROI変更を通知した時点でROIが有効だったか。
    // 単クリックでROIを消したときに「解除」を通知すべきか判断するのに使う
    private bool _roiHadValue;

    private OverlayData? _overlay;

    // カラー現像のデモザイク結果。表示範囲が同じ間はLUTだけ適用し直す
    private readonly DemosaicCache _demosaicCache = new();

    // raw値オーバーレイの描画結果。FormattedTextの生成が重いので、
    // 表示範囲(ズーム/原点/サイズ)と元データが変わるまで使い回す
    private Drawing? _overlayDrawing;
    private OverlayData? _overlayDrawingSource;
    private double _overlayDrawingZoom;
    private double _overlayDrawingOriginX;
    private double _overlayDrawingOriginY;
    private double _overlayDrawingWidth;
    private double _overlayDrawingHeight;

    /// <summary>コントロールを生成する。</summary>
    public ImageViewport()
    {
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (QualityRenderIsRedundant())
            {
                return;
            }

            RequestRender(fast: false);
        };
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += OnViewportSizeChanged;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    /// <summary>
    /// コントロールのサイズ変更時、表示中心を保ったまま原点を調整する
    /// (フルスクリーン切替やウィンドウリサイズで画像が寄らないようにする)。
    /// </summary>
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_image is not null && e.PreviousSize.Width > 0 && e.PreviousSize.Height > 0)
        {
            _originX -= (e.NewSize.Width - e.PreviousSize.Width) / (2 * _zoom);
            _originY -= (e.NewSize.Height - e.PreviousSize.Height) / (2 * _zoom);
            ClampOrigin();
        }

        RequestRender(fast: false);
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

    /// <summary>
    /// R/Gr/Gb/B の2x2タイル並置(チャネル分割)で描画しているか。
    /// このときROI・クリック位置などの表示座標はタイル画像の座標で、元画像の座標ではない。
    /// </summary>
    /// <remarks>
    /// 表示モードがチャネル分割でもBayerパターンが「なし」なら描画はRawへ落ちる
    /// (<see cref="SelectSource"/> と同じ条件)。
    /// </remarks>
    public bool IsChannelSplitLayout =>
        _displayMode == ViewportDisplayMode.ChannelSplit
        && _format is { Bayer: not BayerPattern.None };

    /// <summary>
    /// マウス操作モード。設定するとカーソル形状も切り替わる
    /// (右パネルを畳んでいてもモードが分かるようにするため)。
    /// </summary>
    public ViewportInteractionMode InteractionMode
    {
        get => _interactionMode;
        set
        {
            if (_interactionMode == value)
            {
                return;
            }

            _interactionMode = value;
            Cursor = value switch
            {
                ViewportInteractionMode.RoiSelect => Cursors.Cross,
                ViewportInteractionMode.LineProfile => Cursors.Cross,
                ViewportInteractionMode.WhiteBalancePick => Cursors.UpArrow,
                _ => Cursors.Arrow,
            };
        }
    }

    /// <summary>現在のROI(未選択ならnull)。</summary>
    public RegionOfInterest? Roi => _roi;

    /// <summary>現在のズーム率。</summary>
    public double Zoom => _zoom;

    /// <summary>表示原点X(画面左上に写る画像X座標)。</summary>
    public double OriginX => _originX;

    /// <summary>表示原点Y(画面左上に写る画像Y座標)。</summary>
    public double OriginY => _originY;

    /// <summary>
    /// ズームと表示原点を直接設定する(比較モードのペイン間同期用)。
    /// </summary>
    /// <param name="zoom">ズーム倍率。</param>
    /// <param name="originX">表示原点X。</param>
    /// <param name="originY">表示原点Y。</param>
    public void SetViewTransform(double zoom, double originX, double originY)
    {
        // ズームだけ丸めて原点をそのまま使うと表示中心が大きくずれる
        // (解像度差の大きい比較では画像が画面外に出て空表示になる)。
        // 中心を保ったままクランプする
        double centerX = originX + (ActualWidth / (2 * zoom));
        double centerY = originY + (ActualHeight / (2 * zoom));
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _originX = centerX - (ActualWidth / (2 * _zoom));
        _originY = centerY - (ActualHeight / (2 * _zoom));
        RequestRender(fast: true);
        RestartIdleTimer();
    }

    /// <summary>
    /// 他ペインのカーソル位置(ゴーストカーソル)を表示する。
    /// </summary>
    /// <param name="imageX">画像X座標(小数可)。</param>
    /// <param name="imageY">画像Y座標(小数可)。</param>
    public void SetGhostCursor(double imageX, double imageY)
    {
        // 同じ画素を指し続ける間の再描画は無駄(raw値オーバーレイ表示中は特に高価)
        if (_ghostVisible && _ghostX == imageX && _ghostY == imageY)
        {
            return;
        }

        _ghostVisible = true;
        _ghostX = imageX;
        _ghostY = imageY;
        InvalidateVisual();
    }

    /// <summary>ゴーストカーソルを消す。</summary>
    public void ClearGhostCursor()
    {
        if (_ghostVisible)
        {
            _ghostVisible = false;
            InvalidateVisual();
        }
    }

    private bool _ghostVisible;
    private double _ghostX;
    private double _ghostY;

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
        _bayerPyramid = null;
        _overlay = null;
        _demosaicCache.Clear();
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
        _bayerPyramid = null;
        _overlay = null;
        _demosaicCache.Clear();
        _segmentLuts = null;
        _colorImage = null;
        _displayMode = ViewportDisplayMode.Raw;
        _profileMarkerVisible = false;
        ClearRoi();
        FitToView();
    }

    /// <summary>
    /// ROIを数値指定で設定する(キーボード操作・プリセット用)。
    /// </summary>
    /// <param name="roi">設定するROI。画像範囲へクランプされる。</param>
    public void SetRoi(RegionOfInterest roi)
    {
        if (_image is null)
        {
            return;
        }

        RegionOfInterest clamped = roi.Clamp(_image.Width, _image.Height);
        _roi = clamped.PixelCount > 0 ? clamped : null;
        _roiHadValue = _roi is not null;
        InvalidateVisual();
        RoiChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>ROI選択を解除する。</summary>
    public void ClearRoi()
    {
        // _roi が null でも、直前の単クリックで統計だけ残っている場合があるため
        // 「以前ROIがあった」ことも解除通知の条件にする
        bool had = _roi is not null || _roiHadValue;
        _roi = null;
        _roiDragging = false;
        _roiHadValue = false;
        if (had)
        {
            RoiChanged?.Invoke(this, EventArgs.Empty);
        }

        InvalidateVisual();
    }

    /// <summary>生成完了したピラミッドを取り付け、高解像度で再描画する。</summary>
    /// <param name="pyramid">ピラミッド。</param>
    /// <param name="frame">このピラミッドの生成元フレーム番号。</param>
    public void SetPyramid(TilePyramid? pyramid, int frame = 0)
    {
        _pyramid = pyramid;
        _pyramidFrame = frame;
        RequestRender(fast: false);
    }

    /// <summary>
    /// Bayer位相を保った縮小ピラミッドを取り付ける(カラー系表示の縮小描画に使う)。
    /// </summary>
    /// <param name="pyramid">ピラミッド。</param>
    /// <param name="frame">このピラミッドの生成元フレーム番号。</param>
    public void SetBayerPyramid(BayerPyramid? pyramid, int frame = 0)
    {
        _bayerPyramid = pyramid;
        _bayerPyramidFrame = frame;
        RequestRender(fast: false);
    }

    /// <summary>
    /// 進行中の描画を止めてBayerピラミッドを切り離す。
    /// 完了後は取り付けていたピラミッドを安全にDisposeできる
    /// (描画中にDisposeすると読み出しがObjectDisposedExceptionになる)。
    /// </summary>
    public async Task DetachBayerPyramidAsync()
    {
        _renderCts?.Cancel();
        _bayerPyramid = null;
        try
        {
            await _renderTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>現在の表示フレームに対応するピラミッドを持っているか。</summary>
    public bool HasPyramidForCurrentFrame =>
        _pyramid is not null && _pyramidFrame == _frame;

    /// <summary>
    /// 表示フレームを切り替える(ズーム/位置は維持)。
    /// ピラミッドは生成元フレームでのみ有効なため、他フレームは等倍データから描画する。
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
        _demosaicCache.Clear();
        RequestRender(fast: false);
    }

    /// <summary>
    /// 表示画像をズーム/位置を維持したまま差し替える(シーケンス再生用)。
    /// 新しい描画が確定するまで旧ビットマップを表示し続けるためチラつかない。
    /// </summary>
    /// <param name="image">新しい画像。寸法が変わる場合は全体表示に戻す。</param>
    /// <param name="format">フォーマット。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="color">新しい画像のRGBデータ。</param>
    /// <returns>差し替え前の画像。呼び出し側でDisposeすること。</returns>
    public async Task<RawImage?> ReplaceImageAsync(
        RawImage image, RawFormat format, int frame = 0, ColorImage? color = null)
    {
        RawImage? old = _image;
        bool wasChannelSplit = IsChannelSplitLayout;
        _renderCts?.Cancel();
        Task pending = _renderTask;
        _image = image;
        _format = format;
        _frame = frame;
        _colorImage = color;
        _pyramid = null;
        _bayerPyramid = null;
        _overlay = null;
        _demosaicCache.Clear();
        if (old?.Width != image.Width || old?.Height != image.Height)
        {
            _profileMarkerVisible = false;
            ClearRoi();
            FitToView();
        }
        else
        {
            DiscardRoiOnLayoutChange(wasChannelSplit);
            RequestRender(fast: false);
        }
        try
        {
            await pending;
        }
        catch (OperationCanceledException)
        {
        }

        return old;
    }

    /// <summary>
    /// フォーマット記述子のみを差し替えて再描画する(Bayerパターンのその場変更用)。
    /// ズーム/位置は維持される。
    /// </summary>
    /// <param name="format">新しいフォーマット。</param>
    public void UpdateFormat(RawFormat format)
    {
        bool wasChannelSplit = IsChannelSplitLayout;
        _format = format;
        DiscardRoiOnLayoutChange(wasChannelSplit);
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

    /// <summary>表示モードを切り替えて再描画する。</summary>
    /// <param name="mode">表示モード。</param>
    public void SetDisplayMode(ViewportDisplayMode mode)
    {
        bool wasChannelSplit = IsChannelSplitLayout;
        _displayMode = mode;
        _overlay = null;
        _demosaicCache.Clear();
        DiscardRoiOnLayoutChange(wasChannelSplit);
        RequestRender(fast: false);
    }

    /// <summary>
    /// チャネル分割とそれ以外の表示が切り替わったら、ROI選択を捨てる。
    /// </summary>
    /// <remarks>
    /// ROIは表示座標で保持している。タイル座標で選んだ矩形を元画像座標として
    /// (あるいはその逆に)読み替えると、選んだときとは別の画素を指してしまう。
    /// 解除は <see cref="RoiChanged"/> で通知するので、解析側は旧ROIの統計を消せる。
    /// </remarks>
    /// <param name="wasChannelSplit">変更前にチャネル分割で描画していたか。</param>
    private void DiscardRoiOnLayoutChange(bool wasChannelSplit)
    {
        if (wasChannelSplit != IsChannelSplitLayout)
        {
            ClearRoi();
        }
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

    /// <summary>
    /// デコード済みカラー画像を設定する(nullで解除)。
    /// 設定するとTrueColorモードで描画される。
    /// </summary>
    /// <param name="color">カラー画像。</param>
    public void SetColorImage(ColorImage? color)
    {
        bool wasChannelSplit = IsChannelSplitLayout;
        _colorImage = color;
        _displayMode = color is not null ? ViewportDisplayMode.TrueColor : ViewportDisplayMode.Raw;
        _overlay = null;
        _demosaicCache.Clear();
        DiscardRoiOnLayoutChange(wasChannelSplit);
        RequestRender(fast: false);
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
    /// ラインプロファイルの参照位置マーカーを表示する。
    /// アクティブ方向のラインを実線、もう一方を破線で描画する。
    /// </summary>
    /// <param name="x">プロファイル列(垂直ライン)のX座標。</param>
    /// <param name="y">プロファイル行(水平ライン)のY座標。</param>
    /// <param name="horizontalActive">水平プロファイルがアクティブか。</param>
    public void SetProfileMarker(int x, int y, bool horizontalActive)
    {
        _profileMarkerVisible = true;
        _profileX = x;
        _profileY = y;
        _profileHorizontalActive = horizontalActive;
        InvalidateVisual();
    }

    /// <summary>ラインプロファイルの位置マーカーを消す。</summary>
    public void ClearProfileMarker()
    {
        _profileMarkerVisible = false;
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
        DrawProfileMarker(dc);
        DrawKeyboardCursor(dc);
        DrawGhostCursor(dc);
    }

    private static readonly Pen KeyCursorPen = CreateKeyCursorPen(0xE6);
    private static readonly Pen KeyCursorGuidePen = CreateKeyCursorPen(0x50);

    private static Pen CreateKeyCursorPen(byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 0x7E, 0xCB, 0x72));
        brush.Freeze();
        var pen = new Pen(brush, 1.2);
        pen.Freeze();
        return pen;
    }

    private static readonly Pen GhostCursorPen = CreateGhostCursorPen();

    private static Pen CreateGhostCursorPen()
    {
        // キーボードカーソル(緑)と区別できる暖色。控えめな太さで実画素を隠さない
        var brush = new SolidColorBrush(Color.FromArgb(0xC8, 0xE8, 0xA3, 0x4B));
        brush.Freeze();
        var pen = new Pen(brush, 1.2);
        pen.Freeze();
        return pen;
    }

    /// <summary>他ペインのカーソル位置を示すクロスヘアを描く(比較モード用)。</summary>
    private void DrawGhostCursor(DrawingContext dc)
    {
        if (!_ghostVisible || _image is null)
        {
            return;
        }

        double x = (_ghostX + 0.5 - _originX) * _zoom;
        double y = (_ghostY + 0.5 - _originY) * _zoom;
        if (x < -50 || y < -50 || x > ActualWidth + 50 || y > ActualHeight + 50)
        {
            return;
        }

        dc.DrawLine(GhostCursorPen, new Point(x, 0), new Point(x, ActualHeight));
        dc.DrawLine(GhostCursorPen, new Point(0, y), new Point(ActualWidth, y));
    }

    private void DrawKeyboardCursor(DrawingContext dc)
    {
        if (!_keyCursorVisible || _image is null)
        {
            return;
        }

        double left = (_keyCursorX - _originX) * _zoom;
        double top = (_keyCursorY - _originY) * _zoom;
        double size = Math.Max(9, _zoom);

        // 画面外まで伸びるガイド線で、拡大時でも位置を見失わないようにする
        double cx = left + size / 2;
        double cy = top + size / 2;
        dc.DrawLine(KeyCursorGuidePen, new Point(0, cy), new Point(ActualWidth, cy));
        dc.DrawLine(KeyCursorGuidePen, new Point(cx, 0), new Point(cx, ActualHeight));
        dc.DrawRectangle(null, KeyCursorPen, new Rect(left, top, size, size));
    }

    private static readonly Pen ProfileActivePen = CreateProfilePen(dashed: false);
    private static readonly Pen ProfileInactivePen = CreateProfilePen(dashed: true);

    private static Pen CreateProfilePen(bool dashed)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            dashed ? (byte)0x60 : (byte)0xE6, 0xD9, 0x9B, 0x5B));
        brush.Freeze();
        var pen = new Pen(brush, dashed ? 1.0 : 1.4);
        if (dashed)
        {
            pen.DashStyle = new DashStyle(new double[] { 4, 4 }, 0);
        }

        pen.Freeze();
        return pen;
    }

    private void DrawProfileMarker(DrawingContext dc)
    {
        if (!_profileMarkerVisible || _image is null)
        {
            return;
        }

        double screenY = (_profileY + 0.5 - _originY) * _zoom;
        double screenX = (_profileX + 0.5 - _originX) * _zoom;
        Pen horizontalPen = _profileHorizontalActive ? ProfileActivePen : ProfileInactivePen;
        Pen verticalPen = _profileHorizontalActive ? ProfileInactivePen : ProfileActivePen;
        if (screenY >= 0 && screenY <= ActualHeight)
        {
            dc.DrawLine(horizontalPen, new Point(0, screenY), new Point(ActualWidth, screenY));
        }

        if (screenX >= 0 && screenX <= ActualWidth)
        {
            dc.DrawLine(verticalPen, new Point(screenX, 0), new Point(screenX, ActualHeight));
        }
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

            // 単クリック(カーソル未移動)ではOnMouseMoveが発生しないため、
            // ここで旧ROIの消去を確定させないと矩形と統計が残り続ける
            _roiHadValue = _roi is { PixelCount: > 0 };
            _roi = null;
            InvalidateVisual();
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
        EndDrag(notifyRoi: true);
    }

    /// <inheritdoc />
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // モーダル表示・Alt+Tab・UAC等でキャプチャを失っても
        // ボタンを離した扱いにする(でないとボタンを離した後もパン/ROI伸縮が続く)
        EndDrag(notifyRoi: true);
    }

    private void EndDrag(bool notifyRoi)
    {
        if (_panning)
        {
            _panning = false;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }

            RestartIdleTimer();
        }

        if (!_roiDragging)
        {
            return;
        }

        _roiDragging = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        // ROIが消えた場合も通知しないと、枠だけ消えて旧統計が残る
        bool hasRoi = _roi is { PixelCount: > 0 };
        if (notifyRoi && (hasRoi || _roiHadValue))
        {
            RoiChanged?.Invoke(this, EventArgs.Empty);
        }

        _roiHadValue = hasRoi;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_image is null)
        {
            return;
        }

        // キャプチャを失った状態でのドラッグ継続を防ぐ
        if ((_panning || _roiDragging) && e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag(notifyRoi: true);
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

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _image is null)
        {
            return;
        }

        if (e.Key == Key.Escape && _keyCursorVisible)
        {
            _keyCursorVisible = false;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        (int dx, int dy) = e.Key switch
        {
            Key.Left => (-1, 0),
            Key.Right => (1, 0),
            Key.Up => (0, -1),
            Key.Down => (0, 1),
            _ => (0, 0),
        };
        if (dx == 0 && dy == 0)
        {
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        e.Handled = true;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl系は画素カーソル。Shiftで10画素、AltでBayer同色(2画素)刻み
            int step = modifiers.HasFlag(ModifierKeys.Shift) ? 10
                : modifiers.HasFlag(ModifierKeys.Alt) ? 2
                : 1;
            MoveKeyboardCursor(dx * step, dy * step);
            return;
        }

        // Shiftなしは1/8画面、Shiftありは1画面ぶんパンする
        double fraction = modifiers.HasFlag(ModifierKeys.Shift) ? 1.0 : 0.125;
        _originX += dx * ActualWidth * fraction / _zoom;
        _originY += dy * ActualHeight * fraction / _zoom;
        ClampOrigin();
        RequestRender(fast: true);
        RestartIdleTimer();
    }

    /// <summary>
    /// 画素カーソルを移動し、必要なら見える位置までスクロールする。
    /// </summary>
    private void MoveKeyboardCursor(int dx, int dy)
    {
        if (_image is null)
        {
            return;
        }

        if (!_keyCursorVisible)
        {
            // 初回はビュー中央から開始する
            _keyCursorX = Math.Clamp(
                (int)(_originX + ActualWidth / (2 * _zoom)), 0, _image.Width - 1);
            _keyCursorY = Math.Clamp(
                (int)(_originY + ActualHeight / (2 * _zoom)), 0, _image.Height - 1);
            _keyCursorVisible = true;
        }
        else
        {
            _keyCursorX = Math.Clamp(_keyCursorX + dx, 0, _image.Width - 1);
            _keyCursorY = Math.Clamp(_keyCursorY + dy, 0, _image.Height - 1);
        }

        EnsureKeyboardCursorVisible();
        InvalidateVisual();
        CursorPixelChanged?.Invoke(this, new CursorPixelEventArgs
        {
            X = _keyCursorX,
            Y = _keyCursorY,
            IsInsideImage = true,
        });
    }

    private void EnsureKeyboardCursorVisible()
    {
        double marginX = Math.Min(ActualWidth / (4 * _zoom), _image!.Width / 2.0);
        double marginY = Math.Min(ActualHeight / (4 * _zoom), _image.Height / 2.0);
        double viewW = ActualWidth / _zoom;
        double viewH = ActualHeight / _zoom;
        bool moved = false;

        if (_keyCursorX < _originX + marginX)
        {
            _originX = _keyCursorX - marginX;
            moved = true;
        }
        else if (_keyCursorX > _originX + viewW - marginX)
        {
            _originX = _keyCursorX - viewW + marginX;
            moved = true;
        }

        if (_keyCursorY < _originY + marginY)
        {
            _originY = _keyCursorY - marginY;
            moved = true;
        }
        else if (_keyCursorY > _originY + viewH - marginY)
        {
            _originY = _keyCursorY - viewH + marginY;
            moved = true;
        }

        if (moved)
        {
            ClampOrigin();
            RequestRender(fast: true);
            RestartIdleTimer();
        }
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
        _originX = ClampRange(_originX, -viewW + margin, _image.Width - margin);
        _originY = ClampRange(_originY, -viewH + margin, _image.Height - margin);
    }

    /// <summary>
    /// min &gt; max(ビューが極端に潰れて余白がとれない場合)でも
    /// Math.Clamp の ArgumentException を出さずに中央へ寄せる。
    /// </summary>
    private static double ClampRange(double value, double min, double max)
    {
        return min > max ? (min + max) / 2 : Math.Clamp(value, min, max);
    }

    private void RestartIdleTimer()
    {
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    // 直近に表示した内容の素性(品質パスの要否判定に使う)
    private bool _overlayEvaluated;
    private double _presentedZoom = double.NaN;
    private double _presentedOriginX = double.NaN;
    private double _presentedOriginY = double.NaN;
    private int _presentedWidth;
    private int _presentedHeight;

    /// <summary>
    /// アイドル後の品質パスが、いま表示している内容と同じ結果にしかならないか判定する。
    /// </summary>
    /// <remarks>
    /// 速報パスと品質パスの違いは「1段細かい縮小レベルを使う」ことと
    /// 「raw値オーバーレイを取得する」ことだけ。等倍以上・ピラミッド未生成・ゼブラなど
    /// 縮小レベルを使わない状況では両者の出力は完全に一致するため、
    /// そのまま描き直すと同じ絵をもう一度作るだけになる(大画像では1回が数百ms)。
    /// </remarks>
    /// <returns>描き直す必要がなければtrue。</returns>
    private bool QualityRenderIsRedundant()
    {
        if (_image is null || _bitmap is null)
        {
            return false;
        }

        // 表示中の内容が現在のビュー状態のものでなければ描き直す
        if (_presentedZoom != _zoom
            || _presentedOriginX != _originX
            || _presentedOriginY != _originY
            || _presentedWidth != Math.Max(1, (int)Math.Round(ActualWidth))
            || _presentedHeight != Math.Max(1, (int)Math.Round(ActualHeight)))
        {
            return false;
        }

        // オーバーレイは品質パスでしか取得しない
        if (!_overlayEvaluated
            && _zoom >= RawOverlayMinZoom
            && _displayMode != ViewportDisplayMode.ChannelSplit)
        {
            return false;
        }

        return SelectSource(fast: false) is { } quality
            && quality.Source.Factor * quality.CoordinateFactor == _renderedFactor;
    }

    /// <summary>
    /// 選択された画素供給元と座標倍率。
    /// </summary>
    /// <param name="Source">画素供給元。</param>
    /// <param name="CoordinateFactor">
    /// ソースの座標系が元画像の何分の1か。1以外のときは呼び出し側で
    /// zoom を掛け、origin を割ってから描画する(グレー系ピラミッドは
    /// RenderSource.Factor で内部処理するため常に1)。
    /// </param>
    private readonly record struct SelectedSource(RenderSource Source, int CoordinateFactor);

    private SelectedSource? SelectSource(bool fast)
    {
        if (_image is null)
        {
            return null;
        }

        if (_displayMode == ViewportDisplayMode.TrueColor && _colorImage is not null)
        {
            // カラー画像は色を保つため常に等倍データから描画する
            return new SelectedSource(new RawImageRenderSource(_image, _frame), 1);
        }

        bool bayerMode = _displayMode is ViewportDisplayMode.ChannelSplit
                or ViewportDisplayMode.BayerColor or ViewportDisplayMode.ColorDevelop
            && _format?.Bayer != BayerPattern.None;
        if (bayerMode)
        {
            // Bayer位相を保った縮小レベルは「小さなRaw画像」なので、
            // カラー描画経路をそのまま等倍として使える(座標だけ倍率で補正する)
            RawImage colorImage = _image;
            int colorFrame = _frame;
            int coordinateFactor = 1;
            BayerPyramid? bayer = _bayerPyramidFrame == _frame ? _bayerPyramid : null;
            if (bayer is not null && !_zebraEnabled)
            {
                int factor = SelectFactorForRender(bayer.SelectFactor(_zoom), fast, bayer.GetLevel);
                if (factor > 1 && bayer.GetLevel(factor) is { } level)
                {
                    colorImage = level;
                    colorFrame = 0;
                    coordinateFactor = factor;
                }
            }

            RenderSource source = _displayMode == ViewportDisplayMode.ChannelSplit
                ? new ChannelSplitRenderSource(colorImage, colorFrame)
                : new RawImageRenderSource(colorImage, colorFrame);
            return new SelectedSource(source, coordinateFactor);
        }

        // ゼブラ判定は実画素値に対して行う必要がある。
        // 縮小レベルは2x2〜16x16平均なので飽和画素が薄まり偽陰性になる
        if (_zebraEnabled)
        {
            return new SelectedSource(new RawImageRenderSource(_image, _frame), 1);
        }

        // ピラミッドは生成元フレームのデータなので、他フレームでは使わない
        TilePyramid? pyramid = _pyramidFrame == _frame ? _pyramid : null;
        int grayFactor = pyramid is null
            ? 1
            : SelectFactorForRender(
                pyramid.SelectFactor(_zoom), fast, f => pyramid.GetLevel(f));

        if (grayFactor <= 1)
        {
            return new SelectedSource(new RawImageRenderSource(_image, _frame), 1);
        }

        PyramidLevel grayLevel = pyramid!.GetLevel(grayFactor)!;
        return new SelectedSource(
            new PyramidLevelRenderSource(grayLevel, _image.Width, _image.Height), 1);
    }

    /// <summary>
    /// 操作中(fast)は1段粗いレベルへ落とす。ただし等倍以上では
    /// 読み出し画素数が元々少なく利点がないうえ、平均がブロックとして見えるため落とさない。
    /// </summary>
    private static int SelectFactorForRender<T>(int factor, bool fast, Func<int, T?> getLevel)
        where T : class
    {
        if (!fast || factor <= 1)
        {
            return factor;
        }

        return getLevel(factor * 2) is not null ? factor * 2 : factor;
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

        SelectedSource? selected = SelectSource(fast);
        if (selected is not { } chosen)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _renderCts = cts;
        int destW = Math.Max(1, (int)Math.Round(ActualWidth));
        int destH = Math.Max(1, (int)Math.Round(ActualHeight));

        // 縮小レベルを等倍ソースとして使う場合、画面座標→ソース座標の
        // 対応が保たれるよう zoom と origin を倍率で補正する
        int coordinateFactor = chosen.CoordinateFactor;
        double zoom = _zoom * coordinateFactor;
        double originX = _originX / coordinateFactor;
        double originY = _originY / coordinateFactor;

        // 表示側へ返すのは元画像基準のズームと実効縮小率
        double displayZoom = _zoom;
        int effectiveFactor = chosen.Source.Factor * coordinateFactor;
        var request = new RenderRequest
        {
            Source = chosen.Source,
            Lut = _lut,
            Mode = _displayMode,
            Pattern = _format?.Bayer ?? BayerPattern.None,
            DevelopLuts = _developLuts,
            SegmentLuts = _segmentLuts,
            SegmentWidth = Math.Max(1, _segmentWidth / coordinateFactor),
            ZebraEnabled = _zebraEnabled,
            Color = _colorImage,
            DemosaicCache = _demosaicCache,
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

                    Present(buffer, destW, destH, effectiveFactor, displayZoom, fast);
                });
            }
            catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
            {
                // キャンセル、または画像/ピラミッドの差し替え中に破棄と競合した読み出し。
                // 破棄後の描画結果は不要なのでキャンセル扱いにする
                // (伝播させると後続のClear/ReplaceのawaitでUIまで届いてしまう)
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

        // 表示中の内容の素性。アイドル後の品質パスが同じ結果にしかならないなら省く
        _overlayEvaluated = !fast;
        _presentedZoom = _zoom;
        _presentedOriginX = _originX;
        _presentedOriginY = _originY;
        _presentedWidth = width;
        _presentedHeight = height;
        InvalidateVisual();
        ViewportStateChanged?.Invoke(this, new ViewportStateEventArgs
        {
            Zoom = zoom,
            RenderedFactor = factor,
        });
    }

    private static readonly Typeface OverlayTypeface = new("Consolas");

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

        // ROIドラッグやゴーストカーソル移動でもOnRenderは走るが、
        // そのときオーバーレイの内容は変わらないので作り直さない
        if (_overlayDrawing is null
            || !ReferenceEquals(_overlayDrawingSource, _overlay)
            || _overlayDrawingZoom != _zoom
            || _overlayDrawingOriginX != _originX
            || _overlayDrawingOriginY != _originY
            || _overlayDrawingWidth != ActualWidth
            || _overlayDrawingHeight != ActualHeight)
        {
            _overlayDrawing = BuildRawValueOverlay(_overlay);
            _overlayDrawingSource = _overlay;
            _overlayDrawingZoom = _zoom;
            _overlayDrawingOriginX = _originX;
            _overlayDrawingOriginY = _originY;
            _overlayDrawingWidth = ActualWidth;
            _overlayDrawingHeight = ActualHeight;
        }

        dc.DrawDrawing(_overlayDrawing);
    }

    private Drawing BuildRawValueOverlay(OverlayData ov)
    {
        var group = new DrawingGroup();

        // 描画内容は DrawingContext を閉じたときに group へ書き込まれる。
        // using 宣言だと閉じるのがメソッド末尾(Freeze の後)になり、凍結済みの
        // group への書き込みで InvalidOperationException になるため、ブロックで閉じてから凍結する
        using (DrawingContext dc = group.Open())
        {
            int shift = 16 - _format!.BitDepth;
            double fontSize = Math.Clamp(_zoom / 4.5, 9, 15);
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
                        OverlayTypeface,
                        fontSize,
                        brush,
                        pixelsPerDip);
                    dc.DrawText(text, new Point(
                        left + (_zoom - text.Width) / 2,
                        top + (_zoom - text.Height) / 2));
                }
            }
        }

        group.Freeze();
        return group;
    }
}
