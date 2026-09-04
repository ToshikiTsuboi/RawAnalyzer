using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace RawAnalyzer.Tests;

[CollectionDefinition("WPF UI", DisableParallelization = true)]
public sealed class WpfUiCollection;

// WPFはプロセス内でApplicationを一度しか生成できないため、UIテストで共有する。
// 実アプリの起動処理・セッション保存・ウィンドウ表示は行わない。
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
