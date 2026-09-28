using System.Runtime.CompilerServices;
using RawAnalyzer.App.Services;

namespace RawAnalyzer.Tests;

/// <summary>
/// テストの実行環境を整える。テストのコードより先に(モジュール初期化子で)、利用者のフォルダ
/// (%AppData%\RawAnalyzer)へ書く出力先を一時フォルダへ向ける。
/// </summary>
/// <remarks>
/// <para>
/// 対象はログ(<see cref="AppLog"/>)。アプリ本体は既定の出力先へ書き、1ファイルが4MBを超えると
/// 今日のログを .1 へ移して前の .1 を消す。テストから既定の出力先へ書くと、テストを繰り返し流すだけで
/// 利用者の実際のログを押し出して消してしまう。
/// </para>
/// <para>
/// 設定の保存先(プリセット・サイズ別フォーマット記憶)はテストでは保存先を指定して作る。
/// セッション・最近使ったファイルは MainWindow だけが既定の保存先で作るので、テストで MainWindow は生成しない。
/// </para>
/// <para>
/// ログは実行(プロセス)ごとのフォルダへ書く。別の作業フォルダのテストが同時に走っても同じファイルへ
/// 追記し合わない(追記が重なると共有違反で行が落ちる)。前の実行のフォルダは1日を過ぎたら消す。
/// </para>
/// </remarks>
internal static class TestEnvironment
{
    /// <summary>テストのログを置くフォルダ(%TEMP%\RawAnalyzerTests\logs)。実行ごとのフォルダをこの下に作る。</summary>
    internal static readonly string LogRoot = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", "logs");

    /// <summary>この実行のログの出力先。</summary>
    internal static readonly string LogDirectory = Path.Combine(
        LogRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");

    /// <summary>テストのコードより先に、ログの出力先を一時フォルダへ向ける。</summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        AppLog.RedirectTo(LogDirectory);
        DeleteOldRunLogs();
    }

    /// <summary>1日を過ぎた前の実行のログのフォルダを消す(実行ごとに作るので、消さないと積み上がる)。</summary>
    /// <remarks>同時に走っている別の実行のフォルダは新しいので消さない。消せなかったものは次の実行に任せる。</remarks>
    private static void DeleteOldRunLogs()
    {
        DateTime threshold = DateTime.UtcNow.AddDays(-1);
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(LogRoot))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < threshold)
                {
                    try
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // まだ一度もログを書いていない(フォルダがない)など
        }
    }
}
