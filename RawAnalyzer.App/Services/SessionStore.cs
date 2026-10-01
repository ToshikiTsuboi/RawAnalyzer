using System.IO;
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

    /// <summary>ファイル一覧の絞り込み条件(空なら絞り込みなし)。</summary>
    public string? FileFilter { get; set; }

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
/// <remarks>
/// <para>
/// RawAnalyzer は複数起動でき、どのインスタンスも同じファイルを使う。起動時に読んだ状態を丸ごと書き戻すと、
/// 別のインスタンスで記憶した・F2 で直したファイルごとのフォーマットや開いたフォルダが古い内容で巻き戻るため、
/// 変更は <see cref="Update"/> で「他のインスタンスと排他して最新を読み直し、その変更だけを当てて保存する」。
/// 他のインスタンスの保存は <see cref="Current"/> を読むたびに取り込む。ウィンドウ配置のように閉じるときに書く
/// 項目は、最後に閉じたインスタンスのものが残る。
/// </para>
/// <para>
/// 保存の失敗は従来どおり知らせない。変更は手元の状態に当てておき、次の変更のときに最新の状態へ当て直して保存する。
/// スレッドセーフではない(UI スレッドから使う)。
/// </para>
/// </remarks>
internal sealed class SessionStore
{
    private const int MaxFileFormats = 50;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _filePath;

    /// <summary>保存できていない変更(次の変更で最新の状態へ当て直して保存する)。</summary>
    private readonly List<Action<SessionState>> _unsaved = new();

    /// <summary>最後に読んだ・保存した状態(保存できていない変更も当ててある)。</summary>
    private SessionState _state = new();

    /// <summary>このストアが最後に読んだ・保存したファイルの内容(ファイルがなければ null)。</summary>
    private byte[]? _knownContent;

    /// <summary><see cref="_knownContent"/> が有効か(まだ一度も読み書きしていなければ false)。</summary>
    private bool _known;

    /// <summary>既定の保存先でストアを生成する。</summary>
    public SessionStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RawAnalyzer"))
    {
    }

    /// <summary>保存先ディレクトリを指定してストアを生成する(テスト用)。</summary>
    /// <param name="directory">session.json を置くディレクトリ。</param>
    internal SessionStore(string directory)
    {
        _filePath = Path.Combine(directory, "session.json");
    }

    /// <summary>
    /// 最新の状態。別のインスタンスが保存していれば読み直してから返す(保存できていない変更は当て直す)。
    /// 変更は <see cref="Update"/> で行う(返した状態を直接書き換えても保存されない)。
    /// </summary>
    public SessionState Current
    {
        get
        {
            Refresh();
            return _state;
        }
    }

    /// <summary>状態を読み込む(起動時)。ファイルがない・壊れている・読めない場合は既定値。</summary>
    /// <returns>読み込んだ状態。</returns>
    public SessionState Load()
    {
        _state = new SessionState();
        try
        {
            byte[]? content;
            using (InterProcessFileLock.Acquire(_filePath))
            {
                content = ReadContent();
            }

            // 壊れた内容も「読んだ内容」として覚え、Current で毎回解釈し直さない
            Remember(content);
            _state = Parse(content) ?? new SessionState();
        }
        catch (Exception)
        {
            // 従来どおり既定値で始める(起動を止めない)
        }

        return _state;
    }

    /// <summary>
    /// 他のインスタンスと排他しながら、保存されている最新の状態を読み直して変更を当てて保存する。失敗は知らせない。
    /// </summary>
    /// <remarks>
    /// 起動時に読んだ状態ではなく読み直した状態へ当てるので、別のインスタンスがその後に保存した内容を巻き戻さない。
    /// 保存できなかった変更は次の変更のときに当て直すので、<paramref name="change"/> には呼び出した時点の値を
    /// 取り込んでおく(後で変わるプロパティを読まない)。
    /// </remarks>
    /// <param name="change">状態への変更。</param>
    public void Update(Action<SessionState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        _unsaved.Add(change);
        try
        {
            using (InterProcessFileLock.Acquire(_filePath))
            {
                // 保存されている状態が(このアプリ以外で)壊されていたら、読み直せないので従来どおり手元の状態で置き換える
                SessionState latest = Parse(ReadContent()) ?? _state;
                foreach (Action<SessionState> pending in _unsaved)
                {
                    pending(latest);
                }

                Remember(Write(latest));
                _state = latest;
                _unsaved.Clear();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 他のインスタンスが長く使用中・書けないなど。手元の状態に当てておき、次の変更で当て直して保存する
            change(_state);
        }
        catch (Exception)
        {
            // JSON に書けない値(非有限の数)など、当て直しても保存できない変更は捨てる(従来どおり知らせない。
            // 残すと以後の保存がすべて失敗する)
            _unsaved.Remove(change);
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

    /// <summary>
    /// 状態を保存する。一時ファイル経由で置換し、中断しても既存のセッションを壊さない
    /// (AtomicFileWriter は置換前にディスクへ確定させる)。
    /// </summary>
    /// <returns>書いた内容。</returns>
    private byte[] Write(SessionState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        AtomicFileWriter.Write(_filePath, stream => stream.Write(content));
        return content;
    }

    /// <summary>別のインスタンスが保存した状態を取り込む(保存できていない変更は当て直す)。</summary>
    private void Refresh()
    {
        byte[]? content;
        try
        {
            using (InterProcessFileLock.Acquire(_filePath))
            {
                content = ReadContent();
            }
        }
        catch (Exception)
        {
            return; // 読めなければ手元の状態を使い続ける(ファイルを開く操作を止めない)
        }

        if (IsKnown(content))
        {
            return;
        }

        // 壊れていれば手元の状態を使い続ける(同じ内容は解釈し直さない)
        Remember(content);
        if (Parse(content) is not { } latest)
        {
            return;
        }

        foreach (Action<SessionState> pending in _unsaved)
        {
            pending(latest);
        }

        _state = latest;
    }

    /// <summary>保存ファイルの内容。ファイルがなければ null。</summary>
    private byte[]? ReadContent()
    {
        return File.Exists(_filePath) ? File.ReadAllBytes(_filePath) : null;
    }

    /// <summary>保存ファイルの内容を解釈する。ファイルがなければ既定値、壊れていれば null。</summary>
    private static SessionState? Parse(byte[]? content)
    {
        if (content is null)
        {
            return new SessionState();
        }

        // 手で編集して BOM 付きで保存されたファイルも読む(文字列で読んでいたときと同じ)
        ReadOnlySpan<byte> json = content;
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            json = json[3..];
        }

        try
        {
            return JsonSerializer.Deserialize<SessionState>(json, Options) ?? new SessionState();
        }
        catch (JsonException)
        {
            return null;
        }
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
}
