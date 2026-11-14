using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using RawAnalyzer.App.Themes;

namespace RawAnalyzer.App.Controls;

public sealed partial class ImageViewport
{
    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(CanvasBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_bitmap is not null)
        {
            // ビットマップはデバイス解像度で描いてあるので、DIP の大きさ(画素数/倍率)で置けば 1 画素が
            // 1 デバイス画素に写る。左上はデバイス画素の境目へそろえる(ビューポートの位置が画素の途中だと、
            // 最近傍のサンプル位置が画素の境目に乗って写り方が揺れうる)
            dc.PushGuidelineSet(BitmapGuidelines);
            dc.DrawImage(_bitmap, new Rect(
                0, 0, _bitmap.PixelWidth / _presentedScale, _bitmap.PixelHeight / _presentedScale));
            dc.Pop();
        }

        DrawRawValueOverlay(dc);
        DrawRoi(dc);
        DrawDefectMarkers(dc);
        DrawProfileMarker(dc);
        DrawKeyboardCursor(dc);
        DrawGhostCursor(dc);
        DrawSplitLabels(dc);
    }

    private static readonly Brush SplitLabelBackground = ThemeColors.Brush(ThemeColors.SplitLabelBackground);
    private static readonly Brush SplitLabelRed = ThemeColors.Brush(ThemeColors.ChannelR);
    private static readonly Brush SplitLabelGr = ThemeColors.Brush(ThemeColors.ChannelGr);
    private static readonly Brush SplitLabelGb = ThemeColors.Brush(ThemeColors.ChannelGb);
    private static readonly Brush SplitLabelBlue = ThemeColors.Brush(ThemeColors.ChannelB);
    private static readonly Typeface SplitLabelTypeface = new(new FontFamily("Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private readonly Dictionary<string, FormattedText> _splitLabelText = new();
    private double _splitLabelScale;

    private void DrawSplitLabels(DrawingContext dc)
    {
        if (_bitmap is null || _image is null)
        {
            return;
        }

        if (IsChannelSplitLayout && _format is { } format)
        {
            // ChannelSplitRenderSource の象限は、元画像の行列の偶奇順。奇数寸法の端は描かない。
            int width = _image.Width / 2;
            int height = _image.Height / 2;
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 2; x++)
                {
                    BayerChannel channel = BayerHelper.GetChannel(format.Bayer, x, y);
                    Brush color = channel switch
                    {
                        BayerChannel.R => SplitLabelRed,
                        BayerChannel.Gr => SplitLabelGr,
                        BayerChannel.Gb => SplitLabelGb,
                        _ => SplitLabelBlue,
                    };
                    DrawSplitLabel(dc, new Rect(x * width, y * height, width, height),
                        BayerHelper.GetLabel(channel), color);
                }
            }
        }
        else if (_segmentLuts is { Length: > 1 } luts && _segmentWidth > 0)
        {
            for (int stage = 0; stage < luts.Length; stage++)
            {
                double left = (double)stage * _segmentWidth;
                double width = Math.Clamp(_image.Width - left, 0, _segmentWidth);
                DrawSplitLabel(dc, new Rect(left, 0, width, _image.Height),
                    HdrFormatText.StageName(stage, luts.Length), Brushes.White);
            }
        }
    }

    private void DrawSplitLabel(DrawingContext dc, Rect section, string label, Brush color)
    {
        var screen = new Rect((section.X - _presentedOriginX) * _presentedZoom,
            (section.Y - _presentedOriginY) * _presentedZoom,
            section.Width * _presentedZoom, section.Height * _presentedZoom);
        Rect visible = Rect.Intersect(screen, new Rect(RenderSize));
        if (visible.IsEmpty)
        {
            return;
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (pixelsPerDip != _splitLabelScale)
        {
            _splitLabelText.Clear();
            _splitLabelScale = pixelsPerDip;
        }

        if (!_splitLabelText.TryGetValue(label, out FormattedText? text))
        {
            text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                SplitLabelTypeface, ThemeTypography.FontSizeBody, color, pixelsPerDip);
            _splitLabelText.Add(label, text);
        }

        var size = new Size(text.Width + 10, text.Height + 4);
        if (SplitLabelPlacement.Find(visible, size) is { } position)
        {
            bool needsClip = size.Width > visible.Width || size.Height > visible.Height;
            if (needsClip)
            {
                dc.PushClip(new RectangleGeometry(visible));
            }
            dc.DrawRoundedRectangle(SplitLabelBackground, null, new Rect(position, size), 3, 3);
            dc.DrawText(text, position + new Vector(5, 2));
            if (needsClip)
            {
                dc.Pop();
            }
        }
    }

    private static readonly GuidelineSet BitmapGuidelines = CreateBitmapGuidelines();

    private static GuidelineSet CreateBitmapGuidelines()
    {
        // 縦横 1 本ずつにし、ビットマップ全体を平行移動だけでそろえる(拡大縮小させない)
        var guidelines = new GuidelineSet(new[] { 0.0 }, new[] { 0.0 });
        guidelines.Freeze();
        return guidelines;
    }

    private static readonly Pen KeyCursorPen = CreateCursorPen(ThemeColors.KeyCursor);
    private static readonly Pen KeyCursorGuidePen = CreateCursorPen(ThemeColors.KeyCursorGuide);

    private static Pen CreateCursorPen(Color color)
    {
        var brush = ThemeColors.Brush(color);
        var pen = new Pen(brush, 1.2);
        pen.Freeze();
        return pen;
    }

    private static readonly Pen GhostCursorPen = CreateCursorPen(ThemeColors.GhostCursor);

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

        // 十字は画素の中心で交わらせる(ゴーストカーソル・プロファイルマーカーと同じ規約)。
        // 枠は縮小表示でも見えるよう最小 9px にし、その中心も画素の中心に置く
        // (枠を画素の左上から描くと、ズーム 9 未満では交点が右下の別の画素を指す)
        double cx = (_keyCursorX + 0.5 - _originX) * _zoom;
        double cy = (_keyCursorY + 0.5 - _originY) * _zoom;
        double size = Math.Max(9, _zoom);

        // 画面外まで伸びるガイド線で、拡大時でも位置を見失わないようにする
        dc.DrawLine(KeyCursorGuidePen, new Point(0, cy), new Point(ActualWidth, cy));
        dc.DrawLine(KeyCursorGuidePen, new Point(cx, 0), new Point(cx, ActualHeight));
        dc.DrawRectangle(null, KeyCursorPen, new Rect(cx - size / 2, cy - size / 2, size, size));
    }

    private static readonly Pen ProfileActivePen = CreateProfilePen(dashed: false);
    private static readonly Pen ProfileInactivePen = CreateProfilePen(dashed: true);

    private static Pen CreateProfilePen(bool dashed)
    {
        var brush = ThemeColors.Brush(dashed ? ThemeColors.ProfileInactive : ThemeColors.ProfileActive);
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

        Pen horizontalPen = _profileHorizontalActive ? ProfileActivePen : ProfileInactivePen;
        Pen verticalPen = _profileHorizontalActive ? ProfileInactivePen : ProfileActivePen;

        // マーカーは元画像の行・列で持ち、いまの表示でその行・列が並ぶ位置に描く。
        // どの象限にも並ばない行・列(チャネル分割での奇数寸法の端)の線は描かない
        if (TryMapSourceLineToDisplay(_profileY, _image.Height, out int displayY))
        {
            double screenY = (displayY + 0.5 - _originY) * _zoom;
            if (screenY >= 0 && screenY <= ActualHeight)
            {
                dc.DrawLine(horizontalPen, new Point(0, screenY), new Point(ActualWidth, screenY));
            }
        }

        if (TryMapSourceLineToDisplay(_profileX, _image.Width, out int displayX))
        {
            double screenX = (displayX + 0.5 - _originX) * _zoom;
            if (screenX >= 0 && screenX <= ActualWidth)
            {
                dc.DrawLine(verticalPen, new Point(screenX, 0), new Point(screenX, ActualHeight));
            }
        }
    }

    /// <summary>
    /// 元画像の1本の行(または列)の座標を、いまの表示でその行(列)が並ぶ表示座標へ写す。
    /// </summary>
    /// <remarks>
    /// チャネル分割表示でも、元画像の1行はタイルの1行(左右の象限に偶数列・奇数列)に、
    /// 1列はタイルの1列(上下の象限に偶数行・奇数行)に並ぶので、軸ごとに写せる。
    /// </remarks>
    /// <param name="source">元画像の行Y(または列X)。</param>
    /// <param name="sourceLength">元画像の高さ(または幅)。</param>
    /// <param name="display">表示座標。</param>
    /// <returns>表示に並んでいればtrue。</returns>
    private bool TryMapSourceLineToDisplay(int source, int sourceLength, out int display)
    {
        if (!IsChannelSplitLayout)
        {
            display = source;
            return true;
        }

        return BayerSplit.TryMapSourceToTiledAxis(source, sourceLength & ~1, out display);
    }

    private static readonly Pen HotMarkerPen = CreateMarkerPen(ThemeColors.HotPixel);
    private static readonly Pen DeadMarkerPen = CreateMarkerPen(ThemeColors.DeadPixel);

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

        // マーカーは最大10万個あり、全件の走査と記録は1回で数十〜数百msかかる。OnRender は ROI のドラッグ・
        // 画素カーソル・ゴーストカーソルの移動でも走るので、表示範囲とマーカーが変わるまで記録を使い回す
        var key = new DefectDrawingKey(
            _defectMarkers, IsChannelSplitLayout, _image.Width, _image.Height,
            _zoom, _originX, _originY, ActualWidth, ActualHeight);
        if (_defectDrawing is null || _defectDrawingKey != key)
        {
            _defectDrawing = BuildDefectMarkers(_defectMarkers);
            _defectDrawingKey = key;
        }

        dc.DrawDrawing(_defectDrawing);
    }

    /// <summary>欠陥マーカーの記録がどの表示のものか(画像は寸法だけ持ち、参照は残さない)。</summary>
    private readonly record struct DefectDrawingKey(
        IReadOnlyList<DefectPixel> Markers,
        bool ChannelSplit,
        int ImageWidth,
        int ImageHeight,
        double Zoom,
        double OriginX,
        double OriginY,
        double Width,
        double Height);

    /// <summary>
    /// 見えている欠陥マーカーを、白点・黒点それぞれ1つの図形にまとめて記録する
    /// (1個ずつ円を記録すると、10万個で1回数百msかかる)。
    /// </summary>
    private Drawing BuildDefectMarkers(IReadOnlyList<DefectPixel> defects)
    {
        double radius = Math.Max(5, _zoom * 0.7);
        var hot = new StreamGeometry();
        var dead = new StreamGeometry();
        using (StreamGeometryContext hotContext = hot.Open())
        using (StreamGeometryContext deadContext = dead.Open())
        {
            foreach (DefectPixel defect in defects)
            {
                // 欠陥は元画像の座標。チャネル分割表示ではその画素が並ぶ象限上に描き、
                // どの象限にも並ばない画素(奇数寸法の端)は描かない
                if (!TryMapSourceToDisplay(defect.X, defect.Y, out int x, out int y))
                {
                    continue;
                }

                double cx = (x + 0.5 - _originX) * _zoom;
                double cy = (y + 0.5 - _originY) * _zoom;
                if (cx < -radius || cy < -radius
                    || cx > ActualWidth + radius || cy > ActualHeight + radius)
                {
                    continue;
                }

                AddCircle(defect.Type == DefectType.Hot ? hotContext : deadContext, cx, cy, radius);
            }
        }

        var group = new DrawingGroup();
        foreach ((StreamGeometry geometry, Pen pen) in new[] { (hot, HotMarkerPen), (dead, DeadMarkerPen) })
        {
            if (!geometry.IsEmpty())
            {
                geometry.Freeze();
                group.Children.Add(new GeometryDrawing(null, pen, geometry));
            }
        }

        group.Freeze();
        return group;
    }

    /// <summary>中心 (cx, cy)・半径 radius の円を、半円2つの閉じた図として加える。</summary>
    private static void AddCircle(StreamGeometryContext context, double cx, double cy, double radius)
    {
        var size = new Size(radius, radius);
        context.BeginFigure(new Point(cx + radius, cy), isFilled: false, isClosed: true);
        context.ArcTo(
            new Point(cx - radius, cy), size, 0, isLargeArc: false, SweepDirection.Clockwise,
            isStroked: true, isSmoothJoin: false);
        context.ArcTo(
            new Point(cx + radius, cy), size, 0, isLargeArc: false, SweepDirection.Clockwise,
            isStroked: true, isSmoothJoin: false);
    }

    private static readonly Pen RoiPen = CreateRoiPen();
    private static readonly Brush RoiFill = CreateRoiFill();

    private static Pen CreateRoiPen()
    {
        var pen = new Pen(ThemeColors.Brush(ThemeColors.Accent), 1.5);
        pen.Freeze();
        return pen;
    }

    private static Brush CreateRoiFill()
    {
        return ThemeColors.Brush(ThemeColors.RoiFill);
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
        if (_overlay is null || _format is null || !DeviceScaling.ShowsRawOverlay(_zoom, DeviceScale))
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
            double fontSize = DeviceScaling.RawOverlayFontSize(_zoom);
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
