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
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RawAnalyzer",
        "logs");

    /// <summary>現在の出力先ログファイルのパス。</summary>
    public static string CurrentFilePath =>
        Path.Combine(LogDirectory, $"rawanalyzer-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>ログの保存フォルダ。</summary>
    public static string DirectoryPath => LogDirectory;

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

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] " +
            $"(T{Environment.CurrentManagedThreadId}) {message}";
        Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                string path = CurrentFilePath;
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
