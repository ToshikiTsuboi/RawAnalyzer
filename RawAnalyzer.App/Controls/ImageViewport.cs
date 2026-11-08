using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Controls;

/// <summary>ビューポート状態(ズーム率・使用ピラミッドレベル)の通知引数。</summary>
public sealed class ViewportStateEventArgs : EventArgs
{
    /// <summary>ズーム率(表示 DIP / 元画像画素。<see cref="ImageViewport.Zoom"/> と同じ単位)。</summary>
    public double Zoom { get; init; }

    /// <summary>
    /// デバイス基準のズーム率(画面のデバイス画素 / 元画像画素。表示倍率 100% では <see cref="Zoom"/> と同じ)。
    /// 倍率の表示はこちらで行う(等倍で 100%)。
    /// </summary>
    public double DeviceZoom { get; init; }

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
public sealed partial class ImageViewport : FrameworkElement
{
    private const double MinZoom = 1.0 / 512;
    private const double MaxZoom = 128;
    private const double ZoomStep = 1.25;
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

    // 欠陥マーカーの描画結果。表示範囲とマーカーが変わるまで使い回す(DrawDefectMarkers)
    private Drawing? _defectDrawing;
    private DefectDrawingKey _defectDrawingKey;

    private bool _profileMarkerVisible;
    private int _profileX;
    private int _profileY;
    private bool _profileHorizontalActive;

    private double _zoom = 1.0;
    private double _originX;
    private double _originY;

    private WriteableBitmap? _bitmap;

    // 表示中のビットマップを描いたときの表示倍率(ビットマップはデバイス解像度で描く)
    private double _presentedScale = 1.0;

    // 表示倍率。ウィンドウに載っている間は実際の DPI から読み、載っていない間は最後に読んだ値を使う
    private double _connectedScale = 1.0;
    private double? _deviceScaleOverride;

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

    // パン・ROI のドラッグ中のマウスキャプチャ。既定はこのビューポート自身の(OS の)キャプチャ(OverrideMouseCapture)
    private IMouseCapture _mouseCapture;

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
        _mouseCapture = new OwnMouseCapture(this);
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

    /// <summary>
    /// チャネル分割とそれ以外の表示が切り替わったときに発火する(表示座標の意味が変わるので ROI を捨てた後)。
    /// ROI がなくても発火する(射影の窓は画像全体と象限の案内を切り替え、抜けたら選んだチャネルを忘れる)。
    /// 画像を差し替えて Raw 表示へ戻す <see cref="SetImage"/> で分割表示から抜けたときも発火する。
    /// </summary>
    public event EventHandler? ChannelSplitLayoutChanged;

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
    /// 元画像の画素座標を、いまの表示での座標(ROI・クリック位置と同じ表示座標)へ写す。
    /// </summary>
    /// <remarks>
    /// チャネル分割表示では、その画素が並ぶ象限上の位置(描画と同じ規約の
    /// <see cref="BayerSplit.TryMapSourceToTiled"/>)。それ以外の表示は表示座標=元画像座標。
    /// </remarks>
    /// <param name="sourceX">元画像X座標。</param>
    /// <param name="sourceY">元画像Y座標。</param>
    /// <param name="displayX">表示X座標。</param>
    /// <param name="displayY">表示Y座標。</param>
    /// <returns>
    /// 表示に並んでいる画素ならtrue。チャネル分割表示では、奇数寸法の画像の最終列・最終行の
    /// 画素はどの象限にも並ばないためfalse。
    /// </returns>
    public bool TryMapSourceToDisplay(int sourceX, int sourceY, out int displayX, out int displayY)
    {
        if (!IsChannelSplitLayout || _image is null)
        {
            displayX = sourceX;
            displayY = sourceY;
            return true;
        }

        return BayerSplit.TryMapSourceToTiled(
            sourceX, sourceY, _image.Width & ~1, _image.Height & ~1, out displayX, out displayY);
    }

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

    /// <summary>現在のズーム率(表示 DIP / 元画像画素)。</summary>
    public double Zoom => _zoom;

