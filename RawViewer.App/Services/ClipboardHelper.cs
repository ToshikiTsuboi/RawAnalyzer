using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace RawViewer.App.Services;

/// <summary>
/// クリップボードとテキストファイル出力の共通ヘルパ。
/// </summary>
/// <remarks>
/// クリップボードはOSの共有資源で、RDP・クリップボード履歴・常駐ツールが掴んでいると
/// <c>CLIPBRD_E_CANT_OPEN</c>(ExternalException)になる。素の <c>Clipboard.SetText</c> は
/// これを投げるため、読み込み済み画像やROIごとアプリが落ちていた。
/// ここでは数回リトライし、失敗しても false を返すだけにする。
/// </remarks>
internal static class ClipboardHelper
{
    private const int RetryTimes = 5;
    private const int RetryDelayMs = 100;

    /// <summary>テキストをクリップボードへコピーする。</summary>
    /// <param name="text">コピーする文字列。空なら何もせず false。</param>
    /// <returns>成功したかどうか。</returns>
    public static bool TrySetText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return TryWithRetry(() => Clipboard.SetDataObject(text, copy: true), "テキスト");
    }

    /// <summary>画像をクリップボードへコピーする。</summary>
    /// <param name="bitmap">コピーする画像。</param>
    /// <returns>成功したかどうか。</returns>
    public static bool TrySetImage(BitmapSource? bitmap)
    {
        if (bitmap is null)
        {
            return false;
        }

        return TryWithRetry(
            () =>
            {
                var data = new DataObject();
                data.SetImage(bitmap);
                Clipboard.SetDataObject(data, copy: true);
            },
            "画像");
    }

    /// <summary>
    /// クリップボード操作を数回リトライする。
    /// 他プロセスがクリップボードを開いている間は一時的に失敗するため。
    /// </summary>
    private static bool TryWithRetry(Action action, string what)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < RetryTimes; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex) when (
                ex is ExternalException or COMException or InvalidOperationException)
            {
                last = ex;
                Thread.Sleep(RetryDelayMs);
            }
        }

        AppLog.Warn($"クリップボードへの{what}コピーに失敗: {last?.Message}");
        return false;
    }

    /// <summary>
    /// テキストファイルを書き出す。書込不可パスや他アプリが開いたままの場合も落ちない。
    /// </summary>
    /// <param name="path">出力先パス。</param>
    /// <param name="text">書き出す内容。</param>
    /// <param name="error">失敗理由(成功時はnull)。</param>
    /// <returns>成功したかどうか。</returns>
    public static bool TryWriteText(string path, string text, out string? error)
    {
        try
        {
            File.WriteAllText(path, text, Encoding.UTF8);
            error = null;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException
                or System.Security.SecurityException or ArgumentException)
        {
            AppLog.Warn($"ファイル書き出しに失敗 ({path}): {ex.Message}");
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// テキストファイルを書き出し、失敗したらメッセージボックスで通知する。
    /// </summary>
    /// <param name="owner">親ウィンドウ。</param>
    /// <param name="path">出力先パス。</param>
    /// <param name="text">書き出す内容。</param>
    /// <param name="title">メッセージボックスのタイトル。</param>
    /// <returns>成功したかどうか。</returns>
    public static bool WriteTextOrWarn(Window owner, string path, string text, string title)
    {
        if (TryWriteText(path, text, out string? error))
        {
            return true;
        }

        MessageBox.Show(owner, $"保存に失敗しました: {error}", title,
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
