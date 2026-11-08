using System.Windows;
using System.Windows.Input;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Controls;

public sealed partial class ImageViewport
{
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
            _mouseCapture.Capture();
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
        _mouseCapture.Capture();
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
            if (_mouseCapture.IsCaptured)
            {
                _mouseCapture.Release();
            }

            RestartIdleTimer();
        }

        if (!_roiDragging)
        {
            // ビューポートがキャプチャを取るのはパンとROIのドラッグだけ。どちらでもないのに
            // キャプチャが残っていれば、ボタンを離した時点で外す(残すと次のクリックが吸われる)
            if (_mouseCapture.IsCaptured)
            {
                _mouseCapture.Release();
            }

            return;
        }

        _roiDragging = false;
        if (_mouseCapture.IsCaptured)
        {
            _mouseCapture.Release();
        }

        // ROIが消えた場合も通知しないと、枠だけ消えて旧統計が残る
        bool hasRoi = _roi is { PixelCount: > 0 };
        if (notifyRoi && (hasRoi || _roiHadValue))
        {
            RoiChanged?.Invoke(this, EventArgs.Empty);
        }

        _roiHadValue = hasRoi;
    }

    /// <summary>
    /// パン・ROI のドラッグ中のマウスキャプチャの取得・解放を差し替える(テスト用。null でこのビューポート自身の
    /// OS のキャプチャに戻す)。
    /// </summary>
    /// <remarks>
    /// OS のキャプチャは環境によって取れない(対話的なデスクトップがないなど)。ドラッグの開始で取得し、ドラッグの
    /// 状態を解除するときに解放を求めることを、OS のキャプチャに頼らずに確かめるために使う。
    /// </remarks>
    /// <param name="capture">取得・解放を受けるもの。</param>
    internal void OverrideMouseCapture(IMouseCapture? capture)
    {
        _mouseCapture = capture ?? new OwnMouseCapture(this);
    }

    /// <summary>ドラッグ中のマウスキャプチャの取得・解放。</summary>
    internal interface IMouseCapture
    {
        /// <summary>キャプチャを持っているか。</summary>
        bool IsCaptured { get; }

        /// <summary>キャプチャを取る。</summary>
        void Capture();

        /// <summary>キャプチャを外す。</summary>
        void Release();
    }

    /// <summary>要素自身の(OS の)マウスキャプチャ。</summary>
    private sealed class OwnMouseCapture(UIElement element) : IMouseCapture
    {
        public bool IsCaptured => element.IsMouseCaptured;

        public void Capture() => element.CaptureMouse();

        public void Release() => element.ReleaseMouseCapture();
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

        // 修飾キーはこのキー入力を起こしたキーボードから読む(実際の入力では Keyboard.Modifiers と同じ)
        ModifierKeys modifiers = e.KeyboardDevice.Modifiers;
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
}
