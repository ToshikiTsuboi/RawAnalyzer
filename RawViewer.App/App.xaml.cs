using System.Windows;
using RawViewer.App.Native;

namespace RawViewer.App;

/// <summary>
/// RawViewerアプリケーション。全ウィンドウへダークタイトルバーを適用する。
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // アプリ内で開かれるすべてのウィンドウ(ダイアログ含む)にダークタイトルバーを適用
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window)
                {
                    WindowTheme.Apply(window);
                }
            }));
    }
}