    /// <summary>
    /// raw 値オーバーレイが出る最小のズーム率(DIP 基準。元画像 1 画素が 32 デバイス画素になる 32/表示倍率)。
    /// 拡大して画素値を見せる操作は <see cref="CenterOn"/> にこれ以上のズームを渡す。
    /// </summary>
    public double RawOverlayZoom => DeviceScaling.RawOverlayZoom(DeviceScale);

    /// <summary>
    /// 表示倍率(1 DIP あたりのデバイス画素数。Windows の表示スケールが 125% なら 1.25)。
    /// 描画はこの倍率を掛けたデバイス解像度で行う。
    /// </summary>
    /// <remarks>
    /// ウィンドウに載っていない間(レイアウトだけ通したテストなど)は、最後に載っていたときの倍率
    /// (一度も載っていなければ 1)。画面に出ないので倍率は意味を持たず、描画結果を実行環境の DPI に左右させない。
    /// </remarks>
    public double DeviceScale
    {
        get
        {
            if (_deviceScaleOverride is { } forced)
            {
                return forced;
            }

            if (PresentationSource.FromVisual(this) is not null)
            {
                _connectedScale = DeviceScaling.Normalize(VisualTreeHelper.GetDpi(this).DpiScaleX);
            }

            return _connectedScale;
        }
    }

    /// <summary>
    /// 表示倍率を固定する(実際の DPI を変えられないテスト用。null で実際の DPI に戻す)。
    /// DPI が変わったときと同じく描き直す。
    /// </summary>
    /// <param name="scale">表示倍率。</param>
    internal void OverrideDeviceScale(double? scale)
    {
        double old = DeviceScale;
        _deviceScaleOverride = scale is { } value ? DeviceScaling.Normalize(value) : null;
        OnDeviceScaleChanged(old, DeviceScale);
    }

    /// <inheritdoc />
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _connectedScale = DeviceScaling.Normalize(newDpi.DpiScaleX);
        if (_deviceScaleOverride is null)
        {
            OnDeviceScaleChanged(DeviceScaling.Normalize(oldDpi.DpiScaleX), _connectedScale);
        }
    }

    /// <summary>
    /// 表示倍率が変わったら(別の倍率のモニタへ移した・表示スケールを変えた)、新しいデバイス解像度で描き直す。
    /// 等倍・整数倍の拡大はデバイス基準のズームを保つ(<see cref="DeviceScaling.ZoomAfterScaleChange"/>)。
    /// </summary>
    private void OnDeviceScaleChanged(double oldScale, double newScale)
    {
        if (oldScale == newScale)
        {
            return;
        }

        double zoom = Math.Clamp(
            DeviceScaling.ZoomAfterScaleChange(_zoom, oldScale, newScale), MinZoom, MaxZoom);
        if (_image is not null && zoom != _zoom)
        {
            // 表示中心は保つ
            double centerX = _originX + ActualWidth / (2 * _zoom);
            double centerY = _originY + ActualHeight / (2 * _zoom);
            _zoom = zoom;
            _originX = centerX - ActualWidth / (2 * _zoom);
            _originY = centerY - ActualHeight / (2 * _zoom);
            ClampOrigin();
        }

        // raw 値オーバーレイの文字は倍率(PixelsPerDip)に合わせて作り直す
        _overlayDrawing = null;
        RequestRender(fast: false);
    }

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
        bool wasChannelSplit = IsChannelSplitLayout;
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

        // Raw 表示へ戻すので、チャネル分割表示からは抜ける(HDR 表示へ入る・別のファイルを開くなど)
        if (wasChannelSplit)
        {
            ChannelSplitLayoutChanged?.Invoke(this, EventArgs.Empty);
        }
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
        bool wasDragging = _roiDragging;
        _roi = null;
        _roiDragging = false;
        _roiHadValue = false;

