using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RawAnalyzer.Core;

/// <summary>
/// サイズ別フォーマット記憶(<see cref="FormatHistory"/>)の永続化ストア。
/// 既定では %AppData%/RawAnalyzer/format-history.json に保存する。
/// </summary>
/// <remarks>
/// <para>
/// JSON の書式はプリセット(<see cref="FormatPresetStore"/>)と同じ設定を使う
/// (列挙型は文字列、HDR 方式の旧名は読み込み時に写す)。
/// </para>
/// <para>
/// RawAnalyzer は複数起動でき、どのインスタンスも同じファイルを使う。起動時に読んだ記憶を丸ごと書き戻すと、
/// 別のインスタンスで直した・消した・足した記憶が古い内容で巻き戻るため、変更は <see cref="Update"/> で
/// 「他のインスタンスと排他して最新を読み直し、変更を当てて保存する」。他のインスタンスの変更は
/// <see cref="ReloadIfChanged"/> で取り込む。スレッドセーフではない(UI スレッドから使う)。
/// </para>
/// </remarks>
public sealed class FormatHistoryStore
{
    /// <summary>保存ファイル名。</summary>
    public const string DefaultFileName = "format-history.json";

    /// <summary>他のインスタンスとの排他を待つ上限。読み書きは数ミリ秒で終わる。</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>このストアが最後に読んだ・保存したファイルの内容(ファイルがなければ null)。</summary>
    private byte[]? _knownContent;

    /// <summary><see cref="_knownContent"/> が有効か(まだ一度も読み書きしていなければ false)。</summary>
    private bool _known;

    /// <summary>
    /// 既定の保存先(%AppData%/RawAnalyzer)を使うストアを生成する。
    /// </summary>
    public FormatHistoryStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RawAnalyzer"))
    {
    }

    /// <summary>
    /// 保存先ディレクトリを指定してストアを生成する(テスト用途など)。
    /// </summary>
    /// <param name="directory">format-history.json を配置するディレクトリ。</param>
    public FormatHistoryStore(string directory)
    {
        FilePath = Path.Combine(directory, DefaultFileName);
    }

    /// <summary>記憶ファイルのフルパス。</summary>
    public string FilePath { get; }

    /// <summary>破損した記憶ファイルの退避先。</summary>
    public string BackupPath => FilePath + ".bak";

    /// <summary>
    /// 記憶を読み込む。ファイルが存在しない場合は空の記憶を返す。
    /// 使えない項目は捨てる(<see cref="FormatHistory(IEnumerable{FormatHistoryEntry})"/>)。
    /// </summary>
    /// <returns>読み込んだ記憶。</returns>
    /// <exception cref="JsonException">ファイル内容がJSONとして不正な場合。</exception>
    public FormatHistory Load()
    {
        byte[]? content = ReadContent();

        // 壊れた内容も「読んだ内容」として覚え、ReloadIfChanged で毎回解釈し直さない
        Remember(content);
        return Parse(content);
    }

    /// <summary>
    /// 保存ファイルが、このストアが最後に読んだ・保存した内容から変わっていれば(別のインスタンスが保存した)、
    /// 読み直した記憶を返す。他のインスタンスの保存とは排他する。
    /// </summary>
    /// <returns>読み直した記憶。変わっていなければ null。</returns>
    /// <exception cref="JsonException">ファイル内容がJSONとして不正な場合(次に内容が変わるまで再び解釈しない)。</exception>
    /// <exception cref="IOException">読めない、または他のインスタンスが長く使用中の場合。</exception>
    /// <exception cref="UnauthorizedAccessException">読む権限がない場合。</exception>
    public FormatHistory? ReloadIfChanged()
    {
        byte[]? content;
        using (AcquireLock())
        {
            content = ReadContent();
        }

        if (IsKnown(content))
        {
            return null;
        }

        Remember(content);
        return Parse(content);
    }

    /// <summary>
    /// 他のインスタンスと排他しながら、保存されている最新の記憶を読み直して変更を当て、変更したときだけ保存する。
    /// </summary>
    /// <remarks>
    /// 手元に持っている記憶ではなく読み直した記憶へ当てるので、別のインスタンスがその後に保存した記録・訂正・
    /// 削除を巻き戻さない。保存先ディレクトリがなければ作成する。
    /// </remarks>
    /// <param name="change">読み直した記憶への変更。変更したら true を返す。</param>
    /// <returns>変更を当てた記憶(変更がなければ読み直した記憶)。</returns>
    /// <exception cref="JsonException">保存されている内容がJSONとして不正な場合(何も保存しない)。</exception>
    /// <exception cref="IOException">読み書きできない、または他のインスタンスが長く使用中の場合。</exception>
    /// <exception cref="UnauthorizedAccessException">読み書きする権限がない場合。</exception>
    public FormatHistory Update(Func<FormatHistory, bool> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        using (AcquireLock())
        {
            FormatHistory history = Load();
            if (change(history))
            {
                Save(history);
            }

            return history;
        }
    }

