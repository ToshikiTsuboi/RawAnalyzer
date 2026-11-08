using System.Buffers;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Controls;

public sealed partial class ImageViewport
{
    private void RestartIdleTimer()
    {
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    // 最新の描画要求が速報(fast)で、その結果がまだ表示されていないか(品質パスの要否判定に使う)
    private bool _fastRenderPending;

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

        // 速報の描画がまだ表示されていなければ(描画が 200ms を超えた)、表示中の内容は前の描画のもので、
        // この後に速報の結果(粗い縮小レベル・オーバーレイなし)で置き換わる。表示中の内容では判断できない
        if (_fastRenderPending)
        {
            return false;
        }

        // 表示中の内容が現在のビュー状態のものでなければ描き直す
        double scale = DeviceScale;
        if (_presentedZoom != _zoom
            || _presentedOriginX != _originX
            || _presentedOriginY != _originY
            || _presentedScale != scale
            || _presentedWidth != DeviceScaling.DevicePixels(ActualWidth, scale)
            || _presentedHeight != DeviceScaling.DevicePixels(ActualHeight, scale))
        {
            return false;
        }

        // オーバーレイは品質パスでしか取得しない(分割表示か否かは描画と同じ判定。
        // 表示モードがチャネル分割でも Bayer が「なし」なら Raw として描き、オーバーレイも出す)
        if (!_overlayEvaluated
            && DeviceScaling.ShowsRawOverlay(_zoom, scale)
            && !IsChannelSplitLayout)
        {
            return false;
        }

        return SelectSource(fast: false, scale) is { } quality
            && quality.Source.Factor * quality.CoordinateFactor == _renderedFactor;
    }

    /// <summary>
    /// 選択された画素供給元と座標倍率。
    /// </summary>
    /// <param name="Source">画素供給元。</param>
    /// <param name="CoordinateFactor">
    /// ソースの座標系が元画像の何分の1か。1以外のときは呼び出し側で
    /// zoom を掛け、origin を割ってから描画する(グレー系ピラミッドとチャネル分割は
    /// RenderSource.Factor で内部処理するため常に1)。
    /// </param>
    private readonly record struct SelectedSource(RenderSource Source, int CoordinateFactor);

    /// <summary>描画に使う画素供給元(元画像か縮小レベル)を選ぶ。</summary>
    /// <param name="fast">操作中の速報か。</param>
    /// <param name="scale">表示倍率(縮小レベルはデバイス画素に見合うものを選ぶ)。</param>
    private SelectedSource? SelectSource(bool fast, double scale)
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

            // ゼブラの判定は実画素値で行う(縮小レベルの平均では飽和画素が薄まる)。ゼブラを描くのは
            // グレー系のチャネル分割だけで、Bayerカラー・カラー現像は描かないので縮小レベルを使い続ける
            bool zebraDrawn = _zebraEnabled && _displayMode == ViewportDisplayMode.ChannelSplit;
            if (bayer is not null && !zebraDrawn)
            {
                int factor = DeviceScaling.SelectRenderFactor(
                    bayer.SelectFactor, f => bayer.GetLevel(f) is not null, _zoom, scale, fast);
                if (factor > 1 && bayer.GetLevel(factor) is { } level)
                {
                    colorImage = level;
                    colorFrame = 0;
                    coordinateFactor = factor;
                }
            }

            if (_displayMode == ViewportDisplayMode.ChannelSplit)
            {
                // 分割表示は等倍のタイル座標のまま描き、縮小は象限ごとにソース側で行う。
                // 縮小レベルのタイルを一様に並べると、象限の幅・高さが縮小率で割り切れない
                // とき右・下の象限が等倍の境目より手前から始まり、ROIの象限判定と食い違う
                return new SelectedSource(
                    new ChannelSplitRenderSource(
                        colorImage, colorFrame, coordinateFactor,
                        _image.Width & ~1, _image.Height & ~1),
                    1);
            }

            // 縮小レベルは元画像の幅・高さを 2×縮小率 で割った端数を切り捨てているので、
            // 端数を最終列・最終行で埋めて元画像の範囲ちょうどまで描くソースで包む
            RenderSource source = coordinateFactor > 1
                ? new BayerLevelRenderSource(colorImage, coordinateFactor, _image.Width, _image.Height)
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
            : DeviceScaling.SelectRenderFactor(
                pyramid.SelectFactor, f => pyramid.GetLevel(f) is not null, _zoom, scale, fast);

        if (grayFactor <= 1)
        {
            return new SelectedSource(new RawImageRenderSource(_image, _frame), 1);
        }

        PyramidLevel grayLevel = pyramid!.GetLevel(grayFactor)!;
        return new SelectedSource(
            new PyramidLevelRenderSource(grayLevel, _image.Width, _image.Height), 1);
    }

    private void RequestRender(bool fast)
    {
        _renderCts?.Cancel();
        _fastRenderPending = false;
        if (_image is null || ActualWidth < 1 || ActualHeight < 1)
        {
            _bitmap = null;
            InvalidateVisual();
            return;
        }

        double scale = DeviceScale;
        SelectedSource? selected = SelectSource(fast, scale);
        if (selected is not { } chosen)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _renderCts = cts;
        _fastRenderPending = fast;

        // デバイス解像度で描く(DIP の大きさで描くと、表示倍率 100% 以外では最近傍で非整数倍に
        // 引き伸ばされ、等倍でも元画像の画素が 1,1,1,2 デバイス画素のように周期的に幅を変えて縞に見える)
        int destW = DeviceScaling.DevicePixels(ActualWidth, scale);
        int destH = DeviceScaling.DevicePixels(ActualHeight, scale);

        // 縮小レベルを等倍ソースとして使う場合、画面座標→ソース座標の
        // 対応が保たれるよう zoom と origin を倍率で補正する
        int coordinateFactor = chosen.CoordinateFactor;
        double zoom = DeviceScaling.ToDeviceZoom(_zoom, scale) * coordinateFactor;
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
            ZebraBitDepth = _colorImage is { } shownColor && _displayMode == ViewportDisplayMode.TrueColor
                ? shownColor.BitDepth : _format?.BitDepth ?? 16,
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

                    Present(buffer, destW, destH, scale, effectiveFactor, displayZoom, fast);
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

    private void Present(
        byte[] buffer, int width, int height, double scale, int factor, double zoom, bool fast)
    {
        // 96×倍率 dpi にすると、ビットマップの DIP の大きさがビューポートと同じになる
        double dpi = DeviceScaling.BitmapDpi(scale);
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height
            || _bitmap.DpiX != dpi)
        {
            _bitmap = new WriteableBitmap(width, height, dpi, dpi, PixelFormats.Bgra32, null);
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, width * 4, 0);
        _presentedScale = scale;
        _renderedFactor = factor;

        // 表示するのは最新の描画要求の結果だけ(古い要求は取り消され、表示前に捨てられる)
        _fastRenderPending = false;
        _overlay = !fast && DeviceScaling.ShowsRawOverlay(zoom, scale)
            && !IsChannelSplitLayout
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
            DeviceZoom = DeviceScaling.ToDeviceZoom(zoom, scale),
            RenderedFactor = factor,
        });
    }
}
