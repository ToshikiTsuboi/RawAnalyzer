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
            memory.RememberLoaded(path, FileSize, remembered, autoOpen: true);
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

            width.Text = "640";
            height.Text = "480";
            frames.Text = "1";
            ratio.Text = "16";
            FieldFeedback.AssertValid(width);
            FieldFeedback.AssertValid(height);
            FieldFeedback.AssertValid(frames);
            FieldFeedback.AssertValid(ratio);
            Assert.StartsWith("✓", note.Text);
            dialog.Close();
        });
    }

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
            var candidates = Find<ComboBox>(dialog, "CandidateCombo");
            int other = Enumerable.Range(1, dialog.Candidates.Count)
                .First(i => dialog.Candidates[i - 1].Format != Fmt(640, 480, bayer: BayerPattern.Gbrg));
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
            RawImportDialog dialog = CreateDialog(path, null, initial: Fmt(640, 480));
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
