using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.ViewModels;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 左パネルの絞り込み欄(編集可能 ComboBox。候補は今のフォルダの拡張子)と MainViewModel の結び付き。
/// </summary>
/// <remarks>
/// MainWindow.xaml の FileFilterCombo と同じ設定・バインドの ComboBox で確かめる(MainWindow は生成しない)。
/// </remarks>
[Collection("WPF UI")]
public class FileFilterComboTests
{
    private static FileEntry Entry(string name) => new(name, @"C:\capture\" + name, false, 100);

    [Fact]
    public Task PickedPattern_IsKeptInFolderWithoutThatExtension() => WpfTestHost.Run(() =>
    {
        // ドロップダウンで選んだ候補が次のフォルダの候補に無いと、ComboBox は選択を外して入力欄を空にする。
        // 以前はそれがそのまま絞り込み条件になり、黙って解除されたうえ空がセッションに保存された
        var vm = new MainViewModel();
        vm.ReplaceFiles(new[] { Entry("a.tif"), Entry("b.raw") });
        ComboBox combo = CreateFilterCombo(vm);

        combo.SelectedItem = "*.tif"; // ドロップダウンで選ぶ
        Assert.Equal("*.tif", vm.FileFilterText);

        vm.ReplaceFiles(new[] { Entry("c.raw"), Entry("d.raw") });

        Assert.Equal("*.tif", vm.FileFilterText);
        Assert.Equal("*.tif", combo.Text);
        Assert.Empty(vm.FilteredFiles);
        Assert.Equal("0 / 2 件", vm.FileFilterSummary);

        // 候補にある別のフォルダへ戻れば、そのまま絞り込む
        vm.ReplaceFiles(new[] { Entry("a.tif"), Entry("b.raw") });
        Assert.Equal("*.tif", vm.FileFilterText);
        Assert.Equal(new[] { "a.tif" }, vm.FilteredFiles.Select(f => f.Name));
    });

    [Fact]
    public Task PickedThenEditedPattern_IsKeptInFolderWithoutThatExtension() => WpfTestHost.Run(() =>
    {
        // 選んだ後に打ち足した条件も、選んだ候補(選択)が残るので同じく消えていた
        var vm = new MainViewModel();
        vm.ReplaceFiles(new[] { Entry("a.tif"), Entry("b.raw") });
        ComboBox combo = CreateFilterCombo(vm);

        combo.SelectedItem = "*.tif";
        combo.Text = "*.tif dark*";
        Assert.Equal("*.tif dark*", vm.FileFilterText);

        vm.ReplaceFiles(new[] { Entry("dark_001.raw"), Entry("flat.raw") });

        Assert.Equal("*.tif dark*", vm.FileFilterText);
        Assert.Equal(new[] { "dark_001.raw" }, vm.FilteredFiles.Select(f => f.Name));
    });

    [Fact]
    public Task InvalidCondition_ShowsRedBorderAndReasonLikeNumericFields() => WpfTestHost.Run(() =>
    {
        // 不正な条件は、数値入力欄と同じ InputFeedback で赤枠にし、ツールチップを理由に差し替える
        var vm = new MainViewModel();
        vm.ReplaceFiles(new[] { Entry("a.tif"), Entry("b.raw") });
        ComboBox combo = CreateFilterCombo(vm);
        FieldFeedback.AssertValid(combo);
        Assert.Equal(HelpToolTip, combo.ToolTip);

        combo.Text = "/a(/";
        Assert.Equal(vm.FileFilterError, FieldFeedback.AssertInvalid(combo));
        Assert.StartsWith("正規表現が不正です", vm.FileFilterError);

        combo.Text = "/a/";
        FieldFeedback.AssertValid(combo);
        Assert.Equal(HelpToolTip, combo.ToolTip);
    });

    private const string HelpToolTip = "拡張子・ワイルドカード・正規表現で一覧を絞り込みます (Ctrl+F)";

    /// <summary>MainWindow.xaml の FileFilterCombo と同じ設定・バインドの ComboBox を作り、配置する。</summary>
    private static ComboBox CreateFilterCombo(MainViewModel vm)
    {
        var combo = new ComboBox
        {
            IsEditable = true, IsTextSearchEnabled = false, DataContext = vm, ToolTip = HelpToolTip,
        };
        combo.SetBinding(
            ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.FileExtensionPatterns)));
        combo.SetBinding(
            ComboBox.TextProperty,
            new Binding(nameof(MainViewModel.FileFilterText))
            {
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
        combo.SetBinding(InputFeedback.ErrorProperty, new Binding(nameof(MainViewModel.FileFilterError)));
        combo.Measure(new Size(200, 30));
        combo.Arrange(new Rect(0, 0, 200, 30));
        combo.UpdateLayout();
        return combo;
    }
}
