using System.Windows;
using System.Windows.Threading;
using RawViewer.App.Native;
using RawViewer.App.Services;

namespace RawViewer.App;

/// <summary>
/// RawViewerアプリケーション。全ウィンドウへダークタイトルバーを適用し、
/// 未処理例外を捕捉してログに残す。
/// </summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        AppLog.Info($"RawViewer 起動 (PID {Environment.ProcessId})");

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

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info($"RawViewer 終了 (コード {e.ApplicationExitCode})");
        base.OnExit(e);
    }

    /// <summary>キャンセル由来の例外(通知不要)かどうかを判定する。</summary>
    /// <param name="exception">判定する例外。</param>
    /// <returns>キャンセル由来ならtrue。</returns>
    private static bool IsCancellation(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => true,
            AggregateException aggregate =>
                aggregate.InnerExceptions.Count > 0
                && aggregate.InnerExceptions.All(IsCancellation),
            _ => false,
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (IsCancellation(e.Exception))
        {
            // 処理中断は正常系。UIスレッドまで漏れてもアプリは継続できる
            AppLog.Info($"処理がキャンセルされました: {e.Exception.GetType().Name}");
            e.Handled = true;
            return;
        }

        AppLog.Error("DispatcherUnhandledException", e.Exception);
        e.Handled = true;
        ShowErrorDialog(e.Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // 監視されなかったタスク例外。既定ではファイナライザ経由で無視されるため記録だけ残す
        AppLog.Error("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // 復帰不能。プロセス終了前に痕跡を残すことだけが目的
        AppLog.Error(
            $"AppDomain.UnhandledException (IsTerminating={e.IsTerminating})",
            e.ExceptionObject as Exception);
    }

    private void ShowErrorDialog(Exception exception)
    {
        try
        {
            string message =
                $"予期しないエラーが発生しました。{Environment.NewLine}{Environment.NewLine}" +
                $"{exception.GetType().Name}: {exception.Message}{Environment.NewLine}{Environment.NewLine}" +
                $"詳細はログを参照してください:{Environment.NewLine}{AppLog.CurrentFilePath}";
            if (MainWindow is { IsLoaded: true } owner)
            {
                MessageBox.Show(
                    owner, message, "RawViewer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(message, "RawViewer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception dialogException)
        {
            AppLog.Error("エラーダイアログの表示に失敗", dialogException);
        }
    }
}
