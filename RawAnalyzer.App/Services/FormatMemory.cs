using System.IO;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// サイズ別フォーマット記憶(%AppData%\RawAnalyzer\format-history.json)を持ち、変更のたびに保存する。
/// </summary>
/// <remarks>
/// <para>
/// 記録するのは raw の読み込みに成功したときだけ(通常の「開く」・比較ペイン)。ファイル連番の送り
/// (表示中のフォーマットで読む)とバッチ書き出しでは記録しない。
/// </para>
/// <para>
/// 保存は変更のたびに UI スレッドで同期に行う。上限200件の JSON(数十KB)なので長く止めない
/// (セッションの保存と同じ扱い)。保存に失敗しても記憶はメモリ上に残して警告をログへ残し、
/// 次の変更で保存し直す。
/// </para>
/// </remarks>
internal sealed class FormatMemory
{
    private readonly FormatHistoryStore _store;
    private readonly Action<string> _warn;
    private readonly Func<DateTime> _clock;

    /// <summary>保存先から記憶を読み込む。壊れていれば .bak へ退避して空から始め、警告を残す。</summary>
    /// <param name="store">保存先(テストでは一時フォルダ)。</param>
    /// <param name="warn">警告の出力先(既定はアプリのログ)。</param>
    /// <param name="clock">最終使用日時に使う現在時刻(UTC。既定は DateTime.UtcNow)。</param>
    internal FormatMemory(
        FormatHistoryStore store, Action<string>? warn = null, Func<DateTime>? clock = null)
    {
        _store = store;
        _warn = warn ?? AppLog.Warn;
        _clock = clock ?? (() => DateTime.UtcNow);
        History = store.LoadOrQuarantine(out bool corrupted);
        if (corrupted)
        {
            _warn($"サイズ別フォーマットの記憶が読み込めなかったため退避し、空から始めます: {store.BackupPath}");
        }
    }

    /// <summary>記憶の本体。</summary>
    internal FormatHistory History { get; }

    /// <summary>
    /// raw の読み込みに成功したフォーマットを記録して保存する。
    /// </summary>
    /// <param name="path">読み込んだファイル。</param>
    /// <param name="fileSize">ファイルサイズ(バイト)。負(不明)なら記録しない。</param>
    /// <param name="format">読み込めたフォーマット。</param>
    /// <param name="autoOpen">ダイアログの「次回からこの形式で開く」(記憶から開いたときは null)。</param>
    /// <param name="correctFrom">
    /// フォーマットを指定し直して開き直したときの元のフォーマット。そのキーの記憶にあれば
    /// 新しいフォーマットで置き換える(なければ新しいフォーマットを記録する)。
    /// </param>
    internal void RememberLoaded(
        string path, long fileSize, RawFormat format, bool? autoOpen, RawFormat? correctFrom = null)
    {
        // 読み込みの後にファイルが縮んだなど、そのサイズで開けないものは記憶しない
        if (fileSize < 0 || !FormatHistory.CanOpen(format, fileSize))
        {
            return;
        }

        string extension = FormatHistory.ExtensionOf(path);
        DateTime now = _clock();
        if (correctFrom is null
            || !History.Replace(fileSize, extension, correctFrom, format, autoOpen, now))
        {
            History.Record(fileSize, extension, format, autoOpen, now);
        }

        Save();
    }

    /// <summary>
    /// 表示中のファイルのフォーマットをその場で変えた(右パネルの Bayer)とき、元のフォーマットが
    /// そのキーの記憶にあれば置き換えて保存する(自動適用フラグは引き継ぐ)。
    /// </summary>
    /// <param name="path">表示中のファイル。</param>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <param name="original">変更前のフォーマット。</param>
    /// <param name="replacement">変更後のフォーマット。</param>
    /// <returns>置き換えたら true。</returns>
    internal bool Correct(string path, long fileSize, RawFormat original, RawFormat replacement)
    {
        if (fileSize < 0 || !FormatHistory.CanOpen(replacement, fileSize)
            || !History.Replace(
                fileSize, FormatHistory.ExtensionOf(path), original, replacement, null, _clock()))
        {
            return false;
        }

        Save();
        return true;
    }

    /// <summary>キー(サイズ・拡張子)の記憶をすべて削除して保存する。</summary>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <param name="extension">拡張子。</param>
    /// <returns>削除した件数。</returns>
    internal int Forget(long fileSize, string extension)
    {
        int removed = History.Remove(fileSize, extension);
        if (removed > 0)
        {
            Save();
        }

        return removed;
    }

    private void Save()
    {
        try
        {
            _store.Save(History);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn($"サイズ別フォーマットの記憶を保存できませんでした: {ex.Message}");
        }
    }
}
