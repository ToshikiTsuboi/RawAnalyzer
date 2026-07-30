using System.Windows.Input;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

public class KeyboardCommandTests
{
    [Theory]
    [InlineData(Key.P, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+P")]
    [InlineData(Key.O, ModifierKeys.Control, "Ctrl+O")]
    [InlineData(Key.F1, ModifierKeys.None, "F1")]
    [InlineData(Key.D3, ModifierKeys.Control, "Ctrl+3")]
    [InlineData(Key.OemPlus, ModifierKeys.None, "+")]
    [InlineData(Key.OemMinus, ModifierKeys.None, "-")]
    [InlineData(Key.Prior, ModifierKeys.None, "PageUp")]
    [InlineData(Key.Next, ModifierKeys.None, "PageDown")]
    [InlineData(Key.Escape, ModifierKeys.None, "Esc")]
    [InlineData(Key.Space, ModifierKeys.None, "Space")]
    [InlineData(Key.A, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift,
        "Ctrl+Alt+Shift+A")]
    public void FormatGesture_ProducesReadableText(Key key, ModifierKeys modifiers, string expected)
    {
        Assert.Equal(expected, AppCommand.FormatGesture(key, modifiers));
    }

    [Fact]
    public void GestureText_IsEmptyWhenUnassigned()
    {
        var command = new AppCommand
        {
            Id = "x",
            Category = "テスト",
            Title = "無割当",
            Execute = () => { },
        };

        Assert.False(command.HasGesture);
        Assert.Equal("", command.GestureText);
    }

    [Fact]
    public void IsEnabled_DefaultsToTrue()
    {
        var command = new AppCommand
        {
            Id = "x",
            Category = "テスト",
            Title = "既定",
            Execute = () => { },
        };

        Assert.True(command.IsEnabled());

        var disabled = new AppCommand
        {
            Id = "y",
            Category = "テスト",
            Title = "不可",
            Execute = () => { },
            CanExecute = () => false,
        };

        Assert.False(disabled.IsEnabled());
    }

    [Fact]
    public void SearchText_IncludesCategoryTitleAndDescription()
    {
        var command = new AppCommand
        {
            Id = "x",
            Category = "解析",
            Title = "ノイズ測定",
            Description = "σ_temporal と DR",
            Execute = () => { },
        };

        Assert.Contains("解析", command.SearchText);
        Assert.Contains("ノイズ測定", command.SearchText);
        Assert.Contains("DR", command.SearchText);
    }

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
            Assert.Equal(file, App.App.ResolveStartupPath(new[] { file }));

            // 存在しないものは飛ばす
            Assert.Equal(file, App.App.ResolveStartupPath(new[] { @"Z:\nope.raw", file }));

            // オプション類は無視する
            Assert.Equal(file, App.App.ResolveStartupPath(new[] { "--debug", "/x", file }));

            Assert.Equal(directory, App.App.ResolveStartupPath(new[] { directory }));
            Assert.Null(App.App.ResolveStartupPath(Array.Empty<string>()));
            Assert.Null(App.App.ResolveStartupPath(new[] { @"Z:\nope.raw" }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolveStartupPath_InvalidPathDoesNotThrow()
    {
        Assert.Null(App.App.ResolveStartupPath(new[] { "\0invalid" }));
    }

    [Fact]
    public void Migrate_CopiesLegacySettingsEvenIfCurrentFolderExists()
    {
        // ログ出力が先に現行フォルダを作るため、フォルダの有無で判定してはいけない
        string root = Path.Combine(
            Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        string legacy = Path.Combine(root, "RawViewer");
        string current = Path.Combine(root, "RawAnalyzer");
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

    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(2.0, 6.0206)]
    [InlineData(0.5, -6.0206)]
    [InlineData(10.0, 20.0)]
    [InlineData(100.0, 40.0)]
    public void GainDb_MatchesLinearGain(double linear, double db)
    {
        var vm = new App.ViewModels.MainViewModel { Gain = linear };
        Assert.Equal(db, vm.GainDb, 3);

        // dB を設定すると線形倍率へ戻る
        var other = new App.ViewModels.MainViewModel();
        other.GainDb = db;
        Assert.Equal(linear, other.Gain, 6);
    }

    [Fact]
    public void GainDb_IsClampedToSliderRange()
    {
        var vm = new App.ViewModels.MainViewModel();

        vm.GainDb = 999;
        Assert.Equal(vm.MaxGainDb, vm.GainDb, 6);

        vm.GainDb = -999;
        Assert.Equal(vm.MinGainDb, vm.GainDb, 6);
    }

    [Fact]
    public void GainDb_NotifiesWhenLinearGainChanges()
    {
        var vm = new App.ViewModels.MainViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.Gain = 4.0;

        Assert.Contains(nameof(App.ViewModels.MainViewModel.Gain), changed);
        Assert.Contains(nameof(App.ViewModels.MainViewModel.GainDb), changed);
        Assert.Contains(nameof(App.ViewModels.MainViewModel.GainNote), changed);
        Assert.Equal("= ×4", vm.GainNote);
    }

    [Fact]
    public void Migrate_NoLegacyFolder_DoesNothing()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        Assert.Equal(0, App.Services.SettingsMigration.Migrate(
            Path.Combine(root, "RawViewer"), Path.Combine(root, "RawAnalyzer")));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("", "解析 ノイズ測定", true)]
    [InlineData("ノイズ", "解析 ノイズ測定", true)]
    [InlineData("解析 測定", "解析 ノイズ測定", true)]
    [InlineData("測定 解析", "解析 ノイズ測定", true)]   // 語順は問わない
    [InlineData("ノイズ 欠陥", "解析 ノイズ測定", false)] // 全語一致が必要
    [InlineData("hist", "解析 Histogram", true)]        // 大小文字を区別しない
    public void Matches_RequiresAllTerms(string query, string target, bool expected)
    {
        Assert.Equal(expected, CommandPaletteWindow.Matches(query, target));
    }
}
