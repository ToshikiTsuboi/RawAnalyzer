using System.Diagnostics;
using System.IO;
using System.Text;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 自前の軽量ログ(%AppData%/RawAnalyzer/logs/rawanalyzer-yyyyMMdd.log)。
/// 外部パッケージを使わず、未処理例外の記録を主目的とする。
/// ログ出力自体の失敗は握りつぶし、アプリの動作を妨げない。
/// </summary>
internal static class AppLog
{
    private const long MaxFileBytes = 4L * 1024 * 1024;
    private static readonly object Gate = new();

    /// <summary>既定の保存フォルダ(%AppData%/RawAnalyzer/logs)。アプリ本体は常にここへ書く。</summary>
    internal static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RawAnalyzer",
        "logs");

    // 出力先のフォルダ。アプリ本体は既定のまま変えない(テストだけが RedirectTo で差し替える)。
    // 読み書きは Gate の中で行う
    private static string _directory = DefaultDirectory;

    /// <summary>現在の出力先ログファイルのパス。</summary>
    public static string CurrentFilePath => FilePathIn(DirectoryPath);

    /// <summary>ログの保存フォルダ。</summary>
    public static string DirectoryPath
    {
        get
        {
            lock (Gate)
            {
                return _directory;
            }
        }
    }

    /// <summary>
    /// 出力先のフォルダを差し替える。テストが利用者の実際のログへ書かないようにするためのもので、
    /// アプリ本体からは呼ばない。
    /// </summary>
    /// <remarks>
    /// ログは1ファイルが4MBを超えると今日のログを .1 へ移し、前の .1 を消す。テストから既定の出力先
    /// (%AppData%/RawAnalyzer/logs)へ書くと、テストを繰り返し流すだけで利用者の実際のログを押し出して消してしまう。
    /// テストはテストのコードより先に(モジュール初期化子で)一時フォルダへ向ける。
    /// </remarks>
    /// <param name="directory">出力先のフォルダ。なければ最初の書き込みで作る。</param>
    /// <exception cref="ArgumentException">フォルダが空の場合。</exception>
    internal static void RedirectTo(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        string fullPath = Path.GetFullPath(directory);
        lock (Gate)
        {
            _directory = fullPath;
        }
    }

    /// <summary>情報レベルのメッセージを記録する。</summary>
    /// <param name="message">記録する本文。</param>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>警告レベルのメッセージを記録する。</summary>
    /// <param name="message">記録する本文。</param>
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>例外を発生源つきで記録する。</summary>
    /// <param name="source">発生源(ハンドラ名など)。</param>
    /// <param name="exception">記録する例外。nullの場合は本文のみ。</param>
    public static void Error(string source, Exception? exception)
    {
        Write("ERROR", exception is null ? source : $"{source}{Environment.NewLine}{Describe(exception)}");
    }

    /// <summary>例外を、入れ子の内部例外を含めて多行の文字列に整形する。</summary>
    /// <param name="exception">整形する例外。</param>
    /// <returns>ログ・ダイアログ表示用の文字列。</returns>
    public static string Describe(Exception exception)
    {
        var builder = new StringBuilder();
        AppendException(builder, exception, 0);
        return builder.ToString().TrimEnd();
    }

    private static void AppendException(StringBuilder builder, Exception exception, int depth)
    {
        string indent = new(' ', depth * 2);
        builder.Append(indent).Append(exception.GetType().FullName).Append(": ")
            .AppendLine(exception.Message);
        if (!string.IsNullOrEmpty(exception.StackTrace))
        {
            builder.AppendLine(exception.StackTrace);
        }

        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                builder.Append(indent).AppendLine("--- inner ---");
                AppendException(builder, inner, depth + 1);
            }
        }
        else if (exception.InnerException is not null)
        {
            builder.Append(indent).AppendLine("--- inner ---");
            AppendException(builder, exception.InnerException, depth + 1);
        }
    }

    /// <summary>フォルダ内の今日のログファイルのパス。</summary>
    private static string FilePathIn(string directory) =>
        Path.Combine(directory, $"rawanalyzer-{DateTime.Now:yyyyMMdd}.log");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] " +
            $"(T{Environment.CurrentManagedThreadId}) {message}";
        Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(_directory);
                string path = FilePathIn(_directory);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    // 肥大化したら1世代だけ退避して切り詰める
                    string backup = path + ".1";
                    File.Delete(backup);
                    File.Move(path, backup);
                }

                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // ログ出力の失敗はアプリの動作に影響させない
        }
    }
}
