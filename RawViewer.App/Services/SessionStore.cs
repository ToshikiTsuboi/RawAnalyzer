using System.IO;
using System.Text.Json;
using RawViewer.Core;

namespace RawViewer.App.Services;

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
/// セッション状態の永続化(%AppData%/RawViewer/session.json)。
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
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RawViewer");
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
        // ファイルフォーマット記憶は古いものから間引く(挿入順)
        while (state.FileFormats.Count > MaxFileFormats)
        {
            state.FileFormats.Remove(state.FileFormats.Keys.First());
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(state, Options));
        }
        catch (Exception)
        {
        }
    }

    /// <summary>ファイルフォーマット記憶用のキーを正規化する。</summary>
    /// <param name="path">ファイルパス。</param>
    /// <returns>正規化されたキー。</returns>
    public static string NormalizeKey(string path)
    {
        return Path.GetFullPath(path).ToLowerInvariant();
    }
}
