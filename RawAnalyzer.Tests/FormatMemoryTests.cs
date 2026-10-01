using System.Reflection;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// サイズ別フォーマット記憶の App 側(読み込み成功時の記録・F2 と Bayer の訂正・削除・保存と警告)。
/// </summary>
public class FormatMemoryTests : IDisposable
{
    private const long Size = 640 * 480 * 2;
    private const string Path = @"D:\cap\shot_0001.raw";
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _directory;
    private readonly List<string> _warnings = new();
    private DateTime _now = T0;

    public FormatMemoryTests()
    {
        _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static RawFormat Fmt(BayerPattern bayer = BayerPattern.None, int bitDepth = 12)
    {
        return new RawFormat { Width = 640, Height = 480, BitDepth = bitDepth, Bayer = bayer };
    }

    private const long OtherSize = 320 * 240 * 2;

    private static RawFormat OtherFmt()
    {
        return new RawFormat { Width = 320, Height = 240, BitDepth = 16 };
    }

    private FormatMemory Create()
    {
        return new FormatMemory(new FormatHistoryStore(_directory), _warnings.Add, () => _now);
    }

    [Fact]
    public void RememberLoaded_RecordsAndSavesEveryTime()
    {
        FormatMemory memory = Create();

        memory.RememberLoaded(Path, Size, Fmt(BayerPattern.Rggb), autoOpen: true);

        IReadOnlyFormatHistory reloaded = Create().History;
        FormatHistoryEntry entry = Assert.Single(reloaded.Entries);
        Assert.Equal((Size, ".raw", T0, true), (entry.FileSize, entry.Extension, entry.LastUsedUtc, entry.AutoOpen));
        Assert.Equal(Fmt(BayerPattern.Rggb), entry.Format);
        Assert.Empty(_warnings);

        // 次の記録でも保存し直す(オフで確定したものは記憶するが自動では開かない)
        _now = T0.AddMinutes(1);
        memory.RememberLoaded(Path, Size, Fmt(BayerPattern.Bggr), autoOpen: false);
        IReadOnlyFormatHistory again = Create().History;
        Assert.Equal(2, again.Entries.Count);
        Assert.Equal(Fmt(BayerPattern.Rggb), again.FindAutoOpenFormat(Size, ".raw"));
    }

    [Fact]
    public void RememberLoaded_WithCorrectFrom_ReplacesAutoOpenedFormat()
    {
        // 誤って自動で開いた形式を F2 で直すと、次に同じサイズのファイルを開くときは直した方で開く
        FormatMemory memory = Create();
        memory.RememberLoaded(Path, Size, Fmt(BayerPattern.Rggb), autoOpen: true);
        RawOpenPlan before = RawOpenPlanner.Plan(@"D:\cap\shot_0002.raw", Size, null, memory.History, null);
        Assert.Equal(RawFormatOrigin.SizeMemory, before.Origin);

        _now = T0.AddMinutes(1);
        memory.RememberLoaded(@"D:\cap\shot_0002.raw", Size, Fmt(BayerPattern.Gbrg), autoOpen: true,
            correctFrom: before.Format);

        Assert.Equal(Fmt(BayerPattern.Gbrg), Assert.Single(memory.History.Entries).Format);
        RawOpenPlan after = RawOpenPlanner.Plan(@"D:\cap\shot_0003.raw", Size, null, Create().History, null);
        Assert.Equal(new RawOpenPlan(RawFormatOrigin.SizeMemory, Fmt(BayerPattern.Gbrg), null), after);
    }

    [Fact]
    public void RememberLoaded_CorrectFromNotRemembered_RecordsNewFormat()
    {
        FormatMemory memory = Create();
        memory.RememberLoaded(Path, Size, Fmt(BayerPattern.Rggb), autoOpen: null);

        memory.RememberLoaded(Path, Size, Fmt(BayerPattern.Grbg), autoOpen: false,
            correctFrom: Fmt(BayerPattern.Bggr));

        Assert.Equal(2, memory.History.Find(Size, ".raw").Count);
        Assert.Equal(Fmt(BayerPattern.Rggb), memory.History.FindAutoOpenFormat(Size, ".raw"));
    }

    [Fact]
    public void RememberLoaded_UnknownSizeOrFormatThatNoLongerFits_IsIgnored()
    {
        FormatMemory memory = Create();

        memory.RememberLoaded(Path, -1, Fmt(), autoOpen: true);
        memory.RememberLoaded(Path, Size - 2, Fmt(), autoOpen: true); // 読み込み後にファイルが縮んだ

        Assert.Empty(memory.History.Entries);
        Assert.False(File.Exists(new FormatHistoryStore(_directory).FilePath));
    }

    [Fact]
    public void Correct_BayerChangedInPlace_ReplacesRememberedFormatKeepingFlag()
    {
        FormatMemory memory = Create();
        memory.RememberLoaded(Path, Size, Fmt(), autoOpen: false);

        Assert.True(memory.Correct(Path, Size, Fmt(), Fmt(BayerPattern.Bggr)));
        Assert.False(memory.Correct(Path, Size, Fmt(BayerPattern.Rggb), Fmt(BayerPattern.Grbg)));

        FormatHistoryEntry entry = Assert.Single(Create().History.Entries);
        Assert.Equal(Fmt(BayerPattern.Bggr), entry.Format);
        Assert.False(entry.AutoOpen);
    }

    [Fact]
    public void Forget_RemovesTheSizeAndSaves()
    {
        FormatMemory memory = Create();
        memory.RememberLoaded(Path, Size, Fmt(), autoOpen: true);
        memory.RememberLoaded(Path, Size, Fmt(bitDepth: 16), autoOpen: false);
        memory.RememberLoaded(@"D:\cap\other.bin", Size, Fmt(), autoOpen: true);

        Assert.Equal(2, memory.Forget(Size, ".raw"));
        Assert.Equal(0, memory.Forget(Size, ".raw"));

        IReadOnlyFormatHistory reloaded = Create().History;
        Assert.Empty(reloaded.Find(Size, ".raw"));
        Assert.Single(reloaded.Find(Size, ".bin"));
    }

    [Fact]
    public void Constructor_CorruptedFile_WarnsAndStartsEmpty()
    {
        var store = new FormatHistoryStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, "not json");

        FormatMemory memory = Create();

        Assert.Empty(memory.History.Entries);
        Assert.True(File.Exists(store.BackupPath));
        Assert.Contains(store.BackupPath, Assert.Single(_warnings));
    }

