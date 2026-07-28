using System.IO;
using System.Text.Json;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 最近使ったファイルの履歴(%AppData%/RawAnalyzer/recent.json)。
/// </summary>
internal sealed class RecentFilesStore
{
    private const int MaxEntries = 8;
    private readonly string _filePath;

    /// <summary>既定の保存先でストアを生成する。</summary>
    public RecentFilesStore()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RawAnalyzer");
        _filePath = Path.Combine(directory, "recent.json");
    }

    /// <summary>履歴を読み込む(新しい順)。ファイルがなければ空。</summary>
    public List<string> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new List<string>();
            }

            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_filePath))
                ?? new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>パスを履歴の先頭へ追加して保存する。</summary>
    /// <param name="path">追加するファイルパス。</param>
    public void Add(string path)
    {
        List<string> list = Load();
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > MaxEntries)
        {
            list.RemoveRange(MaxEntries, list.Count - MaxEntries);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(list));
        }
        catch (Exception)
        {
            // 履歴保存の失敗は無視する
        }
    }
}
