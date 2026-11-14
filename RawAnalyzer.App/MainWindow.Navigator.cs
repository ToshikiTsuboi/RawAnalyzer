using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;

namespace RawAnalyzer.App;

public partial class MainWindow
{
    private NavigatorThumbnail? _navigatorSource;
    private RawImage? _navigatorImage;
    private int _navigatorFrame;
    private CancellationTokenSource? _navigatorCts;
    private double _navigatorDpi;
    private bool _navigatorEnabled = true;

    private bool NavigatorEnabled
    {
        get => _navigatorEnabled;
        set
        {
            _navigatorEnabled = value;
            NavigatorMenuItem.IsChecked = value;
            UpdateNavigator();
        }
    }

    private void OnNavigatorMenuClick(object sender, RoutedEventArgs e) =>
        NavigatorEnabled = NavigatorMenuItem.IsChecked;

    private Rect NavigatorVisibleRegion => new(Viewport.OriginX, Viewport.OriginY,
        Viewport.ActualWidth / Viewport.Zoom, Viewport.ActualHeight / Viewport.Zoom);

    private async void UpdateNavigator()
    {
        NavigatorThumbnail? source = _navigatorEnabled && !_vm.IsCompareMode && !_closed
            ? Viewport.GetNavigatorThumbnail() : null;
        RawImage? image = source is not null ? Viewport.Image : null;
        int frame = Viewport.Frame;
        bool imageChanged = !ReferenceEquals(image, _navigatorImage) || frame != _navigatorFrame;
        double dpi = VisualTreeHelper.GetDpi(Viewport).DpiScaleX;
        if (source != _navigatorSource || imageChanged || dpi != _navigatorDpi)
        {
            _navigatorCts?.Cancel();
            _navigatorSource = source;
            _navigatorImage = image;
            _navigatorFrame = frame;
            _navigatorDpi = dpi;
            if (imageChanged || source is null)
            {
                NavigatorImage.Source = null;
            }

            UpdateNavigatorFrame();
            if (source is null)
            {
                return;
            }

            using var cts = new CancellationTokenSource();
            _navigatorCts = cts;
            try
            {
                var bitmap = await source.RenderAsync(dpi, cts.Token);
                if (!cts.IsCancellationRequested && !_closed)
                {
                    NavigatorImage.Source = bitmap;
                    UpdateNavigatorFrame();
                }
            }
            catch (Exception ex) when (TaskRaceGuard.IsAbandoned(ex))
            {
                if (!cts.IsCancellationRequested && !_closed)
                {
                    NavigatorImage.Source = null;
                    UpdateNavigatorFrame();
                }
            }
            finally
            {
                if (ReferenceEquals(_navigatorCts, cts))
                {
                    _navigatorCts = null;
                }
            }
        }
        else
        {
            UpdateNavigatorFrame();
        }
    }

    private void UpdateNavigatorFrame()
    {
        if (_navigatorSource is not { } source || NavigatorImage.Source is not { } bitmap
            || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0)
        {
            NavigatorBox.Visibility = Visibility.Collapsed;
            return;
        }

        var image = new Size(source.ImageWidth, source.ImageHeight);
        Rect visible = NavigatorVisibleRegion;
        if (NavigatorGeometry.Fits(image, visible))
        {
            NavigatorBox.Visibility = Visibility.Collapsed;
            return;
        }

        NavigatorBox.Visibility = Visibility.Visible;
        UpdateNavigatorMargin();
        var thumbnail = new Size(bitmap.Width, bitmap.Height);
        NavigatorSurface.Width = thumbnail.Width;
        NavigatorSurface.Height = thumbnail.Height;
        Rect frame = NavigatorGeometry.ViewFrame(image, thumbnail, visible);
        NavigatorFrame.Visibility = frame.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (!frame.IsEmpty)
        {
            System.Windows.Controls.Canvas.SetLeft(NavigatorFrame, frame.X);
            System.Windows.Controls.Canvas.SetTop(NavigatorFrame, frame.Y);
            NavigatorFrame.Width = Math.Max(1, frame.Width);
            NavigatorFrame.Height = Math.Max(1, frame.Height);
        }
    }

    private void UpdateNavigatorMargin()
    {
        double playback = PlaybackBar.IsVisible ? PlaybackBar.ActualHeight : 0;
        double level = LevelOverlayBox.IsVisible ? LevelOverlayBox.Margin.Bottom + LevelOverlayBox.ActualHeight : 0;
        NavigatorBox.Margin = new Thickness(10, 0, 14, Math.Max(playback, level) + 10);
    }

    private void MoveFromNavigator(Point position)
    {
        if (_navigatorSource is not { } source
            || NavigatorSurface.ActualWidth <= 0 || NavigatorSurface.ActualHeight <= 0)
        {
            return;
        }

        Point point = NavigatorGeometry.ImagePoint(new Size(source.ImageWidth, source.ImageHeight),
            new Size(NavigatorSurface.ActualWidth, NavigatorSurface.ActualHeight), position);
        Viewport.CenterOn(Math.Min((int)point.X, source.ImageWidth - 1),
            Math.Min((int)point.Y, source.ImageHeight - 1), Viewport.Zoom);
    }

    private void OnNavigatorMouseDown(object sender, MouseButtonEventArgs e)
    {
        NavigatorSurface.CaptureMouse();
        MoveFromNavigator(e.GetPosition(NavigatorSurface));
        e.Handled = true;
    }

    private void OnNavigatorMouseMove(object sender, MouseEventArgs e)
    {
        if (NavigatorSurface.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            MoveFromNavigator(e.GetPosition(NavigatorSurface));
        }
    }

    private void OnNavigatorMouseUp(object sender, MouseButtonEventArgs e) => NavigatorSurface.ReleaseMouseCapture();
}
