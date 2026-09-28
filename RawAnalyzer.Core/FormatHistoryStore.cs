using System.Text;
using System.Text.Json;

namespace RawAnalyzer.Core;

/// <summary>
/// サイズ別フォーマット記憶(<see cref="FormatHistory"/>)の永続化ストア。
/// 既定では %AppData%/RawAnalyzer/format-history.json に保存する。
/// </summary>
/// <remarks>
/// JSON の書式はプリセット(<see cref="FormatPresetStore"/>)と同じ設定を使う
/// (列挙型は文字列、HDR 方式の旧名は読み込み時に写す)。
/// </remarks>
public sealed class FormatHistoryStore
{
    /// <summary>保存ファイル名。</summary>
    public const string DefaultFileName = "format-history.json";

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
        if (!File.Exists(FilePath))
        {
            return new FormatHistory();
        }

        using FileStream stream = File.OpenRead(FilePath);
        Document? document = JsonSerializer.Deserialize<Document>(
            stream, FormatPresetStore.SerializerOptions);
        return new FormatHistory(document?.Entries ?? new List<FormatHistoryEntry?>());
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
        AtomicFileWriter.Write(FilePath, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 12, leaveOpen: true);
            writer.Write(json);
        });
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
