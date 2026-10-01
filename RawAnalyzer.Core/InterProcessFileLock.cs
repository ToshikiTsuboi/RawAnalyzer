using System.Security.Cryptography;
using System.Text;

namespace RawAnalyzer.Core;

/// <summary>
/// 同じ設定ファイルを読み書きする他のプロセス(複数起動した RawAnalyzer)との排他。
/// </summary>
/// <remarks>
/// <para>
/// 起動時に読んだ内容を丸ごと書き戻すと、別のインスタンスがその後に保存した内容が巻き戻る。設定ファイルの変更は
/// この排他の中で「最新を読み直し、その操作の変更だけを当てて保存する」。保存中の置き換え
/// (<see cref="AtomicFileWriter"/>)と読み込みが重なって失敗しないよう、読むときも排他する。
/// </para>
/// <para>
/// ファイルのフルパスから作った名前付きミューテックスを使う。名前付きミューテックスは取得したスレッドで解放する
/// 必要があるため、取得したスレッドで同期に使い、同じスレッドで破棄する。
/// </para>
/// </remarks>
public static class InterProcessFileLock
{
    /// <summary>他のインスタンスとの排他を待つ上限。設定ファイルの読み書きは数ミリ秒で終わる。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// ファイルの排他を取得する。取得したスレッドで、読み書きを終えたら破棄する。
    /// </summary>
    /// <param name="filePath">読み書きするファイル(大文字・小文字と相対・絶対の違いは同じファイルとみなす)。</param>
    /// <returns>破棄すると排他を解放するオブジェクト。</returns>
    /// <exception cref="IOException">他のインスタンスが長く使用中で、排他を取得できなかった場合。</exception>
    public static IDisposable Acquire(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        // ミューテックス名に \ は使えないので、正規化したフルパスのハッシュで名前を作る
        string fullPath = Path.GetFullPath(filePath);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant()));
        var mutex = new Mutex(initiallyOwned: false, @"Local\RawAnalyzer.FileLock." + Convert.ToHexString(hash, 0, 16));
        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(Timeout);
            }
            catch (AbandonedMutexException)
            {
                // 保持したまま終了したプロセスがあった。所有権は得ている(書きかけは AtomicFileWriter が置き換えない)
                acquired = true;
            }

            if (!acquired)
            {
                throw new IOException(
                    $"他の RawAnalyzer が {Path.GetFileName(fullPath)} を使用中のため、読み書きできませんでした。");
            }

            return new Release(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>取得したミューテックスを解放して破棄する。</summary>
    private sealed class Release(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            if (_mutex is { } held)
            {
                _mutex = null;
                held.ReleaseMutex();
                held.Dispose();
            }
        }
    }
}
