using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// テストが利用者の設定・ログのフォルダ(%AppData%\RawAnalyzer)へ書かないこと。
/// </summary>
public class TestEnvironmentTests
{
    [Fact]
    public void AppLog_DuringTests_WritesOutsideTheUsersFolder()
    {
        // AppLog は1ファイルが4MBを超えると今日のログを .1 へ移し、前の .1 を消す。テストから利用者の実際のログ
        // (%AppData%\RawAnalyzer\logs)へ書くと、テストを繰り返し流すだけで利用者のログを押し出して消していた
        // (TIFF の読み込みなどのテストが1回の実行で数千行を書く)。出力先を確かめてから書く
        // (差し替えられていなければ、ここで失敗して利用者のログには書かない)
        string userFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RawAnalyzer");
        Assert.False(IsUnder(AppLog.DirectoryPath, userFolder), AppLog.DirectoryPath);
        Assert.False(IsUnder(AppLog.CurrentFilePath, userFolder), AppLog.CurrentFilePath);
        Assert.Equal(TestEnvironment.LogDirectory, AppLog.DirectoryPath);

        // 書き込みも差し替えた出力先へ行く(出力先の表示だけでなく)
        string marker = $"{nameof(TestEnvironmentTests)} {Guid.NewGuid():N}";
        AppLog.Info(marker);
        Assert.Contains(
            Directory.EnumerateFiles(AppLog.DirectoryPath), file => ReadShared(file).Contains(marker));

        // アプリ本体の既定の出力先は変えない
        Assert.Equal(Path.Combine(userFolder, "logs"), AppLog.DefaultDirectory);
    }

    private static bool IsUnder(string path, string folder)
    {
        string normalizedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))
            + Path.DirectorySeparatorChar;
        return (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar)
            .StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>並行して走るテストのログ出力と競合しないように読む。</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
