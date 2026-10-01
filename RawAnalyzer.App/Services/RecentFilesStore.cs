using System.IO;
using System.Text.Json;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 最近使ったファイルの履歴(%AppData%/RawAnalyzer/recent.json)。
/// </summary>
/// <remarks>
/// 複数起動したインスタンスも同じファイルを使う。読み書きは他のインスタンスと排他し(<see cref="InterProcessFileLock"/>)、
/// 追加は最新を読み直してから当てて、一時ファイル経由で置き換える(書きかけを読んだ他のインスタンスが空の履歴から
/// 書き直さない)。
/// </remarks>
internal sealed class RecentFilesStore
{
    private const int MaxEntries = 8;
    private readonly string _filePath;

    /// <summary>既定の保存先でストアを生成する。</summary>
    public RecentFilesStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RawAnalyzer"))
    {
    }

    /// <summary>保存先ディレクトリを指定してストアを生成する(テスト用)。</summary>
    /// <param name="directory">recent.json を置くディレクトリ。</param>
    internal RecentFilesStore(string directory)
    {
        _filePath = Path.Combine(directory, "recent.json");
    }

    /// <summary>履歴を読み込む(新しい順)。ファイルがなければ空。</summary>
    public List<string> Load()
    {
        try
        {
            using (InterProcessFileLock.Acquire(_filePath))
            {
                return Read();
            }
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
        try
        {
            // 他のインスタンスの保存と排他し、最新を読み直してから足す(保存中の内容の前の版に足して書き、
            // その保存を消さない)
            using (InterProcessFileLock.Acquire(_filePath))
            {
                List<string> list = Read();
                list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, path);
                if (list.Count > MaxEntries)
                {
                    list.RemoveRange(MaxEntries, list.Count - MaxEntries);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(list);
                AtomicFileWriter.Write(_filePath, stream => stream.Write(json));
            }
        }
        catch (Exception)
        {
            // 履歴保存の失敗は無視する
        }
    }

    /// <summary>履歴を読む(新しい順)。ファイルがない・壊れていれば空。</summary>
    private List<string> Read()
    {
        try
        {
            return File.Exists(_filePath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_filePath)) ?? new List<string>()
                : new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }
}
