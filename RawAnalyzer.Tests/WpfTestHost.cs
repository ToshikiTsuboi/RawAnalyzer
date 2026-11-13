using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace RawAnalyzer.Tests;

[CollectionDefinition("WPF UI", DisableParallelization = true)]
public sealed class WpfUiCollection;

// WPFはプロセス内でApplicationを一度しか生成できないため、UIテストで共有する。
// 実アプリの起動処理は行わない。保存先を指定できるウィンドウは一時フォルダを使う。
internal static class WpfTestHost
{
    private static readonly Lazy<Task<Dispatcher>> Host = new(Start);

    internal static Task Run(Action action) => Run(() =>
    {
        action();
        return Task.CompletedTask;
    });

    internal static async Task Run(Func<Task> action)
    {
        Dispatcher dispatcher = await Host.Value;
        await dispatcher.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));
    }

    internal static void CaptureIfRequested(FrameworkElement view, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("RAWANALYZER_UI_SNAPSHOTS");
        if (string.IsNullOrEmpty(directory)) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(view);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(view.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var bounds = new Rect(view.RenderSize);
            dc.DrawRectangle(Window.GetWindow(view)?.Background, null, bounds);
            dc.DrawRectangle(new VisualBrush(view)
            {
                Viewbox = bounds,
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
            }, null, bounds);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }

    private static Task<Dispatcher> Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/RawAnalyzer.App;component/Themes/DarkTheme.xaml", UriKind.Relative),
                });
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
                GC.KeepAlive(app);
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        })
        { IsBackground = true, Name = "WPF UI tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }
}