    // ------------------------------------------------------------------ 複数起動(全体レビュー 2026-10-01 B8)
    // 起動時に読んだ記憶を丸ごと書き戻すと、別のインスタンスで直した・消した・足した記憶が古い内容で巻き戻る。
    // 2つの FormatMemory を同じ保存先で作り、2つのインスタンスを模す

    [Fact]
    public void TwoInstances_CorrectionInOne_IsNotRevertedBySaveInOther()
    {
        // B の記憶には誤った形式(RGGB・自動で開く)がある。A がそれで自動で開き、F2 で GBRG に直した後、
        // B で別のサイズの raw を開いて保存しても、直した形式が残る
        FormatMemory b = Create();
        b.RememberLoaded(Path, Size, Fmt(BayerPattern.Rggb), autoOpen: true);
        FormatMemory a = Create();
        _now = T0.AddMinutes(1);
        a.RememberLoaded(@"D:\cap\shot_0002.raw", Size, Fmt(BayerPattern.Gbrg), autoOpen: true,
            correctFrom: Fmt(BayerPattern.Rggb));

        _now = T0.AddMinutes(2);
        b.RememberLoaded(@"D:\cap\other.raw", OtherSize, OtherFmt(), autoOpen: true);

        IReadOnlyFormatHistory next = Create().History;
        Assert.Equal(Fmt(BayerPattern.Gbrg), Assert.Single(next.Find(Size, ".raw")).Format);
        Assert.Equal(Fmt(BayerPattern.Gbrg), next.FindAutoOpenFormat(Size, ".raw"));
        Assert.Single(next.Find(OtherSize, ".raw"));

        // 動き続けている B も、次にサイズ S のファイルを開くときは直した形式で開く
        Assert.Equal(Fmt(BayerPattern.Gbrg), b.History.FindAutoOpenFormat(Size, ".raw"));
    }