        // ドラッグ中に消されたら(画像の差し替え・分割⇔非分割の切り替え・Ctrl+G)ドラッグごと中止する。
        // キャプチャを残すと、ボタンを離しても EndDrag はドラッグ中でないので外さず、
        // ビューポートの外のマウス移動と次のクリックまでビューポートに届いてしまう
        if (wasDragging && !_panning && _mouseCapture.IsCaptured)
        {
            _mouseCapture.Release();
        }

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
    /// <remarks>
    /// 切り離しは呼び出したUIターンで済み、返されたタスクは呼び出した時点で進行中だった描画の停止だけを待つ。
    /// 止めた描画は描き直さないので、同じUIターンで続けて <see cref="SetFrame"/> などで描画を始める
    /// (その描画は切り離したピラミッドを使わず、待ちも止めもしない)。フレーム送りは送りを決めたUIターンで
    /// 切り離しとフレームの切り替えを行い、それから停止を待って破棄する。
    /// </remarks>
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
            ChannelSplitLayoutChanged?.Invoke(this, EventArgs.Empty);
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
        _defectDrawing = null;
        InvalidateVisual();
    }

    /// <summary>
    /// ラインプロファイルの参照位置マーカーを表示する。
    /// アクティブ方向のラインを実線、もう一方を破線で描画する。
    /// </summary>
    /// <remarks>
    /// 位置はプロファイルと同じ元画像の列・行で受け取り、描画時にいまの表示でその列・行が
    /// 並ぶ位置へ写す。表示座標で持つと、チャネル分割とそれ以外を切り替えたときに
    /// プロファイルとは別の行・列の上に残ってしまう。
    /// </remarks>
    /// <param name="x">プロファイル列(垂直ライン)の元画像X座標。</param>
    /// <param name="y">プロファイル行(水平ライン)の元画像Y座標。</param>
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
    /// 座標は表示座標(ROI・クリック位置と同じ)。元画像の画素なら <see cref="CenterOnSourcePixel"/>。
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
    /// 元画像の画素がビュー中央に来るようにズーム・位置を設定する。
    /// チャネル分割表示では、その画素が並ぶ象限上の位置を中央に置く。
    /// </summary>
    /// <param name="x">元画像X座標。</param>
    /// <param name="y">元画像Y座標。</param>
    /// <param name="zoom">ズーム率。</param>
    /// <returns>
    /// その画素が表示に並んでいればtrue。並ばない画素(チャネル分割表示での奇数寸法の
    /// 最終列・最終行)はfalseで、表示は変えない。
    /// </returns>
    public bool CenterOnSourcePixel(int x, int y, double zoom)
    {
        if (!TryMapSourceToDisplay(x, y, out int displayX, out int displayY))
        {
            return false;
        }

        CenterOn(displayX, displayY, zoom);
        return true;
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

    /// <summary>
    /// 等倍(元画像の 1 画素 = 画面の 1 デバイス画素)表示にする。ズームは DIP 基準なので 1/表示倍率 になる
    /// (表示倍率 100% 以外で 1 画素 = 1 DIP にすると、最近傍の非整数倍の拡大で偽の周期的な縞が出る)。
    /// </summary>
    public void ActualSize()
    {
        ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), DeviceScaling.ActualSizeZoom(DeviceScale));
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

    /// <summary>
    /// いまの表示(画像と、ROI・マーカー・画素値などの重ね描き)を、画面と同じデバイス解像度の
    /// ビットマップにする(表示のコピー用)。
    /// </summary>
    /// <remarks>
    /// 画像はデバイス解像度で描いているので、96dpi の DIP の大きさで作ると最近傍で表示倍率ぶん間引かれ、
    /// 等倍の表示でも元画像の画素が不規則に抜ける。表示倍率 100% では従来どおり DIP の大きさ(切り捨て)の 96dpi。
    /// </remarks>
    /// <returns>表示のビットマップ。</returns>
    /// <exception cref="ArgumentException">ビューポートの大きさが 1 デバイス画素に満たない場合。</exception>
    public RenderTargetBitmap CaptureView()
    {
        double scale = DeviceScale;
        double dpi = DeviceScaling.BitmapDpi(scale);
        var bitmap = new RenderTargetBitmap(
            (int)(ActualWidth * scale), (int)(ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(this);
        return bitmap;
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
}
