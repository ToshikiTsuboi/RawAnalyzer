using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RawAnalyzer.App;
using RawAnalyzer.App.Compare;
using RawAnalyzer.App.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// インポートダイアログの候補一覧・「次回からこの形式で開く」・「このサイズの記憶を削除」。
/// </summary>
[Collection("WPF UI")]
public class RawImportDialogTests : IDisposable
{
    private const long FileSize = 640 * 480 * 2;
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _directory;

    public RawImportDialogTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static RawFormat Fmt(int width, int height, int bitDepth = 12,
        BayerPattern bayer = BayerPattern.None)
    {
        return new RawFormat { Width = width, Height = height, BitDepth = bitDepth, Bayer = bayer };
    }

    private string CreateFile(string name, long size)
    {
        string path = Path.Combine(_directory, name);
        using var stream = File.Create(path);
        stream.SetLength(size);
        return path;
    }

    private FormatMemory CreateMemory()
    {
        return new FormatMemory(
            new FormatHistoryStore(Path.Combine(_directory, "settings")), _ => { }, () => T0);
    }

    private RawImportDialog CreateDialog(
        string path, FormatMemory? memory, RawFormat? initial = null, RawFormat? displayed = null,
        long? size = null)
    {
        // サイズは呼び出し側が UI スレッドの外で取って渡す(テストではここで取る)
        return new RawImportDialog(
            path, size ?? new FileInfo(path).Length, new FormatPresetStore(Path.Combine(_directory, "settings")),
            initial, memory, displayed);
    }