    /// <summary>
    /// 記憶を読み込む。破損している場合は .bak へ退避して空の記憶を返す。
    /// </summary>
    /// <param name="corrupted">破損を検知して退避したかどうか。</param>
    /// <returns>読み込んだ記憶。</returns>
    public FormatHistory LoadOrQuarantine(out bool corrupted)
    {
        corrupted = false;
        try
        {
            return Load();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 握りつぶして空から始めると、次の保存で記憶全体が上書きされ復旧できなくなる。
            // 退避して呼び出し側へ知らせる
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Move(FilePath, BackupPath, overwrite: true);
                    corrupted = true;
                    Remember(null);
                }
            }
            catch (Exception moveError) when (
                moveError is IOException or UnauthorizedAccessException)
            {
                // 退避できなくても読み込み自体は空で続行する
            }

            return new FormatHistory();
        }
    }

    /// <summary>
    /// 記憶を保存する。保存先ディレクトリがなければ作成する。
    /// 一時ファイルへ書いてから置換するため、中断しても既存ファイルは壊れない。
    /// </summary>
    /// <param name="history">保存する記憶。</param>
    public void Save(FormatHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var document = new Document { Entries = history.Entries.ToList<FormatHistoryEntry?>() };
        string json = JsonSerializer.Serialize(document, FormatPresetStore.SerializerOptions);
        byte[] content = new UTF8Encoding(false).GetBytes(json);
        AtomicFileWriter.Write(FilePath, stream => stream.Write(content));
        Remember(content);
    }

    /// <summary>保存ファイルの内容。ファイルがなければ null。</summary>
    private byte[]? ReadContent()
    {
        return File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;
    }

    private static FormatHistory Parse(byte[]? content)
    {
        if (content is null)
        {
            return new FormatHistory();
        }

        // 手で編集して BOM 付きで保存されたファイルも読む(ストリームから読んでいたときと同じ)
        ReadOnlySpan<byte> json = content;
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            json = json[3..];
        }

        Document? document = JsonSerializer.Deserialize<Document>(json, FormatPresetStore.SerializerOptions);
        return new FormatHistory(document?.Entries ?? new List<FormatHistoryEntry?>());
    }

    private void Remember(byte[]? content)
    {
        _knownContent = content;
        _known = true;
    }

    private bool IsKnown(byte[]? content)
    {
        return _known && (content is null
            ? _knownContent is null
            : _knownContent is not null && content.AsSpan().SequenceEqual(_knownContent));
    }

    /// <summary>
    /// 同じ記憶ファイルを使う他のプロセス(複数起動した RawAnalyzer)と排他する。名前付きミューテックスは
    /// 取得したスレッドで解放する必要があるため、同じスレッドで同期に使う。
    /// </summary>
    private IDisposable AcquireLock()
    {
        // ミューテックス名に \ は使えないので、正規化したフルパスのハッシュで名前を作る
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(FilePath).ToUpperInvariant()));
        var mutex = new Mutex(initiallyOwned: false, @"Local\RawAnalyzer.FormatHistory." + Convert.ToHexString(hash, 0, 16));
        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                // 保持したまま終了したプロセスがあった。所有権は得ている(書きかけは AtomicFileWriter が置き換えない)
                acquired = true;
            }

            if (!acquired)
            {
                throw new IOException("他の RawAnalyzer がフォーマットの記憶ファイルを使用中のため、読み書きできませんでした。");
            }

            return new LockRelease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>取得したミューテックスを解放して破棄する。</summary>
    private sealed class LockRelease(Mutex mutex) : IDisposable
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

    /// <summary>保存ファイルの中身(将来の書式変更に備えて版を持つ)。</summary>
    private sealed class Document
    {
        /// <summary>書式の版。</summary>
        public int Version { get; set; } = 1;

        /// <summary>記憶(最終使用が新しい順)。</summary>
        public List<FormatHistoryEntry?>? Entries { get; set; }
    }
}
