using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RawAnalyzer.App;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.ViewModels;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class FileListDetailsTests
{
    private static readonly IReadOnlyDictionary<string, RawFormat> NoPathMemory = new Dictionary<string, RawFormat>();

    [Theory]
    [InlineData("demo_scene_a_1920x1080_12bit_rggb.raw", 4147200, "1920×1080 · 12bit")]
    [InlineData("capture.raw", 4147200, "1920×1080 · 12bit")]
    [InlineData("scene_1920x1080.tiff", 4147200, "3.96 MiB")]
    [InlineData("unknown.raw", 123, "123 B")]
    [InlineData("unknown.png", -1, "サイズ不明")]
    public void Describe_UsesOnlyNameAndSize(string name, long length, string expected) =>
        Assert.Equal(expected, FileListDetails.Describe(
            new FileEntry(name, name, Length: length), new FormatHistory(), NoPathMemory));

    [Fact]
    public void Describe_UsesPathMemoryBeforeSizeMemory()
    {
        const string name = "demo_scene_a_1920x1080_12bit_rggb.raw";
        string path = Path.Combine(Path.GetTempPath(), name);
        var entry = new FileEntry(name, path, Length: 4147200);
        var format = new RawFormat { Width = 1920, Height = 1080, BitDepth = 12, Bayer = BayerPattern.Rggb };
        var pathMemory = new Dictionary<string, RawFormat> { [SessionStore.NormalizeKey(path)] = format };
        var sizeMemory = new FormatHistory([new FormatHistoryEntry
        {
            Extension = ".raw", FileSize = entry.Length, Format = format with { Bayer = BayerPattern.Bggr },
        }]);
        Assert.Equal("1920×1080 · 12bit · RGGB", FileListDetails.Describe(entry, sizeMemory, pathMemory));
        Assert.Equal("1920×1080 · 12bit · BGGR", FileListDetails.Describe(entry, sizeMemory, NoPathMemory));
        Assert.Equal("1 B", FileListDetails.Describe(entry with { Length = 1 }, sizeMemory, pathMemory));
    }

    [Fact]
    public void Describe_PrefersRememberedFormat()
    {
        var history = new FormatHistory([new FormatHistoryEntry
        {
            Extension = ".raw", FileSize = 6144,
            Format = new RawFormat { Width = 64, Height = 48, BitDepth = 10, Bayer = BayerPattern.Rggb },
        }]);
        Assert.Equal("64×48 · 10bit · RGGB",
            FileListDetails.Describe(new FileEntry("scene_48x64.raw", "scene_48x64.raw", Length: 6144),
                history, NoPathMemory));
    }
}

[Collection("WPF UI")]
public class FileListVirtualizationTests
{
    [Fact]
    public Task FileList_ShowsPathMemoryAndTracksActualFormatAfterBayerEditAndSequenceMove() => WpfTestHost.Run(async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        string captures = Path.Combine(directory, "captures");
        Directory.CreateDirectory(captures);
        const string name = "demo_scene_a_1920x1080_12bit_rggb.raw";
        string path = Path.Combine(captures, name);
        string nextPath = Path.Combine(captures, "demo_scene_b_1920x1080_12bit_rggb.raw");
        var format = new RawFormat { Width = 1920, Height = 1080, BitDepth = 12, Bayer = BayerPattern.Rggb };
        foreach (string file in new[] { path, nextPath })
        {
            using FileStream stream = File.Create(file);
            stream.SetLength(format.RequiredBytes());
        }

        new SessionStore(directory).Update(session =>
        {
            session.LastFolder = captures;
            SessionStore.TouchFileFormat(session, SessionStore.NormalizeKey(path), format);
            SessionStore.TouchFileFormat(session, SessionStore.NormalizeKey(nextPath), format);
        });
        new RecentFilesStore(directory).Add(path);
        var window = new MainWindow(directory) { ShowActivated = false };
        try
        {
            window.Show();
            var list = (ListBox)window.FindName("FileListBox");
            static async Task WaitUntil(Func<bool> condition)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!condition()) await Task.Delay(10, timeout.Token);
            }

            await WaitUntil(() => FindDetail(list)?.Text == "1920×1080 · 12bit · RGGB"
                && FindDetail(list, 1)?.Text == "1920×1080 · 12bit · RGGB");
            var recent = (ItemsControl)window.FindName("EmptyCanvasRecent");
            var item = (ContentPresenter)recent.ItemContainerGenerator.ContainerFromIndex(0);
            ((Button)VisualTreeHelper.GetChild(item, 0)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var viewport = (ImageViewport)window.FindName("Viewport");
            await WaitUntil(() => viewport.Image is not null);
            var formatSection = (Expander)window.FindName("FormatSection");
            formatSection.IsExpanded = true;
            var bayer = (ComboBox)window.FindName("FmtBayerCombo");
            Assert.Equal(0, bayer.SelectedIndex);
            bayer.SelectedIndex = 1;
            await WaitUntil(() => FindDetail(list)?.Text == "1920×1080 · 12bit · BGGR");
            await WaitUntil(() => FindDetail(list, 1)?.Text == "1920×1080 · 12bit · RGGB");

            ((Button)window.FindName("NextFrameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.Title.Contains(Path.GetFileName(nextPath), StringComparison.Ordinal));
            Assert.Equal(BayerPattern.Bggr, viewport.Image!.Format.Bayer);
            await WaitUntil(() => FindDetail(list, 1)?.Text == "1920×1080 · 12bit · BGGR");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, "file-list-opened-format");
        }
        finally
        {
            window.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    });

    private static TextBlock? FindDetail(ListBox list, int index = 0)
    {
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item)
        {
            return null;
        }

        return FindContentPresenter(item) is { } presenter
            ? presenter.ContentTemplate.FindName("FileDetailsText", presenter) as TextBlock : null;
    }

    private static ContentPresenter? FindContentPresenter(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is ContentPresenter presenter && presenter.ContentTemplate is not null)
            {
                return presenter;
            }

            if (FindContentPresenter(child) is { } nested) return nested;
        }

        return null;
    }

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
