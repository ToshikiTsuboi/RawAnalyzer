using System.Text;
using System.Text.Json;

namespace RawAnalyzer.Core;

/// <summary>
/// RawFormatプリセットの永続化ストア。
/// 既定では %AppData%/RawAnalyzer/presets.json に保存する。
/// </summary>
/// <remarks>
/// RawAnalyzer は複数起動でき、どのインスタンスも同じファイルを使う。ダイアログを開いたときに読んだプリセットを
/// 丸ごと書き戻すと、別のインスタンスがその後に保存したプリセットが消えるため、変更は <see cref="Update"/> で
/// 「他のインスタンスと排他して最新を読み直し、その変更だけを当てて保存する」。
/// </remarks>
public sealed class FormatPresetStore
{
    /// <summary>
    /// プリセットの JSON 書式(列挙型は文字列、HDR 方式の旧名は読み込み時に写す。いずれも型側の変換器)。
    /// サイズ別フォーマット記憶(<see cref="FormatHistoryStore"/>)も同じ書式で保存する。
    /// </summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 既定の保存先(%AppData%/RawAnalyzer)を使うストアを生成する。
    /// </summary>
    public FormatPresetStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RawAnalyzer"))
    {
    }

    /// <summary>
    /// 保存先ディレクトリを指定してストアを生成する(テスト用途など)。
    /// </summary>
    /// <param name="directory">presets.json を配置するディレクトリ。</param>
    public FormatPresetStore(string directory)
    {
        FilePath = Path.Combine(directory, "presets.json");
    }

    /// <summary>プリセットファイルのフルパス。</summary>
    public string FilePath { get; }

    /// <summary>破損したプリセットファイルの退避先。</summary>
    public string BackupPath => FilePath + ".bak";

    /// <summary>
    /// プリセットを読み込む。ファイルが存在しない場合は空の辞書を返す。
    /// </summary>
    /// <returns>プリセット名からRawFormatへの辞書。</returns>
    /// <exception cref="JsonException">ファイル内容がJSONとして不正な場合。</exception>
    public IReadOnlyDictionary<string, RawFormat> Load()
    {
        if (!File.Exists(FilePath))
        {
            return new Dictionary<string, RawFormat>();
        }

        using FileStream stream = File.OpenRead(FilePath);
        return JsonSerializer.Deserialize<Dictionary<string, RawFormat>>(stream, SerializerOptions)
            ?? new Dictionary<string, RawFormat>();
    }

    /// <summary>
    /// 他のインスタンスと排他しながらプリセットを読み込む。JSON として壊れている場合は .bak へ退避して空の辞書を返す。
    /// </summary>
    /// <remarks>
    /// ロック・共有違反・アクセス拒否などで読めないだけのときは、正常なファイルかもしれないので退避しない
    /// (例外を投げる)。保存は <see cref="Update"/> で読み直してから行うので、空の一覧で上書きしない。
    /// </remarks>
    /// <param name="corrupted">破損を検知して退避したかどうか。</param>
    /// <returns>プリセット名からRawFormatへの辞書。</returns>
    /// <exception cref="IOException">読めない、または他のインスタンスが長く使用中の場合(退避しない)。</exception>
    /// <exception cref="UnauthorizedAccessException">読む権限がない場合(退避しない)。</exception>
    public IReadOnlyDictionary<string, RawFormat> LoadOrQuarantine(out bool corrupted)
    {
        corrupted = false;
        using (InterProcessFileLock.Acquire(FilePath))
        {
            try
            {
                return Load();
            }
            catch (JsonException)
            {
                // 握りつぶすと、次に1件保存したときに辞書全体が上書きされ
                // 全プリセットが復旧不能に消える。退避して呼び出し側へ知らせる。
                corrupted = TryQuarantine(FilePath, BackupPath);
                return new Dictionary<string, RawFormat>();
            }
        }
    }

    /// <summary>
    /// JSON として壊れた保存ファイルを .bak へ退避する(サイズ別フォーマット記憶も使う)。
    /// 退避できなくても読み込み自体は空で続行する。
    /// </summary>
    /// <param name="filePath">壊れた保存ファイル。</param>
    /// <param name="backupPath">退避先。</param>
    /// <returns>退避したら true。</returns>
    internal static bool TryQuarantine(string filePath, string backupPath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Move(filePath, backupPath, overwrite: true);
                return true;
            }
        }
        catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    /// <summary>
    /// 他のインスタンスと排他しながら、保存されている最新のプリセットを読み直して変更を当て、変更したときだけ保存する。
    /// </summary>
    /// <remarks>
    /// 手元に持っているプリセットではなく読み直したプリセットへ当てるので、別のインスタンスがその後に保存した
    /// プリセットを巻き戻さない。保存先ディレクトリがなければ作成する。
    /// </remarks>
    /// <param name="change">読み直したプリセットへの変更。変更したら true を返す。</param>
    /// <returns>変更を当てたプリセット(変更がなければ読み直したプリセット)。</returns>
    /// <exception cref="JsonException">保存されている内容がJSONとして不正な場合(何も保存しない)。</exception>
    /// <exception cref="IOException">読み書きできない、または他のインスタンスが長く使用中の場合。</exception>
    /// <exception cref="UnauthorizedAccessException">読み書きする権限がない場合。</exception>
    public IReadOnlyDictionary<string, RawFormat> Update(Func<Dictionary<string, RawFormat>, bool> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        using (InterProcessFileLock.Acquire(FilePath))
        {
            var presets = new Dictionary<string, RawFormat>(Load());
            if (change(presets))
            {
                Save(presets);
            }

            return presets;
        }
    }

    /// <summary>
    /// プリセットを保存する。保存先ディレクトリがなければ作成する。
    /// 一時ファイルへ書いてから置換するため、中断しても既存ファイルは壊れない。
    /// ファイル全体を置き換えるので、他のインスタンスの保存を巻き戻さないよう、変更は <see cref="Update"/> で行う。
    /// </summary>
    /// <param name="presets">プリセット名からRawFormatへの辞書。</param>
    public void Save(IReadOnlyDictionary<string, RawFormat> presets)
    {
        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(presets, SerializerOptions);

        // File.WriteAllText は truncate してから書くため、中断すると
        // 0バイトや途中で切れたJSONが残る。一時ファイル経由で置換する
        // (AtomicFileWriter は置換前にディスクへ確定させる)
        AtomicFileWriter.Write(FilePath, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 12, leaveOpen: true);
            writer.Write(json);
        });
    }
}
