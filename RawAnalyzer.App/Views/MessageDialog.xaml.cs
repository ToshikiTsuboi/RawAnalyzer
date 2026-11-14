using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Themes;

namespace RawAnalyzer.App.Views;

internal partial class MessageDialog : Window
{
    private readonly bool _canCancel;

    internal MessageDialog(string text, string caption, MessageBoxButton buttons, MessageBoxImage kind)
    {
        InitializeComponent();
        _canCancel = buttons == MessageBoxButton.OKCancel;
        Title = caption;
        MessageText.Text = text;
        CancelButton.Visibility = _canCancel ? Visibility.Visible : Visibility.Collapsed;
        OkButton.IsCancel = !_canCancel;

        (string icon, string brush, string name) = kind switch
        {
            MessageBoxImage.Error => (IconGlyphs.Error, "ErrorBrush", "エラー"),
            MessageBoxImage.Warning => (IconGlyphs.Warning, "WarningBrush", "警告"),
            _ => (IconGlyphs.Info, "AccentBrush", "情報"),
        };
        KindIcon.Text = icon;
        KindIcon.Foreground = (Brush)FindResource(brush);
        AutomationProperties.SetName(KindIcon, name);
        Loaded += (_, _) => OkButton.Focus();
    }

    internal MessageBoxResult ShowMessage() =>
        ShowDialog() == true || !_canCancel ? MessageBoxResult.OK : MessageBoxResult.Cancel;

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        CopyButton.IsEnabled = false;
        bool copied = await ClipboardHelper.TrySetTextAsync(MessageText.Text);
        CopyResultText.Text = copied ? "コピーしました" : "コピーできませんでした";
        CopyButton.IsEnabled = true;
    }
}
