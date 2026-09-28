using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        string path, FormatMemory? memory, RawFormat? initial = null, RawFormat? displayed = null)
    {
        return new RawImportDialog(
            path, new FormatPresetStore(Path.Combine(_directory, "settings")), initial, memory, displayed);
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
            memory.History.Record(FileSize, ".raw", remembered, autoOpen: true, T0);
            RawFormat displayed = Fmt(480, 640, bitDepth: 16);

            RawImportDialog dialog = CreateDialog(path, memory, displayed: displayed);
            var combo = Find<ComboBox>(dialog, "CandidateCombo");

            // 一覧は Core の候補そのもの(先頭の行は案内)
            IReadOnlyList<FormatCandidate> expected = FormatCandidates.Build(
                FileSize, ".raw", path, memory.History, displayed);
            Assert.Equal(expected, dialog.Candidates);
            Assert.True(combo.IsEnabled);
            Assert.Equal(expected.Count + 1, combo.Items.Count);
            Assert.Equal($"候補から選ぶ…({expected.Count} 件)", combo.Items[0]);
            Assert.Equal("記憶(同じサイズ): 640×480 12bit RGGB 上詰め Big", combo.Items[1]);
            Assert.Equal("表示中の画像: 480×640 16bit モノクロ", combo.Items[2]);
            Assert.Contains("ファイル名: 320×960 12bit RGGB 上詰め Big", combo.Items.Cast<string>());
            Assert.Contains("解像度表: 640×480 12bit モノクロ", combo.Items.Cast<string>());

            // 初期値は候補の先頭。一覧もその候補を指す
            Assert.Equal("640", Find<TextBox>(dialog, "WidthBox").Text);
            Assert.Equal(1, Find<ComboBox>(dialog, "PackingCombo").SelectedIndex);
            Assert.Equal(1, Find<ComboBox>(dialog, "EndianCombo").SelectedIndex);
            Assert.Equal(0, Find<ComboBox>(dialog, "BayerCombo").SelectedIndex);
            Assert.Equal(1, combo.SelectedIndex);
            CaptureIfRequested(dialog, "import-candidates");

            // 選ぶと全項目にその形式が入る(プリセットの選択と同じ)
            int index = combo.Items.IndexOf("表示中の画像: 480×640 16bit モノクロ");
            combo.SelectedIndex = index;
            Assert.Equal("480", Find<TextBox>(dialog, "WidthBox").Text);
            Assert.Equal("640", Find<TextBox>(dialog, "HeightBox").Text);
            Assert.Equal("16bit", Find<ComboBox>(dialog, "BitDepthCombo").SelectedItem);
            Assert.Equal(0, Find<ComboBox>(dialog, "PackingCombo").SelectedIndex);
            Assert.Equal(0, Find<ComboBox>(dialog, "EndianCombo").SelectedIndex);
            Assert.Equal(4, Find<ComboBox>(dialog, "BayerCombo").SelectedIndex);
            Assert.StartsWith("✓", Find<TextBlock>(dialog, "SizeNoteText").Text);
            Assert.True(Find<Button>(dialog, "OpenButton").IsEnabled);
            Assert.Equal(index, combo.SelectedIndex);

            // 手で変えて候補と違う値になったら、一覧は案内の行に戻る(戻せばまたその候補を指す)
            Find<TextBox>(dialog, "HeightBox").Text = "641";
            Assert.Equal(0, combo.SelectedIndex);
            Find<TextBox>(dialog, "HeightBox").Text = "640";
            Assert.Equal(index, combo.SelectedIndex);
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

            var combo = Find<ComboBox>(dialog, "CandidateCombo");
            Assert.False(combo.IsEnabled);
            Assert.Single(combo.Items);
            Assert.Empty(dialog.Candidates);

            // 候補も初期値もなければ従来どおりの既定値
            Assert.Equal("1920", Find<TextBox>(dialog, "WidthBox").Text);
            Assert.Equal("1080", Find<TextBox>(dialog, "HeightBox").Text);
            dialog.Close();
        });
    }

    [Fact]
    public Task AutoOpenCheck_StartsFromRememberedFlagAndIsReturned()
    {
        return WpfTestHost.Run(() =>
        {
            string path = CreateFile("a.raw", FileSize);
            FormatMemory memory = CreateMemory();
            RawFormat off = Fmt(640, 480, bayer: BayerPattern.Bggr);
            memory.History.Record(FileSize, ".raw", off, autoOpen: false, T0);

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
            memory.History.Record(FileSize, ".raw", first, autoOpen: true, T0);
            memory.History.Record(FileSize, ".raw", Fmt(480, 640, bitDepth: 16), autoOpen: false, T0);
            memory.History.Record(FileSize, ".bin", first, autoOpen: true, T0);

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
            CaptureIfRequested(dialog, "import-forgotten");
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
}
