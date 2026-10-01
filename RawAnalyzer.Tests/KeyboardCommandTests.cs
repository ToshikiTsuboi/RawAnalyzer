using System.Windows.Input;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

public class KeyboardCommandTests
{
    [Theory]
    [InlineData(Key.F1, ModifierKeys.None, "F1")]          // 既定分岐(キー名そのまま)
    [InlineData(Key.D3, ModifierKeys.Control, "Ctrl+3")]   // D0〜D9 は数字へ
    [InlineData(Key.Prior, ModifierKeys.None, "PageUp")]   // 変換表
    [InlineData(Key.A, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift,
        "Ctrl+Alt+Shift+A")]                                // 修飾キーの順序
    public void FormatGesture_ProducesReadableText(Key key, ModifierKeys modifiers, string expected)
    {
        Assert.Equal(expected, AppCommand.FormatGesture(key, modifiers));
    }

    [Fact]
    public void GetDisabledReason_IsNullWhileExecutable()
    {
        // 実行できるコマンドには理由を出さない(理由の関数は呼ばない)
        var always = new AppCommand { Id = "a", Category = "c", Title = "t", Execute = () => { } };
        var enabled = new AppCommand
        {
            Id = "b",
            Category = "c",
            Title = "t",
            Execute = () => { },
            CanExecute = () => true,
            DisabledReason = () => throw new InvalidOperationException("実行できるときに理由を作った"),
        };

        Assert.Null(always.GetDisabledReason());
        Assert.Null(enabled.GetDisabledReason());
    }

    [Theory]
    [InlineData("画像を開いていないため実行できません。", "画像を開いていないため実行できません。")]
    [InlineData(null, AppCommand.DefaultDisabledReason)] // 理由を指定していなければ既定の文言
    [InlineData(" ", AppCommand.DefaultDisabledReason)]  // 空の理由では何も分からないので既定の文言
    public void GetDisabledReason_ExplainsWhyItCannotRun(string? reason, string expected)
    {
        // 以前はコマンドパレットで実行できないコマンドを選んで Enter を押しても何も起きなかった
        var command = new AppCommand
        {
            Id = "save",
            Category = "ファイル",
            Title = "保存…",
            Execute = () => { },
            CanExecute = () => false,
            DisabledReason = reason is null ? null : () => reason,
        };

        Assert.False(command.IsEnabled());
        Assert.Equal(expected, command.GetDisabledReason());
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

            // 存在しないもの・パスとして不正なものは(例外にせず)飛ばす
            Assert.Equal(file, App.App.ResolveStartupPath(new[] { @"Z:\nope.raw", file }));
            Assert.Equal(file, App.App.ResolveStartupPath(new[] { "\0invalid", file }));

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

    [Theory]
    [InlineData(10.0, 20.0)]     // 20·log10 (10·log10 なら 10)
    [InlineData(0.5, -6.0206)]
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

    [Theory]
    [InlineData("", "解析 ノイズ測定", true)]
    [InlineData("測定 解析", "解析 ノイズ測定", true)]   // 語順は問わない
    [InlineData("ノイズ 欠陥", "解析 ノイズ測定", false)] // 全語一致が必要
    [InlineData("hist", "解析 Histogram", true)]        // 大小文字を区別しない
    public void Matches_RequiresAllTerms(string query, string target, bool expected)
    {
        Assert.Equal(expected, CommandPaletteWindow.Matches(query, target));
    }

    /// <summary>
    /// IME をオンのまま打った検索語(語の間の全角スペース、全角英字)でも一致する。
    /// 以前は半角スペースでしか区切らず、「ヒストグラム　コピー」は1語として探して何にも一致しなかった。
    /// </summary>
    [Theory]
    [InlineData("ヒストグラム　コピー", "解析 ヒストグラムをクリップボードへコピー", true)]
    [InlineData("ヒストグラム　欠陥", "解析 ヒストグラムをクリップボードへコピー", false)] // 全語一致は従来どおり
    [InlineData("ヒストグラム\tコピー", "解析 ヒストグラムをクリップボードへコピー", true)]
    [InlineData("ＲＯＩ", "ROI ROIを解除", true)]  // 全角英字
    [InlineData("ｒｏｉ", "ROI ROIを解除", true)]  // 全角英字(大小文字も区別しない)
    public void Matches_SplitsOnAnyWhitespace_AndIgnoresWidth(string query, string target, bool expected)
    {
        Assert.Equal(expected, CommandPaletteWindow.Matches(query, target));
    }
}
