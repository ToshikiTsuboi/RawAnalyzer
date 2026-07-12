using System.Windows;

namespace RawViewer.App.Views;

/// <summary>
/// バックグラウンド処理の進捗表示とキャンセルを行うモーダルウィンドウ。
/// </summary>
public partial class ProgressWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<IProgress<double>, CancellationToken, Task> _work;

    /// <summary>処理中に発生した例外(キャンセル時はnull)。</summary>
    public Exception? Error { get; private set; }

    /// <summary>キャンセルされたかどうか。</summary>
    public bool WasCanceled { get; private set; }

    private ProgressWindow(string message, Func<IProgress<double>, CancellationToken, Task> work)
    {
        InitializeComponent();
        MessageText.Text = message;
        _work = work;
        ContentRendered += OnContentRendered;
    }

    /// <summary>
    /// 進捗ウィンドウを表示しながら処理を実行する。
    /// </summary>
    /// <param name="owner">親ウィンドウ。</param>
    /// <param name="message">表示するメッセージ。</param>
    /// <param name="work">実行する処理(進捗0〜1を報告)。</param>
    /// <returns>完了状態(キャンセル・例外はプロパティで返す)。</returns>
    public static ProgressWindow Run(
        Window owner, string message, Func<IProgress<double>, CancellationToken, Task> work)
    {
        var window = new ProgressWindow(message, work) { Owner = owner };
        window.ShowDialog();
        return window;
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        var progress = new Progress<double>(p =>
        {
            double percent = Math.Clamp(p * 100, 0, 100);
            Bar.Value = percent;
            PercentText.Text = $"{percent:F0}%";
        });

        try
        {
            await Task.Run(() => _work(progress, _cts.Token), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            WasCanceled = true;
        }
        catch (Exception ex)
        {
            Error = ex;
        }

        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        _cts.Cancel();
    }
}