    [Fact]
    public Task InputFields_HaveAccessibleNamesAndFollowVisualTabOrder() => WpfTestHost.Run(async () =>
    {
        string path = CreateFile("settings.raw", FileSize);
        FormatMemory memory = CreateMemory();
        RawFormat format = Fmt(640, 480) with
        {
            Hdr = HdrMode.LineInterleaved,
            HdrStages = 2,
            Endianness = Endianness.Big,
        };
        memory.RememberLoaded(path, FileSize, format, autoOpen: true);
        RawImportDialog dialog = CreateDialog(path, memory, format);
        try
        {
            dialog.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var fields = LogicalInputs(dialog).ToArray();
            Assert.Equal(16, fields.Length);
            Assert.All(fields, field => Assert.True(
                !string.IsNullOrWhiteSpace(AutomationProperties.GetName(field))
                    || AutomationProperties.GetLabeledBy(field) is not null, field.Name));
            var details = Find<Expander>(dialog, "DetailsExpander");
            var header = Assert.IsType<ToggleButton>(details.Template.FindName("HeaderSite", details));
            Assert.Equal("詳細", new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(header).GetName());

            string[] expected =
            [
                "CandidateList", "WidthBox", "HeightBox", "BitDepthCombo", "BayerCombo", "HeaderSite",
                "PackingCombo", "EndianCombo", "HeaderOffsetBox", "FrameCountBox", "HdrCombo", "HdrStagesCombo",
                "ExposureRatioBox", "HdrLineBlockBox", "HdrRowOffsetBox", "GuessButton", "AutoOpenCheck",
                "ForgetSizeButton", "PresetCombo", "SavePresetButton",
            ];
            Control width = Find<Control>(dialog, "WidthBox");
            Assert.True(width.Focus());
            Assert.True(width.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)));
            var traversed = new List<string>();
            for (int step = 0; step < expected.Length; step++)
            {
                var focused = Assert.IsAssignableFrom<Control>(Keyboard.FocusedElement);
                traversed.Add(focused is ListBoxItem ? "CandidateList" : focused.Name);
                if (step < expected.Length - 1)
                {
                    Assert.True(focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                }
            }
            Assert.Equal(expected, traversed);
        }
        finally
        {
            dialog.Close();
        }
    });

    private static IEnumerable<FrameworkElement> LogicalInputs(DependencyObject root)
    {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBox or ComboBox or ListBox or CheckBox)
            {
                yield return (FrameworkElement)child;
            }
            foreach (FrameworkElement input in LogicalInputs(child)) yield return input;
        }
    }

    [Fact]
    public Task FileSize_IsTheOnePassedIn_WithoutTouchingTheFile() => WpfTestHost.Run(async () =>
    {
        string path = Path.Combine(_directory, "unreachable.raw");
        FormatMemory memory = CreateMemory();
        RawImportDialog dialog = CreateDialog(path, memory, initial: Fmt(640, 480, bitDepth: 16), size: FileSize);
        try
        {
            Assert.False(File.Exists(path));
            Assert.Equal(FormatCandidates.Build(FileSize, ".raw", path, memory.History, null), dialog.Candidates);
            var sizeNote = Find<TextBlock>(dialog, "SizeNoteText");
            var previewNote = Find<TextBlock>(dialog, "PreviewNote");
            var open = Find<Button>(dialog, "OpenButton");
            int reads = 0;
            int uiThread = Environment.CurrentManagedThreadId;
            var read = dialog.ReadPreview;
            dialog.ReadPreview = (rawPath, format, width, height, token) =>
            {
                Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                Interlocked.Increment(ref reads);
                return read(rawPath, format, width, height, token);
            };
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(0, Volatile.Read(ref reads));
            Assert.StartsWith("✓", sizeNote.Text);
            Assert.True(open.IsEnabled);

            dialog.Show();
            await WaitForPreviewAsync(() => previewNote.Text.StartsWith("プレビューを読み取れませんでした。"));
            Assert.Equal(1, Volatile.Read(ref reads));
            Assert.StartsWith("✓", sizeNote.Text);
            Assert.True(open.IsEnabled);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task Preview_WidthChangesAndInvalidInputKeepsLastBitmap() => WpfTestHost.Run(async () =>
    {
        string path = CreatePreviewFile();
        RawImportDialog dialog = CreateDialog(path, null, initial: Fmt(192, 128));
        try
        {
            dialog.Show();
            var preview = Find<Image>(dialog, "PreviewImage");
            await WaitForPreviewAsync(() => preview.Source is not null);
            var correct = Assert.IsAssignableFrom<BitmapSource>(preview.Source);
            Assert.Equal((192, 128), (correct.PixelWidth, correct.PixelHeight));
            await CaptureVisibleAsync(dialog, "import-preview-correct-width");

            var width = Find<TextBox>(dialog, "WidthBox");
            width.Text = "181";
            await WaitForPreviewAsync(() => preview.Source is not null && preview.Source != correct);
            var wrong = Assert.IsAssignableFrom<BitmapSource>(preview.Source);
            Assert.Equal((181, 128), (wrong.PixelWidth, wrong.PixelHeight));
            await CaptureVisibleAsync(dialog, "import-preview-wrong-width");

            width.Text = "不正";
            Assert.Same(wrong, preview.Source);
            Assert.InRange(preview.Opacity, 0.01, 0.99);
            Assert.Contains("幅は1以上の整数", Find<TextBlock>(dialog, "PreviewNote").Text);
            Assert.False(Find<Button>(dialog, "OpenButton").IsEnabled);
            await CaptureVisibleAsync(dialog, "import-preview-invalid");
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task Preview_ShortFileShowsOnlyCompleteRowsAndKeepsExpectedShape() => WpfTestHost.Run(async () =>
    {
        const int width = 8;
        const int height = 6;
        string path = CreateFile("short.raw", width * 2 * 3 + 1);
        RawImportDialog dialog = CreateDialog(path, null, initial: Fmt(width, height));
        try
        {
            dialog.Show();
            var preview = Find<Image>(dialog, "PreviewImage");
            await WaitForPreviewAsync(() => preview.Source is not null);
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(preview.Source);
            Assert.Equal((width, height), (bitmap.PixelWidth, bitmap.PixelHeight));
            Assert.Contains("3/6 行", Find<TextBlock>(dialog, "PreviewNote").Text);
            byte[] pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            Assert.Equal(255, pixels[3]);
            Assert.Equal(0, pixels[3 * width * 4 + 3]);
            Assert.False(Find<Button>(dialog, "OpenButton").IsEnabled);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task Preview_BlockedReadKeepsOnlyLatestFormatAndCannotOverwriteIt() => WpfTestHost.Run(async () =>
    {
        string path = Path.Combine(_directory, "blocked.raw");
        RawImportDialog dialog = CreateDialog(path, null, initial: Fmt(192, 128), size: 192L * 128 * 2);
        var firstStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readWidths = new System.Collections.Concurrent.ConcurrentQueue<int>();
        dialog.ReadPreview = (_, format, _, _, token) =>
        {
            readWidths.Enqueue(format.Width);
            if (format.Width == 192)
            {
                firstStarted.SetResult(token);
                releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            return new RawPreview(format.Width, 1, new ushort[format.Width], 1, 1);
        };

        try
        {
            dialog.Show();
            CancellationToken firstToken = await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var width = Find<TextBox>(dialog, "WidthBox");
            width.Text = "180";
            await Task.Delay(200);
            width.Text = "179";
            await Task.Delay(200);
            Assert.True(firstToken.IsCancellationRequested);
            Assert.Equal(new[] { 192 }, readWidths.ToArray());
            var preview = Find<Image>(dialog, "PreviewImage");
            var published = new List<int>();
            var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
            EventHandler capture = (_, _) =>
            {
                if (preview.Source is BitmapSource bitmap)
                {
                    published.Add(bitmap.PixelWidth);
                }
            };
            descriptor.AddValueChanged(preview, capture);
            try
            {
                releaseFirst.SetResult();
                await WaitForPreviewAsync(() => preview.Source is not null);
                Assert.Equal(new[] { 192, 179 }, readWidths.ToArray());
                Assert.Equal(new[] { 179 }, published);
            }
            finally
            {
                descriptor.RemoveValueChanged(preview, capture);
            }
        }
        finally
        {
            releaseFirst.TrySetResult();
            dialog.Close();
        }
    });

    [Fact]
    public Task Preview_CloseCancelsWithoutWaitingAndNextDialogWaitsForTheReadSlot() => WpfTestHost.Run(async () =>
    {
        string path = Path.Combine(_directory, "blocked.raw");
        RawImportDialog first = CreateDialog(path, null, initial: Fmt(2, 2), size: 8);
        RawImportDialog next = CreateDialog(path, null, initial: Fmt(2, 2), size: 8);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int nextReads = 0;
        first.ReadPreview = (_, _, _, _, token) =>
        {
            started.SetResult(token);
            release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return new RawPreview(2, 2, [0, 0, 0, 0], 2, 1);
        };
        next.ReadPreview = (_, _, _, _, _) =>
        {
            Interlocked.Increment(ref nextReads);
            return new RawPreview(2, 2, [0, 0, 0, 0], 2, 1);
        };

        try
        {
            first.Show();
            CancellationToken token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first.Close();
            Assert.True(token.IsCancellationRequested);
            Assert.False(first.IsVisible);
            next.Show();
            await Task.Delay(250);
            Assert.Equal(0, Volatile.Read(ref nextReads));
            release.SetResult();
            await WaitForPreviewAsync(() => Find<Image>(next, "PreviewImage").Source is not null);
            Assert.Null(Find<Image>(first, "PreviewImage").Source);
            Assert.Equal(1, Volatile.Read(ref nextReads));
        }
        finally
        {
            release.TrySetResult();
            first.Close();
            next.Close();
        }
    });

    private string CreatePreviewFile()
    {
        string path = Path.Combine(_directory, "sample_192x128_12bit.raw");
        using var writer = new BinaryWriter(File.Create(path));
        for (int y = 0; y < 128; y++)
        {
            for (int x = 0; x < 192; x++)
            {
                int radiusSquared = (x - 96) * (x - 96) + (y - 64) * (y - 64);
                int value = 160 + x * 8 + y * 4;
                if (radiusSquared < 42 * 42)
                {
                    value += 900;
                }
                if (x is >= 16 and < 56 && y is >= 35 and < 93)
                {
                    value += ((x / 8 + y / 8) & 1) * 600;
                }
                writer.Write((ushort)value);
            }
        }
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Preview_WorksFromNormalOpenAndCompareAdd(bool comparing) => WpfTestHost.Run(async () =>
    {
        string path = CreatePreviewFile();
        string settings = Path.Combine(_directory, "settings");
        new RecentFilesStore(settings).Add(path);
        var window = new MainWindow(settings) { ShowActivated = false };
        var compare = Find<CompareView>(window, "CompareArea");
        var viewport = Find<ImageViewport>(window, "Viewport");
        RawImportDialog? shown = null;
        EventHandler? closeOnTimeout = null;
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        RawImportDialog? OwnedImport() => window.OwnedWindows.OfType<RawImportDialog>().SingleOrDefault();
        closeOnTimeout = (_, _) =>
        {
            watchdog.Stop();
            OwnedImport()?.Close();
        };
        watchdog.Tick += closeOnTimeout;
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            watchdog.Start();
            Task<bool>? add = null;
            if (comparing)
            {
                WpfTestHost.Toggle(Find<ToggleButton>(window, "CompareToolToggle"));
                // ShowDialog のネストしたフレームの中でテストがプレビューを操作できるように送る。
                add = window.Dispatcher.InvokeAsync(() => compare.AddPaneFromPathAsync(path)).Task.Unwrap();
            }
            else
            {
                var recent = Find<ItemsControl>(window, "EmptyCanvasRecent");
                var item = Assert.IsType<ContentPresenter>(recent.ItemContainerGenerator.ContainerFromIndex(0));
                WpfTestHost.Invoke(Assert.IsType<Button>(VisualTreeHelper.GetChild(item, 0)));
            }

            await WaitForPreviewAsync(() => (shown = OwnedImport())?.IsLoaded == true);
            RawImportDialog dialog = Assert.IsType<RawImportDialog>(shown);
            await WaitForPreviewAsync(() => Find<Image>(dialog, "PreviewImage").Source is not null);
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(Find<Image>(dialog, "PreviewImage").Source);
            Assert.Equal((192, 128), (bitmap.PixelWidth, bitmap.PixelHeight));
            WpfTestHost.Invoke(Find<Button>(dialog, "OpenButton"));
            if (add is not null)
            {
                Assert.True(await add.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(192, compare.ActivePane?.Pane?.Image.Width);
            }
            else
            {
                await WaitForPreviewAsync(() => viewport.Image is not null);
                Assert.Equal(192, viewport.Image!.Width);
            }
        }
        finally
        {
            watchdog.Stop();
            watchdog.Tick -= closeOnTimeout;
            if (shown?.IsVisible == true)
            {
                shown.Close();
            }
            await compare.CloseAllAsync();
            window.Close();
        }
    });

    private static async Task WaitForPreviewAsync(Func<bool> isReady)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!isReady())
        {
            Assert.False(timeout.IsCancellationRequested, "プレビューが時間内に更新されませんでした。");
            await Task.Delay(20);
        }
    }

    [Fact]
    public Task UndefinedPackingAndEndianness_AreShownAsTheReaderInterpretsThem()
    {
        // 回帰テスト: 読み込みは Big 以外を Little とみなすのに、ダイアログは Little 以外を Big として示していた
        // (壊れたプリセットの Endianness=5 など)。詰め方向は読み込みと同じく Lsb 以外を上詰めとして示す
        string path = CreateFile("undefined_enums.raw", FileSize);
        return WpfTestHost.Run(() =>
        {
            RawFormat initial = Fmt(640, 480, bitDepth: 16) with
            {
                Packing = (BitPacking)7, Endianness = (Endianness)5,
            };

            RawImportDialog dialog = CreateDialog(path, null, initial: initial);

            Assert.Equal(1, Find<ComboBox>(dialog, "PackingCombo").SelectedIndex);
            Assert.Equal(0, Find<ComboBox>(dialog, "EndianCombo").SelectedIndex);
            dialog.Close();
        });
    }

    private static T Find<T>(Window window, string name)
        where T : class
    {
        return (T)window.FindName(name);
    }

    private static void Click(Window window, string name)
    {
        Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    [Fact]
    public Task CandidateList_ShowsSourcesAndSelectingOneFillsEveryField()
    {
        return WpfTestHost.Run(() =>
        {
            string path = CreateFile("cap_320x960.raw", FileSize);
            FormatMemory memory = CreateMemory();
            RawFormat remembered = Fmt(640, 480, bayer: BayerPattern.Rggb) with
            {
                Packing = BitPacking.Msb, Endianness = Endianness.Big,
            };
            memory.RememberLoaded(path, FileSize, remembered, autoOpen: true);
            RawFormat displayed = Fmt(480, 640, bitDepth: 16);

            RawImportDialog dialog = CreateDialog(path, memory, displayed: displayed);
            var list = Find<ListBox>(dialog, "CandidateList");

            // 一覧は Core の候補そのもの。
            IReadOnlyList<FormatCandidate> expected = FormatCandidates.Build(
                FileSize, ".raw", path, memory.History, displayed);
            Assert.Equal(expected, dialog.Candidates);
            Assert.True(list.IsEnabled);
            var rows = list.Items.Cast<RawImportDialog.CandidateRow>().ToArray();
            Assert.Equal(expected.Count, rows.Length);
            Assert.Contains($"{expected.Count} 件", Find<TextBlock>(dialog, "CandidateHeading").Text);
            Assert.Equal(new RawImportDialog.CandidateRow("記憶(同じサイズ)", "640×480 12bit RGGB 上詰め Big"), rows[0]);
            Assert.Equal(new RawImportDialog.CandidateRow("表示中の画像", "480×640 16bit モノクロ"), rows[1]);
            Assert.Contains(new RawImportDialog.CandidateRow("ファイル名", "320×960 12bit RGGB 上詰め Big"), rows);
            Assert.Contains(new RawImportDialog.CandidateRow("解像度表", "640×480 12bit モノクロ"), rows);
            Assert.InRange(rows.Length, 5, 10);

            // 初期値は候補の先頭。一覧もその候補を指す
            Assert.Equal("640", Find<TextBox>(dialog, "WidthBox").Text);
            Assert.Equal(1, Find<ComboBox>(dialog, "PackingCombo").SelectedIndex);
            Assert.Equal(1, Find<ComboBox>(dialog, "EndianCombo").SelectedIndex);
            Assert.Equal(0, Find<ComboBox>(dialog, "BayerCombo").SelectedIndex);
            Assert.Equal(0, list.SelectedIndex);
            CaptureIfRequested(dialog, "import-candidates");

            // 選ぶと全項目にその形式が入る(プリセットの選択と同じ)
            const int index = 1;
            list.SelectedIndex = index;
            Assert.Equal("480", Find<TextBox>(dialog, "WidthBox").Text);
            Assert.Equal("640", Find<TextBox>(dialog, "HeightBox").Text);
            Assert.Equal("16bit", Find<ComboBox>(dialog, "BitDepthCombo").SelectedItem);
            Assert.Equal(0, Find<ComboBox>(dialog, "PackingCombo").SelectedIndex);
            Assert.Equal(0, Find<ComboBox>(dialog, "EndianCombo").SelectedIndex);
            Assert.Equal(4, Find<ComboBox>(dialog, "BayerCombo").SelectedIndex);
            Assert.StartsWith("✓", Find<TextBlock>(dialog, "SizeNoteText").Text);
            Assert.True(Find<Button>(dialog, "OpenButton").IsEnabled);
            Assert.Equal(index, list.SelectedIndex);

            // 手で変えて候補と違う値になったら、未選択に戻る(値を戻せばまたその候補を指す)。
            Find<TextBox>(dialog, "HeightBox").Text = "641";
            Assert.Equal(-1, list.SelectedIndex);
            Find<TextBox>(dialog, "HeightBox").Text = "640";
            Assert.Equal(index, list.SelectedIndex);
            dialog.Close();
        });
    }

    [Fact]
    public Task SizeMismatch_ShowsByteDifferenceAndHeaderHint()
    {
        // 全体レビュー 2026-10-01 B91。不一致の表示は MB の小数2桁だけで、先頭に 512B のヘッダがある
        // 1920×1080 16bit(4,147,712B)をヘッダ 0 のまま入れると「3.96 MB ≠ ファイル 3.96 MB」と出て、
        // ずれの大きさ(ヘッダの付け忘れ)が分からなかった。両方のバイト数と差を示し、1フレーム未満の
        // 余りならヘッダ・末尾の余りの可能性を添える
        string path = CreateFile("with_header.raw", (1920L * 1080 * 2) + 512);
        return WpfTestHost.Run(() =>
        {
            RawImportDialog dialog = CreateDialog(path, null, initial: Fmt(1920, 1080, bitDepth: 16));
            var note = Find<TextBlock>(dialog, "SizeNoteText");

            Assert.StartsWith("⚠", note.Text);
            Assert.Contains((1920L * 1080 * 2).ToString("N0"), note.Text);
            Assert.Contains(((1920L * 1080 * 2) + 512).ToString("N0"), note.Text);
            Assert.Contains("512", note.Text);
            Assert.Contains("ヘッダ", note.Text);
            Assert.DoesNotContain("3.96 MB ≠", note.Text);

            // 末尾に余りがあるだけなら従来どおり開ける
            Assert.True(Find<Button>(dialog, "OpenButton").IsEnabled);

            // ファイルが足りないときは足りないバイト数を示し、開けない
            Find<TextBox>(dialog, "WidthBox").Text = "1922";
            Assert.StartsWith("⚠", note.Text);
            Assert.Contains("足りません", note.Text);
            Assert.Contains((((1922L - 1920) * 1080 * 2) - 512).ToString("N0"), note.Text);
            Assert.False(Find<Button>(dialog, "OpenButton").IsEnabled);
            dialog.Close();
        });
    }

    [Fact]
    public Task FullWidthNumbers_AreReadLikeOtherNumericFields()
    {
        // IME がオンのまま打った全角の数字・3桁区切りも、露光比などの数値入力欄と同じく読む
        // (以前は幅・高さ・ヘッダ・フレーム数・ライン単位・行オフセットだけ「不正です」になった)
        string path = CreateFile("full_width.raw", FileSize + 1024);
        return WpfTestHost.Run(() =>
        {
            RawImportDialog dialog = CreateDialog(path, null, initial: Fmt(320, 240));
            var note = Find<TextBlock>(dialog, "SizeNoteText");
            Find<TextBox>(dialog, "WidthBox").Text = "６４０";
            Find<TextBox>(dialog, "HeightBox").Text = "４８０";
            Find<TextBox>(dialog, "HeaderOffsetBox").Text = "１，０２４";
            Find<TextBox>(dialog, "FrameCountBox").Text = "１";
            Find<TextBox>(dialog, "HdrLineBlockBox").Text = "２";
            Find<TextBox>(dialog, "HdrRowOffsetBox").Text = "ー１";

            Assert.StartsWith("✓", note.Text);
            Assert.True(Find<Button>(dialog, "OpenButton").IsEnabled);

            // 整数でない値は従来どおり断る
            Find<TextBox>(dialog, "WidthBox").Text = "６４０．５";
            Assert.StartsWith("✕", note.Text);
            dialog.Close();
        });
    }

    [Fact]
    public Task InvalidFields_AreEachMarkedWithReason()
    {
        // 以前はサイズ欄に最初の1件の「幅が不正です」を出すだけで、欄の見た目ではどこが悪いか分からず、
        // 2つ目以降の不正は1つ目を直すまで見えなかった。不正な欄をすべて、ファイル一覧の絞り込み欄と同じく
        // 赤枠とツールチップの理由で示す
        string path = CreateFile("invalid_fields.raw", FileSize);
        return WpfTestHost.Run(() =>
        {
            RawImportDialog dialog = CreateDialog(path, null, initial: Fmt(640, 480));
            var note = Find<TextBlock>(dialog, "SizeNoteText");
            var width = Find<TextBox>(dialog, "WidthBox");
            var height = Find<TextBox>(dialog, "HeightBox");
            var frames = Find<TextBox>(dialog, "FrameCountBox");
            var ratio = Find<TextBox>(dialog, "ExposureRatioBox");
            FieldFeedback.AssertValid(width);
            FieldFeedback.AssertValid(height);

            width.Text = "abc";
            height.Text = "0";
            frames.Text = "1.5";
            ratio.Text = "-2";

            string widthReason = FieldFeedback.AssertInvalid(width);
            Assert.Contains("幅は1以上の整数", widthReason);
            Assert.Contains("高さは1以上の整数", FieldFeedback.AssertInvalid(height));
            Assert.Contains("フレーム数は1以上の整数", FieldFeedback.AssertInvalid(frames));
            Assert.Contains("露光比は正の数値", FieldFeedback.AssertInvalid(ratio));
            FieldFeedback.AssertValid(Find<TextBox>(dialog, "HeaderOffsetBox"));
            Assert.Equal($"✕ {widthReason}", note.Text); // サイズ欄には最初の理由
            Assert.False(Find<Button>(dialog, "OpenButton").IsEnabled);
            var details = Find<Expander>(dialog, "DetailsExpander");
            Assert.True(details.IsExpanded);
            details.IsExpanded = false;
            Assert.True(details.IsExpanded);
            Assert.Equal(Visibility.Visible, Find<Grid>(dialog, "HdrDetails").Visibility);

            width.Text = "640";
            height.Text = "480";
            frames.Text = "1";
            ratio.Text = "16";
            FieldFeedback.AssertValid(width);
            FieldFeedback.AssertValid(height);
            FieldFeedback.AssertValid(frames);
            FieldFeedback.AssertValid(ratio);
            Assert.StartsWith("✓", note.Text);
            Assert.Equal(Visibility.Collapsed, Find<Grid>(dialog, "HdrDetails").Visibility);
            dialog.Close();
        });
    }

    [Fact]
    public Task Disclosure_ShowsHdrFieldsAndOpensNonDefaultPresetOnlyOnApply() => WpfTestHost.Run(async () =>
    {
        string path = CreatePreviewFile();
        new FormatPresetStore(Path.Combine(_directory, "settings")).Save(new Dictionary<string, RawFormat>
        {
            ["SensorA"] = Fmt(192, 128) with { Packing = BitPacking.Msb, Endianness = Endianness.Big },
        });
        RawImportDialog dialog = CreateDialog(path, null, initial: Fmt(192, 128));
        try
        {
            dialog.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var details = Find<Expander>(dialog, "DetailsExpander");
            var hdr = Find<Grid>(dialog, "HdrDetails");
            Assert.False(details.IsExpanded);
            Assert.Equal(Visibility.Collapsed, hdr.Visibility);
            var image = Find<Image>(dialog, "PreviewImage");
            await WaitForPreviewAsync(() => image.Source is not null);
            await CaptureVisibleAsync(dialog, "import-basic");

            ImageSource? previous = image.Source;
            Find<ComboBox>(dialog, "PresetCombo").SelectedItem = "SensorA";
            Assert.True(details.IsExpanded);
            await WaitForPreviewAsync(() => image.Source is not null && image.Source != previous);
            await CaptureVisibleAsync(dialog, "import-details");
            var header = Assert.IsType<ToggleButton>(details.Template.FindName("HeaderSite", details));
            WpfTestHost.Toggle(header);
            Assert.False(details.IsExpanded);
            // プリセットで開いた詳細は再入力で開き直さない。
            Find<TextBox>(dialog, "HeightBox").Text = "127";
            Assert.False(details.IsExpanded);
            Assert.Equal(1, Find<ComboBox>(dialog, "PackingCombo").SelectedIndex);
            Find<TextBox>(dialog, "HeightBox").Text = "128";

            var layout = Find<ComboBox>(dialog, "HdrCombo");
            layout.SelectedIndex = 2;
            Assert.True(hdr.IsVisible);
            var lineBlock = Find<TextBox>(dialog, "HdrLineBlockBox");
            previous = image.Source;
            lineBlock.Text = "2";
            await WaitForPreviewAsync(() => image.Source is not null && image.Source != previous);
            await CaptureVisibleAsync(dialog, "import-hdr-line-interleaved");
            layout.SelectedIndex = 0;
            Assert.False(hdr.IsVisible);
            Assert.Equal("2", lineBlock.Text);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task EditingAfterPreset_ReturnsPresetListToGuideRowSoReselectingRestoresIt()
    {
        // 全体レビュー 2026-10-01 B92。プリセットを選んだあと幅を書き換えても一覧はそのプリセット名のまま残り、
        // 元に戻そうと同じプリセットを選び直しても(選択が変わらないので)何も起きなかった。
        // 入力がプリセットと食い違ったら一覧を案内の行へ戻し(候補一覧と同じ)、選び直せば全項目を戻す
        string path = CreateFile("sensor.raw", FileSize);
        new FormatPresetStore(Path.Combine(_directory, "settings")).Save(new Dictionary<string, RawFormat>
        {
            ["SensorA"] = Fmt(640, 480, bayer: BayerPattern.Gbrg),
        });
        return WpfTestHost.Run(() =>
        {
            RawImportDialog dialog = CreateDialog(path, null, initial: Fmt(320, 240, bitDepth: 16));
            var presets = Find<ComboBox>(dialog, "PresetCombo");
            var width = Find<TextBox>(dialog, "WidthBox");

            presets.SelectedItem = "SensorA";
            Assert.Equal("640", width.Text);
            Assert.Equal(3, Find<ComboBox>(dialog, "BayerCombo").SelectedIndex);
            Assert.Equal("SensorA", presets.SelectedItem);

            width.Text = "600";
            Assert.Equal(0, presets.SelectedIndex);
            Assert.Equal("600", width.Text);

            presets.SelectedItem = "SensorA";
            Assert.Equal("640", width.Text);
            Assert.Equal("SensorA", presets.SelectedItem);

            // 候補を選んでプリセットと違う値になっても案内の行へ戻る
            var candidates = Find<ListBox>(dialog, "CandidateList");
            int other = Enumerable.Range(0, dialog.Candidates.Count)
                .First(i => dialog.Candidates[i].Format != Fmt(640, 480, bayer: BayerPattern.Gbrg));
            candidates.SelectedIndex = other;
            Assert.Equal(0, presets.SelectedIndex);
            dialog.Close();
        });
    }

    [Fact]
    public Task SavePreset_KeepsPresetsSavedByAnotherInstanceAfterTheDialogOpened()
    {
        // 残課題 2026-10-02 M1。ダイアログを開いたときに読んだプリセットを丸ごと書き戻していたので、その後に
        // 別のインスタンス(のダイアログ)で保存したプリセットが消えた。保存されている最新へこのプリセットだけを当てる
        string path = CreateFile("sensor.raw", FileSize);
        return WpfTestHost.Run(() =>
        {
            RawImportDialog a = CreateDialog(path, null, initial: Fmt(640, 480));
            RawImportDialog b = CreateDialog(path, null, initial: Fmt(640, 480));
            b.SavePreset("SensorB", Fmt(640, 480, bayer: BayerPattern.Rggb));
            a.SavePreset("SensorA", Fmt(640, 480, bayer: BayerPattern.Gbrg));

            IReadOnlyDictionary<string, RawFormat> saved =
                new FormatPresetStore(Path.Combine(_directory, "settings")).Load();
            Assert.Equal(Fmt(640, 480, bayer: BayerPattern.Rggb), saved["SensorB"]);
            Assert.Equal(Fmt(640, 480, bayer: BayerPattern.Gbrg), saved["SensorA"]);

            // 一覧にも他のインスタンスが保存したプリセットが出て、保存したものが選ばれている
            var presets = Find<ComboBox>(a, "PresetCombo");
            Assert.Contains("SensorB", presets.Items.Cast<string>());
            Assert.Equal("SensorA", presets.SelectedItem);
            a.Close();
            b.Close();
        });
    }

    [Fact]
    public Task UnknownFileSize_SaysTheSizeCannotBeRead()
    {
        // ファイルサイズを取得できない(-1)ときに「ファイル -0.00 MB」と出て、取得できないことが伝わらなかった
        string path = Path.Combine(_directory, "missing.raw");
        return WpfTestHost.Run(() =>
        {
            // 呼び出し側はサイズを取得できなかったら -1 を渡す
            RawImportDialog dialog = CreateDialog(path, null, initial: Fmt(640, 480), size: -1);
            string note = Find<TextBlock>(dialog, "SizeNoteText").Text;

            Assert.StartsWith("⚠", note);
            Assert.Contains("ファイルサイズを取得できません", note);
            Assert.DoesNotContain("-0.00", note);
            Assert.False(Find<Button>(dialog, "OpenButton").IsEnabled);
            dialog.Close();
        });
    }

    [Fact]
    public Task CandidateList_WithoutCandidates_IsDisabled()
    {
        return WpfTestHost.Run(() =>
        {
            string path = CreateFile("odd.raw", 1001);

            RawImportDialog dialog = CreateDialog(path, CreateMemory());

            var list = Find<ListBox>(dialog, "CandidateList");
            Assert.False(list.IsEnabled);
            Assert.Empty(list.Items);
            Assert.Equal(-1, list.SelectedIndex);
            Assert.Empty(dialog.Candidates);

            // 候補も初期値もなければ従来どおりの既定値
            Assert.Equal("1920", Find<TextBox>(dialog, "WidthBox").Text);
            Assert.Equal("1080", Find<TextBox>(dialog, "HeightBox").Text);
            dialog.Close();
        });
    }

    [Fact]
    public Task CandidateList_ArrowKeysApplyAndScrollToTheSelectedRow() => WpfTestHost.Run(async () =>
    {
        string path = CreateFile("cap_320x960.raw", FileSize);
        FormatMemory memory = CreateMemory();
        foreach (int width in new[] { 80, 96, 100, 120, 128, 160, 192, 200, 240, 256, 300, 320 })
        {
            memory.RememberLoaded(path, FileSize, Fmt(width, 307200 / width), autoOpen: false);
        }
        RawImportDialog dialog = CreateDialog(path, memory, initial: Fmt(640, 480, 8));
        try
        {
            dialog.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var list = Find<ListBox>(dialog, "CandidateList");
            Assert.Equal(10, list.Items.Count);
            Assert.Equal(-1, list.SelectedIndex);
            var widthBox = Find<TextBox>(dialog, "WidthBox");
            Assert.True(widthBox.Focus());
            Assert.True(widthBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)));
            Assert.IsType<ListBoxItem>(Keyboard.FocusedElement);

            for (int i = 0; i < list.Items.Count; i++)
            {
                var focused = Assert.IsType<ListBoxItem>(Keyboard.FocusedElement);
                focused.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list),
                    Environment.TickCount, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent });
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(9, list.SelectedIndex);
            Assert.Equal(dialog.Candidates[9].Format.Width.ToString(), widthBox.Text);
            var selected = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(9));
            Rect bounds = selected.TransformToAncestor(list).TransformBounds(new Rect(selected.RenderSize));
            Assert.InRange(bounds.Bottom, 1, list.ActualHeight);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task AutoOpenCheck_StartsFromRememberedFlagAndIsReturned()
    {
        return WpfTestHost.Run(() =>
        {
            string path = CreateFile("a.raw", FileSize);
            FormatMemory memory = CreateMemory();
            RawFormat off = Fmt(640, 480, bayer: BayerPattern.Bggr);
            memory.RememberLoaded(path, FileSize, off, autoOpen: false);

            // 初期値(候補の先頭)が自動適用オフの記憶なら、オフから始める
            RawImportDialog fromMemory = CreateDialog(path, memory);
            var check = Find<CheckBox>(fromMemory, "AutoOpenCheck");
            Assert.False(check.IsChecked);
            Assert.False(fromMemory.AutoOpenNextTime);
            check.IsChecked = true;
            Assert.True(fromMemory.AutoOpenNextTime);
            fromMemory.Close();

            // F2 のように初期値が渡されても、それが記憶(オフ)ならオフ
            RawImportDialog f2 = CreateDialog(path, memory, initial: off);
            Assert.False(f2.AutoOpenNextTime);
            f2.Close();

            // 記憶にない初期値・記憶なしは既定のオン
            RawImportDialog other = CreateDialog(path, memory, initial: Fmt(480, 640, bitDepth: 16));
            Assert.True(other.AutoOpenNextTime);
            other.Close();
            RawImportDialog noMemory = CreateDialog(path, memory: null);
            Assert.True(noMemory.AutoOpenNextTime);
            noMemory.Close();
        });
    }

    [Fact]
    public Task ForgetSizeButton_RemovesEveryMemoryOfThisSizeAndSaysSo()
    {
        return WpfTestHost.Run(() =>
        {
            string path = CreateFile("b.raw", FileSize);
            FormatMemory memory = CreateMemory();
            RawFormat first = Fmt(640, 480, bayer: BayerPattern.Rggb);
            // 記憶は保存されたものが正(他のインスタンスの変更を巻き戻さないよう、削除は保存されている最新の
            // 記憶に当てる)ので、手元の記憶を直接いじらず読み込み成功の記録として保存しておく
            memory.RememberLoaded(path, FileSize, first, autoOpen: true);
            memory.RememberLoaded(path, FileSize, Fmt(480, 640, bitDepth: 16), autoOpen: false);
            memory.RememberLoaded(Path.ChangeExtension(path, ".bin"), FileSize, first, autoOpen: true);

            RawImportDialog dialog = CreateDialog(path, memory, initial: first);
            var button = Find<Button>(dialog, "ForgetSizeButton");
            var note = Find<TextBlock>(dialog, "MemoryNoteText");
            Assert.True(button.IsEnabled);
            Assert.Equal("このサイズの記憶: 2 件", note.Text);
            Assert.Contains(dialog.Candidates, c => c.Source == FormatCandidateSource.SameSizeMemory);

            Click(dialog, "ForgetSizeButton");

            // そのキーの記憶だけを消して保存し、消したことを示す。入力中の値はそのまま
            Assert.Empty(memory.History.Find(FileSize, ".raw"));
            Assert.Single(memory.History.Find(FileSize, ".bin"));
            FormatHistory saved = new FormatHistoryStore(Path.Combine(_directory, "settings")).Load();
            Assert.Empty(saved.Find(FileSize, ".raw"));
            Assert.Equal("✓ このサイズの記憶 2 件を削除しました", note.Text);
            Assert.False(button.IsEnabled);
            Assert.DoesNotContain(dialog.Candidates, c => c.Source == FormatCandidateSource.SameSizeMemory);
            Assert.Equal("640", Find<TextBox>(dialog, "WidthBox").Text);
            Assert.Equal(0, Find<ComboBox>(dialog, "BayerCombo").SelectedIndex);
            dialog.Close();

            // 記憶がないサイズでは押せない
            RawImportDialog empty = CreateDialog(CreateFile("c.raw", FileSize + 2), memory);
            Assert.False(Find<Button>(empty, "ForgetSizeButton").IsEnabled);
            Assert.Equal("このサイズの記憶はありません", Find<TextBlock>(empty, "MemoryNoteText").Text);
            empty.Close();
        });
    }

    private static void CaptureIfRequested(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("RAWANALYZER_UI_SNAPSHOTS");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, double.PositiveInfinity));
        int height = (int)Math.Ceiling(content.DesiredSize.Height);
        content.Arrange(new Rect(0, 0, window.Width, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext dc = background.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, window.Width, height));
        }

        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, $"{name}-dialog.png"));
        encoder.Save(output);
    }

    private static async Task CaptureVisibleAsync(Window window, string name)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        WpfTestHost.CaptureIfRequested((FrameworkElement)window.Content, $"{name}-dialog");
    }
}
