using System.IO;
using System.Text;
using System.Text.Json;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// セッション間で保持する状態(ウィンドウ配置・最後のフォルダ・ファイルごとのフォーマット)。
/// </summary>
internal sealed class SessionState
{
    /// <summary>ウィンドウ左端(未保存ならnull)。</summary>
    public double? WindowLeft { get; set; }

    /// <summary>ウィンドウ上端。</summary>
    public double? WindowTop { get; set; }

    /// <summary>ウィンドウ幅。</summary>
    public double? WindowWidth { get; set; }

    /// <summary>ウィンドウ高さ。</summary>
    public double? WindowHeight { get; set; }

    /// <summary>最大化状態だったか。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>最後に表示していたフォルダ。</summary>
    public string? LastFolder { get; set; }

    /// <summary>左パネルの幅。</summary>
    public double? LeftPanelWidth { get; set; }

    /// <summary>右パネルの幅。</summary>
    public double? RightPanelWidth { get; set; }

    /// <summary>左パネルを表示していたか。</summary>
    public bool LeftPanelVisible { get; set; } = true;

    /// <summary>右パネルを表示していたか。</summary>
    public bool RightPanelVisible { get; set; } = true;

    /// <summary>ファイルパス(小文字正規化)→ 最後に使ったRawFormat。</summary>
    public Dictionary<string, RawFormat> FileFormats { get; set; } = new();
}

/// <summary>
/// セッション状態の永続化(%AppData%/RawAnalyzer/session.json)。
/// </summary>
internal sealed class SessionStore
{
    private const int MaxFileFormats = 50;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _filePath;

    /// <summary>既定の保存先でストアを生成する。</summary>
    public SessionStore()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RawAnalyzer");
        _filePath = Path.Combine(directory, "session.json");
    }

    /// <summary>状態を読み込む。ファイルがない・壊れている場合は既定値。</summary>
    public SessionState Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new SessionState();
            }

            return JsonSerializer.Deserialize<SessionState>(
                File.ReadAllText(_filePath), Options) ?? new SessionState();
        }
        catch (Exception)
        {
            return new SessionState();
        }
    }

    /// <summary>状態を保存する。失敗は無視する。</summary>
    /// <param name="state">保存する状態。</param>
    public void Save(SessionState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            // 一時ファイル経由で置換し、中断しても既存のセッションを壊さない
            // (AtomicFileWriter は置換前にディスクへ確定させる)
            string json = JsonSerializer.Serialize(state, Options);
            AtomicFileWriter.Write(_filePath, stream =>
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 12, leaveOpen: true);
                writer.Write(json);
            });
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// ファイルごとのフォーマット記憶を更新し、上限を超えたぶんを最終使用が古い順に間引く。
    /// </summary>
    /// <param name="state">更新する状態。</param>
    /// <param name="key">正規化済みのファイルキー。</param>
    /// <param name="format">記憶するフォーマット。</param>
    /// <remarks>
    /// Dictionary の Remove + Add は解放済みスロットを再利用するため列挙位置が変わらない。
    /// そのまま Keys.First() を消すと「最古」ではなく「最若番スロット」が消え、
    /// 上限到達後は新規ファイルのフォーマットが二度と記憶されなくなる。
    /// ここでは辞書を作り直して列挙順＝最終使用順を保証する。
    /// </remarks>
    public static void TouchFileFormat(SessionState state, string key, RawFormat format)
    {
        var reordered = new Dictionary<string, RawFormat>(state.FileFormats.Count + 1);
        foreach (KeyValuePair<string, RawFormat> entry in state.FileFormats)
        {
            if (!string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                reordered.Add(entry.Key, entry.Value);
            }
        }

        reordered.Add(key, format);
        while (reordered.Count > MaxFileFormats)
        {
            reordered.Remove(reordered.Keys.First());
        }

        state.FileFormats = reordered;
    }

    /// <summary>ファイルフォーマット記憶用のキーを正規化する。</summary>
    /// <param name="path">ファイルパス。</param>
    /// <returns>正規化されたキー。</returns>
    public static string NormalizeKey(string path)
    {
        return Path.GetFullPath(path).ToLowerInvariant();
    }
}