    [Fact]
    public void TwoInstances_ForgetInOne_IsNotRevivedByOther()
    {
        FormatMemory b = Create();
        b.RememberLoaded(Path, Size, Fmt(BayerPattern.Rggb), autoOpen: true);
        FormatMemory a = Create();
        Assert.Equal(1, a.Forget(Size, ".raw"));

        // B は消したサイズを自動で開かず(ダイアログを出す)、B の保存でも記憶は復活しない
        Assert.Null(b.History.FindAutoOpenFormat(Size, ".raw"));
        _now = T0.AddMinutes(1);
        b.RememberLoaded(@"D:\cap\other.raw", OtherSize, OtherFmt(), autoOpen: true);

        IReadOnlyFormatHistory next = Create().History;
        Assert.Empty(next.Find(Size, ".raw"));
        Assert.Single(next.Find(OtherSize, ".raw"));
    }

    [Fact]
    public void TwoInstances_RecordsFromBoth_AreKept()
    {
        // B が起動した後に A で記録した記憶は、B の保存で消えない
        FormatMemory b = Create();
        FormatMemory a = Create();
        a.RememberLoaded(Path, Size, Fmt(BayerPattern.Rggb), autoOpen: true);
        _now = T0.AddMinutes(1);
        b.RememberLoaded(@"D:\cap\other.raw", OtherSize, OtherFmt(), autoOpen: true);
        _now = T0.AddMinutes(2);
        Assert.True(b.Correct(@"D:\cap\shot_0003.raw", Size, Fmt(BayerPattern.Rggb), Fmt(BayerPattern.Bggr)));

        IReadOnlyFormatHistory next = Create().History;
        Assert.Equal(Fmt(BayerPattern.Bggr), Assert.Single(next.Find(Size, ".raw")).Format);
        Assert.Single(next.Find(OtherSize, ".raw"));
        Assert.Equal(2, a.History.Entries.Count);
    }

    [Fact]
    public void History_IsReadOnly_SoChangesGoThroughTheMethodsThatSave()
    {
        // 残課題 2026-10-02 M2。History が書き換えられる FormatHistory をそのまま返していたので、直接記録できるのに
        // 保存されず(次の起動・他のインスタンスに残らない)、他のインスタンスの保存を読み直すと消えた。
        // 読み取り専用で見せ、書き換えは保存まで行う RememberLoaded・Correct・Forget だけにする
        Type type = typeof(FormatMemory)
            .GetProperty(nameof(FormatMemory.History), BindingFlags.Instance | BindingFlags.NonPublic)!.PropertyType;

        Assert.False(typeof(FormatHistory).IsAssignableFrom(type));
        Assert.DoesNotContain(type.GetMethods(), m => m.Name is nameof(FormatHistory.Record)
            or nameof(FormatHistory.Replace) or nameof(FormatHistory.Remove));
    }

    [Fact]
    public void SaveFailure_KeepsMemoryAndWarns()
    {
        // 保存先がフォルダになっていて置き換えられない
        var store = new FormatHistoryStore(_directory);
        Directory.CreateDirectory(store.FilePath);
        FormatMemory memory = Create();

        memory.RememberLoaded(Path, Size, Fmt(), autoOpen: true);

        Assert.Single(memory.History.Entries);
        Assert.Contains("保存できませんでした", Assert.Single(_warnings));
    }

    [Fact]
    public void SaveFailure_IsSavedWithTheNextChange()
    {
        // 保存できなかった変更は捨てず、次の変更のときに最新の記憶へ当て直して一緒に保存する
        var store = new FormatHistoryStore(_directory);
        Directory.CreateDirectory(store.FilePath);
        FormatMemory memory = Create();
        memory.RememberLoaded(Path, Size, Fmt(), autoOpen: true);
        Assert.Single(_warnings);

        Directory.Delete(store.FilePath);
        _now = T0.AddMinutes(1);
        memory.RememberLoaded(@"D:\cap\other.raw", OtherSize, OtherFmt(), autoOpen: true);

        IReadOnlyFormatHistory saved = Create().History;
        Assert.Single(saved.Find(Size, ".raw"));
        Assert.Single(saved.Find(OtherSize, ".raw"));
        Assert.Single(_warnings);
    }
}
