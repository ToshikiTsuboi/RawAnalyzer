using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Themes;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class MessageDialogTests
{
    [Theory]
    [InlineData(MessageBoxImage.Information, IconGlyphs.Info, "AccentBrush", "information")]
    [InlineData(MessageBoxImage.Warning, IconGlyphs.Warning, "WarningBrush", "warning")]
    [InlineData(MessageBoxImage.Error, IconGlyphs.Error, "ErrorBrush", "error")]
    public Task Dialog_UsesThemedIconAndWrappedCopyableMessage(
        MessageBoxImage kind, string glyph, string color, string snapshot) => WpfTestHost.Run(async () =>
    {
        const string message = "処理結果の表示例です。\n本文は折り返して表示し、ボタンからコピーできます。";
        var dialog = new MessageDialog(message, "RawAnalyzer", MessageBoxButton.OKCancel, kind)
        {
            ShowActivated = false,
        };
        try
        {
            dialog.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            dialog.UpdateLayout();
            var icon = (TextBlock)dialog.FindName("KindIcon");
            var body = (TextBlock)dialog.FindName("MessageText");
            Assert.Equal(glyph, icon.Text);
            Assert.Same(dialog.FindResource(color), icon.Foreground);
            Assert.Equal(message, body.Text);
            Assert.Equal(TextWrapping.Wrap, body.TextWrapping);
            Assert.True(body.ActualWidth <= 600);
            Assert.True(((Button)dialog.FindName("CopyButton")).IsVisible);
            // Capture the full client area, including the DockPanel's outer margin.
            var content = (FrameworkElement)dialog.Content;
            Thickness margin = content.Margin;
            WpfTestHost.CaptureIfRequested(content, $"message-{snapshot}",
                new Rect(-margin.Left, -margin.Top, content.ActualWidth + margin.Left + margin.Right,
                    content.ActualHeight + margin.Top + margin.Bottom));
        }
        finally
        {
            dialog.Close();
        }
    });

    [Theory]
    [InlineData(MessageBoxButton.OK, "OkButton", MessageBoxResult.OK)]
    [InlineData(MessageBoxButton.OK, null, MessageBoxResult.OK)]
    [InlineData(MessageBoxButton.OKCancel, "OkButton", MessageBoxResult.OK)]
    [InlineData(MessageBoxButton.OKCancel, "CancelButton", MessageBoxResult.Cancel)]
    [InlineData(MessageBoxButton.OKCancel, null, MessageBoxResult.Cancel)]
    public Task Show_ReturnsModalChoice(MessageBoxButton buttons, string? choice, MessageBoxResult expected) =>
        WpfTestHost.Run(() =>
        {
            var owner = new Window { ShowActivated = false };
            owner.Show();
            try
            {
                var interaction = owner.Dispatcher.InvokeAsync(() =>
                {
                    MessageDialog dialog = Assert.Single(owner.OwnedWindows.OfType<MessageDialog>());
                    try
                    {
                        var ok = (Button)dialog.FindName("OkButton");
                        var cancel = (Button)dialog.FindName("CancelButton");
                        Assert.True(ok.IsDefault);
                        Assert.Equal(buttons == MessageBoxButton.OK, ok.IsCancel);
                        Assert.True(cancel.IsCancel);
                        Assert.Equal(buttons == MessageBoxButton.OKCancel, cancel.IsVisible);
                        if (choice is not null)
                        {
                            ((Button)dialog.FindName(choice)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                    }
                    finally
                    {
                        if (dialog.IsVisible) dialog.Close();
                    }
                }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(expected, AppMessage.Show(owner, "検証", "RawAnalyzer", buttons, MessageBoxImage.Warning));
                interaction.Task.GetAwaiter().GetResult();
            }
            finally
            {
                owner.Close();
            }
        });
}
