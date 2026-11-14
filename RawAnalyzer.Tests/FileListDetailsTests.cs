using System.Windows.Controls;
using System.Windows.Threading;
using RawAnalyzer.App;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class FileListDetailsTests
{
    [Theory]
    [InlineData("scene_1920x1080.raw", 4147200, "1920×1080 · 12bit · モノクロ")]
    [InlineData("scene_1920x1080.tiff", 4147200, "3.96 MiB")]
    [InlineData("unknown.raw", 123, "123 B")]
    [InlineData("unknown.png", -1, "サイズ不明")]
    public void Describe_UsesOnlyNameAndSize(string name, long length, string expected) =>
        Assert.Equal(expected, FileListDetails.Describe(new FileEntry(name, name, Length: length), new FormatHistory()));

    [Fact]
    public void Describe_PrefersRememberedFormat()
    {
        var history = new FormatHistory([new FormatHistoryEntry
        {
            Extension = ".raw", FileSize = 6144,
            Format = new RawFormat { Width = 64, Height = 48, BitDepth = 10, Bayer = BayerPattern.Rggb },
        }]);
        Assert.Equal("64×48 · 10bit · RGGB",
            FileListDetails.Describe(new FileEntry("scene_48x64.raw", "scene_48x64.raw", Length: 6144), history));
    }
}

[Collection("WPF UI")]
public class FileListVirtualizationTests
{
    [Fact]
    public Task FileList_RealizesOnlyViewportAndCacheFor5000Entries() => WpfTestHost.Run(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        var window = new MainWindow(directory) { ShowActivated = false };
        try
        {
            var list = (ListBox)window.FindName("FileListBox");
            var vm = (MainViewModel)window.DataContext;
            vm.ReplaceFiles(Enumerable.Range(0, 5000).Select(i =>
                new FileEntry($"capture_with_a_long_file_name_{i:D4}.png", $"capture_{i:D4}.png", Length: i)));
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(5000, list.Items.Count);
            int CountContainers() => Enumerable.Range(0, list.Items.Count)
                .Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem);
            Assert.InRange(CountContainers(), 1, 120);
            list.ScrollIntoView(list.Items[4999]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(4999));
            Assert.InRange(CountContainers(), 1, 120);
            WpfTestHost.CaptureIfRequested((System.Windows.FrameworkElement)window.Content, "file-list-virtualized");
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    });
}
