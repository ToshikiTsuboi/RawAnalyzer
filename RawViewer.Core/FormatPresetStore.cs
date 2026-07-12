using System.Text.Json;

namespace RawViewer.Core;

/// <summary>
/// RawFormatプリセットの永続化ストア。
/// 既定では %AppData%/RawViewer/presets.json に保存する。
/// </summary>
public sealed class FormatPresetStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 既定の保存先(%AppData%/RawViewer)を使うストアを生成する。
    /// </summary>
    public FormatPresetStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RawViewer"))
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
    /// プリセットを保存する。保存先ディレクトリがなければ作成する。
    /// </summary>
    /// <param name="presets">プリセット名からRawFormatへの辞書。</param>
    public void Save(IReadOnlyDictionary<string, RawFormat> presets)
    {
        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(presets, SerializerOptions);
        File.WriteAllText(FilePath, json);
    }
}
