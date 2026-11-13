using System.Windows;
using RawAnalyzer.App.Views;

namespace RawAnalyzer.App.Services;

/// <summary>アプリと同じテーマで、モーダルのメッセージを表示する。</summary>
internal static class AppMessage
{
    internal static MessageBoxResult Show(
        Window owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage kind)
    {
        // インポート画面の初期化中に出る警告では、まだその画面のウィンドウハンドルがない。
        Window? visibleOwner = owner.IsVisible
            ? owner
            : Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        var dialog = new MessageDialog(text, caption, buttons, kind)
        {
            Owner = visibleOwner,
            WindowStartupLocation = visibleOwner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
        };
        return dialog.ShowMessage();
    }
}
