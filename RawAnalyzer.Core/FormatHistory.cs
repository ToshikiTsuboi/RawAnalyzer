namespace RawAnalyzer.Core;

/// <summary>
/// サイズ別フォーマット記憶の1件。ファイルサイズと拡張子の組(キー)ごとに、
/// そのサイズのファイルを開けたフォーマットを記録する。
/// </summary>
public sealed record FormatHistoryEntry
{
    /// <summary>ファイルサイズ(バイト)。</summary>
    public required long FileSize { get; init; }

    /// <summary>拡張子(小文字・ドット付き。拡張子がなければ空文字列)。</summary>
    public required string Extension { get; init; }

    /// <summary>そのサイズのファイルを開けたフォーマット(一式)。</summary>
    public required RawFormat Format { get; init; }

    /// <summary>最終使用日時(UTC)。</summary>
    public DateTime LastUsedUtc { get; init; }

    /// <summary>
    /// 同じキーのファイルを次回からダイアログなしでこのフォーマットで開くか(自動適用フラグ)。
    /// オフの記憶は候補には出るが、自動では使わない。
    /// </summary>
    public bool AutoOpen { get; init; } = true;
}

/// <summary>
/// サイズ別フォーマット記憶(<see cref="FormatHistory"/>)の読み取り専用の見え方。
/// </summary>
/// <remarks>
/// 保存までを受け持つ側(アプリの記憶)が、書き換えると保存されない記憶をそのまま見せないために使う。
/// </remarks>
public interface IReadOnlyFormatHistory
{
    /// <summary>すべての記憶(最終使用が新しい順)。</summary>
    IReadOnlyList<FormatHistoryEntry> Entries { get; }

    /// <summary>キーが一致する記憶を返す(最終使用が新しい順)。</summary>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <param name="extension">拡張子(正規化していなくてよい)。</param>
    /// <returns>一致した記憶。なければ空。</returns>
    IReadOnlyList<FormatHistoryEntry> Find(long fileSize, string extension);

    /// <summary>キーとフォーマットが一致する記憶を返す。</summary>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <param name="extension">拡張子(正規化していなくてよい)。</param>
    /// <param name="format">フォーマット。</param>
    /// <returns>一致した記憶。なければnull。</returns>
    FormatHistoryEntry? Find(long fileSize, string extension, RawFormat format);

    /// <summary>
    /// ダイアログを出さずに開くフォーマットを返す。キーの記憶のうち自動適用がオンのものが
    /// ちょうど1つのときだけそのフォーマットを返す(0件・2件以上なら null)。
    /// </summary>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <param name="extension">拡張子(正規化していなくてよい)。</param>
    /// <returns>自動で開くフォーマット。決められなければ null。</returns>
    RawFormat? FindAutoOpenFormat(long fileSize, string extension);
}

/// <summary>
/// ファイルサイズと拡張子をキーにした Raw フォーマットの記憶。
/// 別のファイルを開くときに、同じサイズのファイルを開いたときのフォーマットから推定するために使う。
/// </summary>
/// <remarks>
/// <para>
/// 同じキーに別の解釈(異なる <see cref="RawFormat"/>)を記録すると両方を残す。同じ内容なら
/// 最終使用日時(と自動適用フラグ)を更新する。全体の上限は <see cref="MaxEntries"/> 件で、
/// 超えたら最終使用が古い順に消す。記憶は最終使用が新しい順に並べて持つ。
/// </para>
/// <para>
/// 自動で開くのは、キーの記憶のうち自動適用がオンのものがちょうど1つのときだけ
/// (<see cref="FindAutoOpenFormat"/>)。ダイアログで自動適用をオンにして確定した解釈は、
/// 同じキーの他の解釈の自動適用をオフにする(「次回からこの形式で開く」を満たすため)。
/// </para>
/// <para>UI スレッドから使う前提で、スレッドセーフではない。</para>
/// </remarks>
public sealed class FormatHistory : IReadOnlyFormatHistory
{
    /// <summary>記憶する件数の上限(全キー合計)。</summary>
    public const int MaxEntries = 200;

    // 最終使用が新しい順(先頭が最新)
    private readonly List<FormatHistoryEntry> _entries = new();

    /// <summary>空の記憶を生成する。</summary>
    public FormatHistory()
    {
    }

