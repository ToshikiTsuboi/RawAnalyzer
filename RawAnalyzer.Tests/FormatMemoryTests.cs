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

    private FormatMemory Create()
    {
        return new FormatMemory(new FormatHistoryStore(_directory), _warnings.Add, () => _now);
    }

    [Fact]
    public void RememberLoaded_RecordsAndSavesEveryTime()
    {
        FormatMemory memory = Create();

        memory.RememberLoaded(Path, Size, Fmt(BayerPattern.Rggb), autoOpen: true);

        FormatHistory reloaded = Create().History;
        FormatHistoryEntry entry = Assert.Single(reloaded.Entries);
        Assert.Equal((Size, ".raw", T0, true), (entry.FileSize, entry.Extension, entry.LastUsedUtc, entry.AutoOpen));
        Assert.Equal(Fmt(BayerPattern.Rggb), entry.Format);
        Assert.Empty(_warnings);

        // 次の記録でも保存し直す(オフで確定したものは記憶するが自動では開かない)
        _now = T0.AddMinutes(1);
        memory.RememberLoaded(Path, Size, Fmt(BayerPattern.Bggr), autoOpen: false);
        FormatHistory again = Create().History;
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

        FormatHistory reloaded = Create().History;
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
}
