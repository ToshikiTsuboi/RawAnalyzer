using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Views;

namespace RawAnalyzer.Tests;

/// <summary>
/// ラインプロファイル窓・射影の窓の UI テストの共通の手助け。グラフ(統計・縦軸・横軸・縦軸の設定)は窓と別の
/// 名前の範囲を持つ共有のコントロール(<see cref="ProfilePlotView"/>)にあるので、窓とグラフの両方から名前で探す。
/// </summary>
internal static class ProfileWindowParts
{
    /// <summary>窓、なければ窓のグラフ(Plot)から名前で要素を探す。</summary>
    /// <typeparam name="T">要素の型。</typeparam>
    /// <param name="window">窓。</param>
    /// <param name="name">要素の名前(x:Name)。</param>
    /// <returns>要素。</returns>
    internal static T Find<T>(Window window, string name)
        where T : class
    {
        object? found = window.FindName(name)
            ?? (window.FindName("Plot") as ProfilePlotView)?.FindName(name);
        return found as T ?? throw new InvalidOperationException($"{name} ({typeof(T).Name}) が見つかりません");
    }

    /// <summary>窓を 800×440 で配置する(表示はしない)。</summary>
    /// <param name="window">窓。</param>
    internal static void Layout(Window window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(800, 440));
        content.Arrange(new Rect(0, 0, 800, 440));
        content.UpdateLayout();
    }

    /// <summary>環境変数 RAWANALYZER_UI_SNAPSHOTS があれば、窓の中身を PNG に書き出す(見た目の確認用)。</summary>
    /// <param name="window">窓。</param>
    /// <param name="name">ファイル名(拡張子なし)。</param>
    internal static void CaptureIfRequested(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("RAWANALYZER_UI_SNAPSHOTS");
        if (string.IsNullOrEmpty(directory)) return;
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(800, 440, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext dc = background.RenderOpen())
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, 800, 440));
        bitmap.Render(background);
        bitmap.Render(content);
        Save(bitmap, directory, name);
    }

    /// <summary>環境変数 RAWANALYZER_UI_SNAPSHOTS があれば、要素を PNG に書き出す。</summary>
    /// <param name="element">要素。</param>
    /// <param name="name">ファイル名(拡張子なし)。</param>
    /// <param name="width">幅。</param>
    /// <param name="height">高さ。</param>
    internal static void CaptureElementIfRequested(FrameworkElement element, string name, int width, int height)
    {
        string? directory = Environment.GetEnvironmentVariable("RAWANALYZER_UI_SNAPSHOTS");
        if (string.IsNullOrEmpty(directory)) return;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        Save(bitmap, directory, name);
    }

    private static void Save(BitmapSource bitmap, string directory, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(System.IO.Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }
}
