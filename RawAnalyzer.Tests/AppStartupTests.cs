using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>起動時の処理(起動引数から開くパスを選ぶ・旧設定フォルダからの引き継ぎ)。</summary>
public class AppStartupTests
{
    [Fact]
    public void ResolveStartupPath_PicksFirstExistingPath()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "a.raw");
        File.WriteAllBytes(file, new byte[8]);
        try
        {
            var fileTarget = new App.StartupTarget(file, IsFolder: false);
            Assert.Equal(fileTarget, App.App.ResolveStartupPath(new[] { file }));

            // 存在しないもの・パスとして不正なものは(例外にせず)飛ばす
            Assert.Equal(fileTarget, App.App.ResolveStartupPath(new[] { @"Z:\nope.raw", file }));
            Assert.Equal(fileTarget, App.App.ResolveStartupPath(new[] { "\0invalid", file }));

            // オプション類は無視する
            Assert.Equal(fileTarget, App.App.ResolveStartupPath(new[] { "--debug", "/x", file }));

            // フォルダかどうかも実在と一緒に(UI スレッドの外で)決める。レビュー 2026-10-03 R2。以前はウィンドウの表示後に
            // UI スレッドの外でもう一度確かめ、確かめる間に利用者が開いたフォルダを起動引数のフォルダで置き換えていた
            Assert.Equal(new App.StartupTarget(directory, IsFolder: true),
                App.App.ResolveStartupPath(new[] { directory }));
            Assert.Null(App.App.ResolveStartupPath(Array.Empty<string>()));
            Assert.Null(App.App.ResolveStartupPath(new[] { @"Z:\nope.raw" }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Migrate_CopiesLegacySettingsEvenIfCurrentFolderExists()
    {
        // ログ出力が先に現行フォルダを作るため、フォルダの有無で判定してはいけない
        string root = Path.Combine(
            Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        string legacy = Path.Combine(root, "RawViewer");
        string current = Path.Combine(root, "RawAnalyzer");

        // 旧フォルダがなければ何もしない(現行フォルダも作らない)
        Assert.Equal(0, App.Services.SettingsMigration.Migrate(legacy, current));
        Assert.False(Directory.Exists(root));

        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(Path.Combine(current, "logs"));
        File.WriteAllText(Path.Combine(legacy, "presets.json"), "{\"p\":1}");
        File.WriteAllText(Path.Combine(legacy, "session.json"), "{\"s\":2}");
        File.WriteAllText(Path.Combine(legacy, "unrelated.txt"), "x");
        try
        {
            int copied = App.Services.SettingsMigration.Migrate(legacy, current);

            Assert.Equal(2, copied);
            Assert.Equal("{\"p\":1}", File.ReadAllText(Path.Combine(current, "presets.json")));
            Assert.Equal("{\"s\":2}", File.ReadAllText(Path.Combine(current, "session.json")));

            // 対象外のファイルは持っていかない
            Assert.False(File.Exists(Path.Combine(current, "unrelated.txt")));

            // 旧フォルダは残す(手動で確認・削除できるように)
            Assert.True(File.Exists(Path.Combine(legacy, "presets.json")));

            // 2回目は何もしない(コピー先に既にあるものは上書きしない)
            Assert.Equal(0, App.Services.SettingsMigration.Migrate(legacy, current));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
