using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class ToolWindowChromeTests
{
    [Fact]
    public Task Dialogs_UseStandardWindowStyle() => WpfTestHost.Run(() =>
    {
        Window[] dialogs = [new AboutWindow(), new ShortcutHelpWindow([])];
        foreach (Window dialog in dialogs)
        {
            try
            {
                Assert.Equal(WindowStyle.SingleBorderWindow, dialog.WindowStyle);
            }
            finally
            {
                dialog.Close();
            }
        }
    });

    [Fact]
    public Task ProgressWindow_CancelButtonKeepsTheDialogOpenUntilWorkStops() => WpfTestHost.Run(() =>
    {
        var owner = new Window { ShowActivated = false };
        owner.Show();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool stayedOpen = false;
        try
        {
            ProgressWindow result = ProgressWindow.Run(owner, "ビニングを適用中…", async (report, token) =>
            {
                using var registration = token.Register(() => canceled.TrySetResult());
                report.Report(0.42);
                await owner.Dispatcher.InvokeAsync(() =>
                {
                    var progress = owner.OwnedWindows.OfType<ProgressWindow>().Single();
                    var bar = (ProgressBar)progress.FindName("Bar");
                    Assert.Equal(42, bar.Value);
                    WpfTestHost.CaptureDialogIfRequested(progress, "d9-progress");
                    WpfTestHost.Invoke((Button)progress.FindName("CancelButton"));
                }, DispatcherPriority.Background);
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await owner.Dispatcher.InvokeAsync(() =>
                {
                    var progress = owner.OwnedWindows.OfType<ProgressWindow>().Single();
                    stayedOpen = progress.IsVisible && !((Button)progress.FindName("CancelButton")).IsEnabled;
                });
                token.ThrowIfCancellationRequested();
            });
            Assert.True(stayedOpen);
            Assert.True(result.WasCanceled);
            Assert.False(result.Succeeded);
            Assert.Null(result.Error);
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public Task ProgressWindow_CloseRequestsCancellationAndWaitsForWork() => WpfTestHost.Run(() =>
    {
        var owner = new Window { ShowActivated = false };
        owner.Show();
        var cancellation = new TaskCompletionSource();
        WindowStyle? style = null;
        bool stayedOpen = false;
        try
        {
            ProgressWindow result = ProgressWindow.Run(owner, "検証中", async (_, token) =>
            {
                using var registration = token.Register(() => cancellation.TrySetResult());
                await owner.Dispatcher.InvokeAsync(() =>
                {
                    var progress = owner.OwnedWindows.OfType<ProgressWindow>().Single();
                    style = progress.WindowStyle;
                    progress.Close();
                    stayedOpen = progress.IsVisible;
                }, DispatcherPriority.Normal);
                token.ThrowIfCancellationRequested();
            });
            Assert.Equal(WindowStyle.SingleBorderWindow, style);
            Assert.True(stayedOpen);
            Assert.True(cancellation.Task.IsCompleted);
            Assert.True(result.WasCanceled);
            Assert.False(result.Succeeded);
        }
        finally
        {
            owner.Close();
        }
    });
}