    /// <summary>
    /// 読み込んだ記憶から生成する。使えない項目(サイズが負・フォーマットが不正・
    /// そのサイズでは開けないフォーマット)は捨て、拡張子を正規化し、最終使用が新しい順に並べ、
    /// 同じキー・同じフォーマットの重複は新しい方だけを残し、上限を超えたぶんは古い方から捨てる。
    /// </summary>
    /// <param name="entries">読み込んだ記憶(null の項目は捨てる)。</param>
    public FormatHistory(IEnumerable<FormatHistoryEntry?> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // OrderByDescending は安定なので、同じ日時ならファイル上の順(新しい順で保存している)を保つ
        foreach (FormatHistoryEntry entry in entries
            .OfType<FormatHistoryEntry>()
            .OrderByDescending(e => e.LastUsedUtc))
        {
            if (_entries.Count >= MaxEntries)
            {
                break;
            }

            if (entry.Format is null || entry.FileSize < 0
                || !CanOpen(entry.Format, entry.FileSize))
            {
                continue;
            }

            string extension = NormalizeExtension(entry.Extension);
            if (IndexOf(entry.FileSize, extension, entry.Format) >= 0)
            {
                continue;
            }

            _entries.Add(entry with { Extension = extension });
        }
    }

    /// <summary>すべての記憶(最終使用が新しい順)。</summary>
    public IReadOnlyList<FormatHistoryEntry> Entries => _entries;

    /// <summary>
    /// 拡張子をキー用に正規化する(前後の空白を除き、小文字・ドット付きにする)。
    /// </summary>
    /// <param name="extension">拡張子(".RAW"・"raw" など)。null・空なら空文字列。</param>
    /// <returns>正規化した拡張子。</returns>
    public static string NormalizeExtension(string? extension)
    {
        string trimmed = (extension ?? "").Trim().ToLowerInvariant();
        return trimmed.Length == 0 || trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }

    /// <summary>ファイルパスからキー用の拡張子を取り出す。</summary>
    /// <param name="path">ファイルパスまたはファイル名。</param>
    /// <returns>正規化した拡張子(拡張子がなければ空文字列)。</returns>
    public static string ExtensionOf(string path)
    {
        return NormalizeExtension(Path.GetExtension(path));
    }

    /// <summary>キーが一致する記憶を返す(最終使用が新しい順)。</summary>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <param name="extension">拡張子(正規化していなくてよい)。</param>
    /// <returns>一致した記憶。なければ空。</returns>
    public IReadOnlyList<FormatHistoryEntry> Find(long fileSize, string extension)
    {
        string key = NormalizeExtension(extension);
        return _entries.Where(e => IsKey(e, fileSize, key)).ToList();
    }

    /// <summary>キーとフォーマットが一致する記憶を返す。</summary>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <param name="extension">拡張子(正規化していなくてよい)。</param>
    /// <param name="format">フォーマット。</param>
    /// <returns>一致した記憶。なければnull。</returns>
    public FormatHistoryEntry? Find(long fileSize, string extension, RawFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        int index = IndexOf(fileSize, NormalizeExtension(extension), format);
        return index >= 0 ? _entries[index] : null;
    }

    /// <summary>
    /// ダイアログを出さずに開くフォーマットを返す。キーの記憶のうち自動適用がオンのものが
    /// ちょうど1つのときだけそのフォーマットを返す(0件・2件以上なら null)。
    /// </summary>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <param name="extension">拡張子(正規化していなくてよい)。</param>
    /// <returns>自動で開くフォーマット。決められなければ null。</returns>
    public RawFormat? FindAutoOpenFormat(long fileSize, string extension)
    {
        string key = NormalizeExtension(extension);
        FormatHistoryEntry? found = null;
        foreach (FormatHistoryEntry entry in _entries)
        {
            if (!IsKey(entry, fileSize, key) || !entry.AutoOpen)
            {
                continue;
            }

            if (found is not null)
            {
                return null; // 自動適用の候補が複数ある(どちらか決められない)
            }

            found = entry;
        }

        return found?.Format;
    }

