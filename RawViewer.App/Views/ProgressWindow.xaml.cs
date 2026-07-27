using System.ComponentModel;
using System.Windows;
using RawViewer.App.Services;

namespace RawViewer.App.Views;

/// <summary>
/// バックグラウンド処理の進捗表示とキャンセルを行うモーダルウィンドウ。
/// </summary>
/// <remarks>
/// タイトルバーの[×]でもキャンセル扱いになり、処理が実際に停止するまでウィンドウは閉じない。
/// 未完了のまま閉じて呼び出し側が「成功」と誤認することはない。
/// </remarks>
public partial class ProgressWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<IProgress<double>, CancellationToken, Task> _work;
    private bool _finished;

    /// <summary>処理中に発生した例外(キャンセル時はnull)。</summary>
    public Exception? Error { get; private set; }

    /// <summary>キャンセルされたかどうか。</summary>
    public bool WasCanceled { get; private set; }

    /// <summary>処理が最後まで走り、例外もキャンセルも無かったかどうか。</summary>
    public bool Succeeded => _finished && !WasCanceled && Error is null;

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

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            // 処理継続中に閉じられた場合は、キャンセルを要求して完了を待つ
            e.Cancel = true;
            RequestCancel();
            return;
        }

        base.OnClosing(e);
    }

    private void RequestCancel()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        CancelButton.IsEnabled = false;
        MessageText.Text = "キャンセルしています…";
        _cts.Cancel();
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
        catch (Exception ex) when (IsCancellation(ex))
        {
            WasCanceled = true;
        }
        catch (Exception ex)
        {
            Error = ex;
            AppLog.Error("ProgressWindow の処理が例外で終了", ex);
        }

        _finished = true;
        _cts.Dispose();
        Close();
    }

    /// <summary>キャンセル由来の例外(AggregateExceptionに包まれた場合も含む)かどうか。</summary>
    private static bool IsCancellation(Exception exception) => exception switch
    {
        OperationCanceledException => true,
        AggregateException aggregate =>
            aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsCancellation),
        _ => false,
    };

    private void OnCancelClick(object sender, RoutedEventArgs e) => RequestCancel();
}
