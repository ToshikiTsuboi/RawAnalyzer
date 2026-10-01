using System.IO;
using System.Text.Json;
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
/// <para>
/// 複数起動したインスタンスは同じファイルを使う。起動時に読んだ記憶を丸ごと書き戻すと、別のインスタンスで
/// 直した・消した・足した記憶が古い内容で巻き戻るため、変更は保存されている最新の記憶へ当てて保存し
/// (<see cref="FormatHistoryStore.Update"/>)、<see cref="History"/> を読むたびに他のインスタンスの保存を取り込む。
/// </para>
/// </remarks>
internal sealed class FormatMemory
{
    private readonly FormatHistoryStore _store;
    private readonly Action<string> _warn;
    private readonly Func<DateTime> _clock;

    /// <summary>保存できていない変更(次の変更で保存し直し、読み直した記憶にも当て直す)。</summary>
    private readonly List<Func<FormatHistory, bool>> _unsaved = new();

    private FormatHistory _history;

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
        _history = store.LoadOrQuarantine(out bool corrupted);
        if (corrupted)
        {
            _warn($"サイズ別フォーマットの記憶が読み込めなかったため退避し、空から始めます: {store.BackupPath}");
        }
    }

    /// <summary>
    /// 記憶(読み取り専用)。別のインスタンスが保存していれば読み直してから返す。書き換えは保存まで行う
    /// <see cref="RememberLoaded"/>・<see cref="Correct"/>・<see cref="Forget"/> で行う(手元の記憶を直接書き換えると
    /// 保存されず、読み直したときに消える)。
    /// </summary>
    internal IReadOnlyFormatHistory History
    {
        get
        {
            Refresh();
            return _history;
        }
    }

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
        Apply(history =>
        {
            if (correctFrom is null
                || !history.Replace(fileSize, extension, correctFrom, format, autoOpen, now))
            {
                history.Record(fileSize, extension, format, autoOpen, now);
            }

            return true;
        });
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
        if (fileSize < 0 || !FormatHistory.CanOpen(replacement, fileSize))
        {
            return false;
        }

        string extension = FormatHistory.ExtensionOf(path);
        DateTime now = _clock();
        return Apply(history => history.Replace(fileSize, extension, original, replacement, null, now));
    }

    /// <summary>キー(サイズ・拡張子)の記憶をすべて削除して保存する。</summary>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <param name="extension">拡張子。</param>
    /// <returns>削除した件数。</returns>
    internal int Forget(long fileSize, string extension)
    {
        int removed = 0;
        Apply(history =>
        {
            removed = history.Remove(fileSize, extension);
            return removed > 0;
        });
        return removed;
    }

    /// <summary>
    /// 変更を保存されている最新の記憶へ当てて保存する(保存できていない前の変更も当て直す)。
    /// </summary>
    /// <param name="change">記憶への変更。変更したら true を返す。</param>
    /// <returns>この変更が記憶を変えたか。</returns>
    private bool Apply(Func<FormatHistory, bool> change)
    {
        _unsaved.Add(change);
        bool changed = false;
        FormatHistory? applied = null;
        try
        {
            _history = _store.Update(latest =>
            {
                applied = latest;
                bool any = false;
                foreach (Func<FormatHistory, bool> pending in _unsaved)
                {
                    bool result = pending(latest);
                    any |= result;
                    if (ReferenceEquals(pending, change))
                    {
                        changed = result;
                    }
                }

                return any;
            });
            _unsaved.Clear();
            return changed;
        }
        catch (JsonException)
        {
            // 保存されている記憶が(このアプリ以外で)壊された。読み直せないので従来どおり手元の記憶で置き換える
            changed = change(_history);
            try
            {
                _store.Save(_history);
                _unsaved.Clear();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn($"サイズ別フォーマットの記憶を保存できませんでした: {ex.Message}");
            }

            return changed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // このインスタンスでは変更を使い続け、次の変更で(読み直した記憶へ当てて)保存し直す。
            // 読み直せていれば(保存だけ失敗)、変更を当て終えた最新の記憶を使う
            _warn($"サイズ別フォーマットの記憶を保存できませんでした: {ex.Message}");
            if (applied is not null)
            {
                _history = applied;
                return changed;
            }

            return change(_history);
        }
    }

    /// <summary>別のインスタンスが保存した記憶を取り込む(保存できていない変更は当て直す)。</summary>
    private void Refresh()
    {
        FormatHistory? latest;
        try
        {
            latest = _store.ReloadIfChanged();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 読めなければ手元の記憶を使い続ける
            return;
        }

        if (latest is null)
        {
            return;
        }

        foreach (Func<FormatHistory, bool> pending in _unsaved)
        {
            pending(latest);
        }

        _history = latest;
    }
}