    /// <summary>
    /// 読み込みに成功したフォーマットを記録する。同じキー・同じフォーマットの記憶があれば
    /// 最終使用日時と自動適用フラグを更新し、なければ追加する(同じキーの別の解釈は残す)。
    /// </summary>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <param name="extension">拡張子(正規化していなくてよい)。</param>
    /// <param name="format">読み込めたフォーマット。</param>
    /// <param name="autoOpen">
    /// 自動適用フラグ。ダイアログで確定したときはその選択を渡す(true なら同じキーの他の解釈の
    /// 自動適用をオフにする)。選択がない(記憶から開いた)ときは null: 既存の記憶ならフラグを保ち、
    /// 新しい記憶はそのキーに記憶がまだないときだけオンにする(既に記憶のあるキーで自動で開く
    /// 形式は、ダイアログでの確定で決める)。
    /// </param>
    /// <param name="lastUsedUtc">最終使用日時(UTC)。</param>
    /// <exception cref="ArgumentOutOfRangeException">ファイルサイズが負の場合。</exception>
    /// <exception cref="ArgumentException">
    /// フォーマットがそのサイズのファイルでは開けない場合(<see cref="CanOpen"/>)。
    /// </exception>
    public void Record(
        long fileSize, string extension, RawFormat format, bool? autoOpen, DateTime lastUsedUtc)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        ThrowIfCannotOpen(format, fileSize);
        string key = NormalizeExtension(extension);
        int index = IndexOf(fileSize, key, format);
        bool flag = autoOpen
            ?? (index >= 0 ? _entries[index].AutoOpen : !_entries.Any(e => IsKey(e, fileSize, key)));
        Upsert(fileSize, key, format, flag, exclusive: autoOpen == true, lastUsedUtc);
    }

    /// <summary>
    /// 記憶のフォーマットを訂正する。キーに <paramref name="original"/> の記憶があれば、それを
    /// <paramref name="replacement"/> で置き換える(誤って自動で開いたものを直すと、次からは直した方で開く)。
    /// </summary>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <param name="extension">拡張子(正規化していなくてよい)。</param>
    /// <param name="original">訂正前のフォーマット。</param>
    /// <param name="replacement">訂正後のフォーマット。</param>
    /// <param name="autoOpen">
    /// 自動適用フラグ(ダイアログで確定したときの選択。true なら同じキーの他の解釈の自動適用をオフに
    /// する)。null なら訂正前の記憶のフラグを引き継ぐ。
    /// </param>
    /// <param name="lastUsedUtc">最終使用日時(UTC)。</param>
    /// <returns>置き換えたら true。<paramref name="original"/> の記憶がなければ何もせず false。</returns>
    /// <exception cref="ArgumentException">
    /// 訂正後のフォーマットがそのサイズのファイルでは開けない場合(<see cref="CanOpen"/>)。
    /// </exception>
    public bool Replace(
        long fileSize, string extension, RawFormat original, RawFormat replacement,
        bool? autoOpen, DateTime lastUsedUtc)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(replacement);
        ThrowIfCannotOpen(replacement, fileSize);
        string key = NormalizeExtension(extension);
        int index = IndexOf(fileSize, key, original);
        if (index < 0)
        {
            return false;
        }

        bool inherited = _entries[index].AutoOpen;
        _entries.RemoveAt(index);
        Upsert(fileSize, key, replacement, autoOpen ?? inherited, exclusive: autoOpen == true,
            lastUsedUtc);
        return true;
    }

    /// <summary>キーの記憶をすべて削除する。</summary>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <param name="extension">拡張子(正規化していなくてよい)。</param>
    /// <returns>削除した件数。</returns>
    public int Remove(long fileSize, string extension)
    {
        string key = NormalizeExtension(extension);
        return _entries.RemoveAll(e => IsKey(e, fileSize, key));
    }

    /// <summary>
    /// フォーマットがそのサイズのファイルで開けるか(値が妥当で、必要なバイト数がサイズ以下)。
    /// </summary>
    /// <param name="format">フォーマット。</param>
    /// <param name="fileSize">ファイルサイズ(バイト)。</param>
    /// <returns>開けるなら true。</returns>
    public static bool CanOpen(RawFormat format, long fileSize)
    {
        ArgumentNullException.ThrowIfNull(format);
        try
        {
            format.Validate();
            return format.RequiredBytes() <= fileSize;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>記憶はどれもそのサイズで開けるものに限る(自動で開くときにそのまま使えるように)。</summary>
    private static void ThrowIfCannotOpen(RawFormat format, long fileSize)
    {
        if (!CanOpen(format, fileSize))
        {
            throw new ArgumentException(
                $"フォーマット({format.Width}×{format.Height})はサイズ {fileSize} バイトのファイルでは開けません。",
                nameof(format));
        }
    }

    private static bool IsKey(FormatHistoryEntry entry, long fileSize, string normalizedExtension)
    {
        return entry.FileSize == fileSize
            && string.Equals(entry.Extension, normalizedExtension, StringComparison.Ordinal);
    }

    private int IndexOf(long fileSize, string normalizedExtension, RawFormat format)
    {
        return _entries.FindIndex(
            e => IsKey(e, fileSize, normalizedExtension) && e.Format == format);
    }

    private void Upsert(
        long fileSize, string normalizedExtension, RawFormat format, bool autoOpen, bool exclusive,
        DateTime lastUsedUtc)
    {
        int index = IndexOf(fileSize, normalizedExtension, format);
        if (index >= 0)
        {
            _entries.RemoveAt(index);
        }

        if (exclusive)
        {
            // 「次回からこの形式で開く」を満たすため、同じキーの他の解釈は候補だけに戻す
            for (int i = 0; i < _entries.Count; i++)
            {
                if (IsKey(_entries[i], fileSize, normalizedExtension) && _entries[i].AutoOpen)
                {
                    _entries[i] = _entries[i] with { AutoOpen = false };
                }
            }
        }

        _entries.Insert(0, new FormatHistoryEntry
        {
            FileSize = fileSize,
            Extension = normalizedExtension,
            Format = format,
            LastUsedUtc = lastUsedUtc,
            AutoOpen = autoOpen,
        });

        // 先頭に入れたばかりの記憶は消さない(上限は1以上)
        while (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }
    }
}
