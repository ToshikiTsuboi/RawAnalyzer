using RawAnalyzer.App.ViewModels;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>左パネルのファイル一覧と絞り込みの結び付き(MainViewModel の純ロジック)。</summary>
public class FileListFilterTests
{
    private static FileEntry Entry(string name) => new(name, @"C:\capture\" + name, false, 100);

    private static MainViewModel CreateWithFiles(params string[] names)
    {
        var vm = new MainViewModel();
        vm.ReplaceFiles(names.Select(Entry));
        return vm;
    }

    [Fact]
    public void ReplaceFiles_WithoutFilter_ShowsAllAndListsExtensionsByFrequency()
    {
        MainViewModel vm = CreateWithFiles("a.raw", "b.RAW", "c.tif", "d.png", "noext");

        Assert.Equal(5, vm.FilteredFiles.Count);
        Assert.Equal("5 件", vm.FileFilterSummary);
        // 多い順、同数は名前順。拡張子の無いファイルは候補に出さない
        Assert.Equal(new[] { "*.raw", "*.png", "*.tif" }, vm.FileExtensionPatterns);
    }

    [Fact]
    public void FileFilterText_NarrowsListAndReportsCounts()
    {
        MainViewModel vm = CreateWithFiles("dark_001.raw", "dark_002.raw", "flat.raw", "scene.tif");

        vm.FileFilterText = "dark*";
        Assert.Equal(new[] { "dark_001.raw", "dark_002.raw" }, vm.FilteredFiles.Select(f => f.Name));
        Assert.Equal("2 / 4 件", vm.FileFilterSummary);
        Assert.False(vm.FileFilterHasError);

        vm.FileFilterText = "*.tif";
        Assert.Equal(new[] { "scene.tif" }, vm.FilteredFiles.Select(f => f.Name));

        vm.ClearFileFilter();
        Assert.Equal(4, vm.FilteredFiles.Count);
        Assert.Equal("4 件", vm.FileFilterSummary);
    }

    [Fact]
    public void FileFilterText_InvalidRegex_KeepsAllVisibleAndFlagsError()
    {
        MainViewModel vm = CreateWithFiles("a.raw", "b.tif");

        vm.FileFilterText = "/a(/";

        Assert.True(vm.FileFilterHasError);
        Assert.NotNull(vm.FileFilterError);
        Assert.Equal(2, vm.FilteredFiles.Count);
        Assert.Equal("2 / 2 件", vm.FileFilterSummary); // 空の条件とは区別して件数を出し、すべて残す

        vm.FileFilterText = "/a/";
        Assert.False(vm.FileFilterHasError);
        Assert.Equal(new[] { "a.raw" }, vm.FilteredFiles.Select(f => f.Name));
    }

    [Fact]
    public void ReplaceFiles_ReappliesCurrentFilterAndKeepsDirectories()
    {
        var vm = new MainViewModel { FileFilterText = ".raw" };

        vm.ReplaceFiles(new[]
        {
            new FileEntry("sub", @"C:\capture\sub", IsDirectory: true),
            Entry("x.raw"),
            Entry("y.tif"),
        });

        Assert.Equal(new[] { "sub", "x.raw" }, vm.FilteredFiles.Select(f => f.Name));
        Assert.Equal("2 / 3 件", vm.FileFilterSummary);
    }

    [Fact]
    public void Files_IncrementalChanges_UpdateFilteredList()
    {
        MainViewModel vm = CreateWithFiles("a.raw");
        vm.FileFilterText = "*.raw";

        vm.Files.Add(Entry("b.raw"));
        vm.Files.Add(Entry("c.tif"));

        Assert.Equal(new[] { "a.raw", "b.raw" }, vm.FilteredFiles.Select(f => f.Name));
        Assert.Equal("2 / 3 件", vm.FileFilterSummary);
    }

    [Fact]
    public void FileFilterText_RegexTimeout_ShowsAllAndFlagsError()
    {
        // 照合の途中で時間切れになった条件は、不正な正規表現と同じくすべて表示して入力欄に理由を示す
        // (以前は時間切れの1件を表示して次のファイルでもまた時間切れまで回った)
        MainViewModel vm = CreateWithFiles(Enumerable.Range(0, 30)
            .Select(i => $"capture_20260930_{i:D6}_long_exposure_frame.raw")
            .ToArray());
        var hasErrorNotified = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.FileFilterHasError))
            {
                hasErrorNotified.Add(vm.FileFilterHasError);
            }
        };

        vm.FileFilterText = @"/^(?=(\w+)+$)/"; // 先読みを含むのでバックトラックで照合する(時間切れがありうる)

        Assert.True(vm.FileFilterHasError);
        Assert.NotNull(vm.FileFilterError);
        Assert.True(hasErrorNotified[^1]); // 入力欄の強調表示に届く
        Assert.Equal(30, vm.FilteredFiles.Count);
        Assert.Equal("30 / 30 件", vm.FileFilterSummary);
    }
}
