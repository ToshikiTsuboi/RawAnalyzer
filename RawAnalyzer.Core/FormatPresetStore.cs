using System.Text;
using System.Text.Json;

namespace RawAnalyzer.Core;

/// <summary>
/// RawFormatプリセットの永続化ストア。
/// 既定では %AppData%/RawAnalyzer/presets.json に保存する。
/// </summary>
public sealed class FormatPresetStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
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
    /// プリセットを読み込む。破損している場合は .bak へ退避して空の辞書を返す。
    /// </summary>
    /// <param name="corrupted">破損を検知して退避したかどうか。</param>
    /// <returns>プリセット名からRawFormatへの辞書。</returns>
    public IReadOnlyDictionary<string, RawFormat> LoadOrQuarantine(out bool corrupted)
    {
        corrupted = false;
        try
        {
            return Load();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 握りつぶすと、次に1件保存したときに辞書全体が上書きされ
            // 全プリセットが復旧不能に消える。退避して呼び出し側へ知らせる。
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

            return new Dictionary<string, RawFormat>();
        }
    }

    /// <summary>
    /// プリセットを保存する。保存先ディレクトリがなければ作成する。
    /// 一時ファイルへ書いてから置換するため、中断しても既存ファイルは壊れない。
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
