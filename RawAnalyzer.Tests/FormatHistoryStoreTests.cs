using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// サイズ別フォーマット記憶の保存(format-history.json)。
/// </summary>
public class FormatHistoryStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 1, 12, 34, 56, DateTimeKind.Utc);
    private readonly string _directory;

    public FormatHistoryStoreTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "RawAnalyzerTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void SaveAndLoad_RoundTripsEntriesInOrder()
    {
        var store = new FormatHistoryStore(_directory);
        Assert.Equal(Path.Combine(_directory, "format-history.json"), store.FilePath);
        Assert.Empty(store.LoadOrQuarantine(out bool corrupted).Entries);
        Assert.False(corrupted, "ファイル未存在は破損ではないこと");

        var history = new FormatHistory();
        RawFormat hdr = new()
        {
            Width = 1920, Height = 1080, BitDepth = 10, Packing = BitPacking.Msb,
            Endianness = Endianness.Big, HeaderOffset = 512, FrameCount = 2,
            Bayer = BayerPattern.Gbrg, Hdr = HdrMode.FrameSequential, ExposureRatio = 4.5,
            HdrLineBlock = 1, HdrRowOffset = -2,
        };
        RawFormat mono = new() { Width = 640, Height = 480, BitDepth = 12 };
        history.Record(512 + (1920L * 1080 * 2 * 2), ".raw", hdr, autoOpen: true, T0);
        history.Record(640 * 480 * 2, ".bin", mono, autoOpen: false, T0.AddHours(1));

        store.Save(history);
        FormatHistory loaded = new FormatHistoryStore(_directory).LoadOrQuarantine(out corrupted);

        Assert.False(corrupted);
        Assert.False(File.Exists(store.BackupPath));
        Assert.Equal(history.Entries, loaded.Entries);
        Assert.Equal(DateTimeKind.Utc, loaded.Entries[0].LastUsedUtc.Kind);
        Assert.Null(loaded.FindAutoOpenFormat(640 * 480 * 2, ".bin")); // 自動適用オフも保たれる
        Assert.Equal(hdr, loaded.FindAutoOpenFormat(512 + (1920L * 1080 * 2 * 2), ".raw"));
    }

    [Fact]
    public void Save_UsesPresetJsonConventions()
    {
        var store = new FormatHistoryStore(_directory);
        var history = new FormatHistory();
        history.Record(64, ".raw", new RawFormat
        {
            Width = 4, Height = 8, BitDepth = 16, Packing = BitPacking.Msb,
            Endianness = Endianness.Big, Bayer = BayerPattern.Rggb, Hdr = HdrMode.LineInterleaved,
        }, null, T0);

        store.Save(history);

        // 列挙型はプリセットと同じく文字列で保存する(手で読める・並びを変えても壊れない)。
        // 文字列化は型に付けた変換器の働きで、プリセット(FormatPresetStore)も同じ SerializerOptions で書く
        string json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"Rggb\"", json);
        Assert.Contains("\"LineInterleaved\"", json);
        Assert.Contains("\"Msb\"", json);
        Assert.Contains("\"Big\"", json);
        Assert.Contains("\"Version\": 1", json);
    }

    [Fact]
    public void Load_LegacyHdrNameAndInvalidEntries_AreHandledLikePresets()
    {
        var store = new FormatHistoryStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath,
            """
            {
              "Version": 1,
              "Entries": [
                { "FileSize": 8294400, "Extension": ".RAW", "LastUsedUtc": "2026-09-01T00:00:00Z",
                  "AutoOpen": true,
                  "Format": { "Width": 1920, "Height": 1080, "BitDepth": 12, "FrameCount": 2,
                              "Hdr": "Dol", "HdrStages": 2 } },
                { "FileSize": 10, "Extension": ".raw", "LastUsedUtc": "2026-09-02T00:00:00Z",
                  "Format": { "Width": 1920, "Height": 1080 } },
                null
              ]
            }
            """);

        FormatHistory loaded = store.LoadOrQuarantine(out bool corrupted);

        // 旧名 Dol は Auto として読む。そのサイズで開けない記憶は捨てる
        Assert.False(corrupted);
        FormatHistoryEntry entry = Assert.Single(loaded.Entries);
        Assert.Equal(".raw", entry.Extension);
        Assert.Equal(HdrMode.Auto, entry.Format.Hdr);
    }

    [Fact]
    public void Update_AppliesChangeToLatestSavedHistoryNotToOwnSnapshot()
    {
        // 複数起動: 先に読んだストア(B)が、後から別のストア(A)が保存した記憶を巻き戻さない
        var b = new FormatHistoryStore(_directory);
        FormatHistory snapshot = b.LoadOrQuarantine(out _);
        var a = new FormatHistoryStore(_directory);
        FormatHistory saved = a.LoadOrQuarantine(out _);
        saved.Record(8, ".raw", new RawFormat { Width = 2, Height = 2 }, null, T0);
        a.Save(saved);

        FormatHistory updated = b.Update(history =>
        {
            history.Record(18, ".raw", new RawFormat { Width = 3, Height = 3 }, null, T0.AddMinutes(1));
            return true;
        });

        Assert.Empty(snapshot.Entries);
        Assert.Equal(2, updated.Entries.Count);
        Assert.Equal(2, new FormatHistoryStore(_directory).Load().Entries.Count);
    }

    [Fact]
    public void Load_FileWithUtf8Bom_IsRead()
    {
        var store = new FormatHistoryStore(_directory);
        var history = new FormatHistory();
        history.Record(8, ".raw", new RawFormat { Width = 2, Height = 2 }, null, T0);
        store.Save(history);
        File.WriteAllText(store.FilePath, File.ReadAllText(store.FilePath), new System.Text.UTF8Encoding(true));

        Assert.Single(new FormatHistoryStore(_directory).Load().Entries);
    }

    [Fact]
    public void Update_WithoutChange_DoesNotWrite()
    {
        var store = new FormatHistoryStore(_directory);

        FormatHistory result = store.Update(_ => false);

        Assert.Empty(result.Entries);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void ReloadIfChanged_ReturnsHistoryOnlyAfterAnotherStoreSaves()
    {
        var b = new FormatHistoryStore(_directory);
        b.LoadOrQuarantine(out _);
        Assert.Null(b.ReloadIfChanged());

        var a = new FormatHistoryStore(_directory);
        a.Update(history =>
        {
            history.Record(8, ".raw", new RawFormat { Width = 2, Height = 2 }, null, T0);
            return true;
        });

        Assert.Single(b.ReloadIfChanged()!.Entries);
        Assert.Null(b.ReloadIfChanged());
        Assert.Null(a.ReloadIfChanged()); // 自分の保存は読み直さない
    }

    [Fact]
    public async Task LoadOrQuarantine_WhileAnotherInstanceIsSaving_WaitsForIt()
    {
        // 読み込みも他のインスタンスの保存と排他する(置き換えの途中に読んで、読めない・古い内容を使わない)
        var store = new FormatHistoryStore(_directory);
        Directory.CreateDirectory(_directory);
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task other = Task.Factory.StartNew(() =>
        {
            // ミューテックスは取得したスレッドで解放するので、待たずに同じスレッドで書く
            using (InterProcessFileLock.Acquire(store.FilePath))
            {
                holding.Set();
                release.Wait();
                var history = new FormatHistory();
                history.Record(8, ".raw", new RawFormat { Width = 2, Height = 2 }, null, T0);
                new FormatHistoryStore(_directory).Save(history);
            }
        }, TaskCreationOptions.LongRunning);
        holding.Wait();

        Task<FormatHistory> load = Task.Run(() => store.LoadOrQuarantine(out _));
        await Task.WhenAny(load, Task.Delay(200));
        release.Set();
        await other;

        Assert.Single((await load).Entries);
    }

    [Fact]
    public void LoadOrQuarantine_CorruptedFile_MovesToBackupAndStartsEmpty()
    {
        // 握りつぶして空から始めると、次の保存で記憶が上書きされ復旧できなくなる
        var store = new FormatHistoryStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, "{ \"Entries\": [ { broken");

        FormatHistory loaded = store.LoadOrQuarantine(out bool corrupted);

        Assert.True(corrupted);
        Assert.Empty(loaded.Entries);
        Assert.False(File.Exists(store.FilePath));
        Assert.Contains("broken", File.ReadAllText(store.BackupPath));

        // 空から始めて保存し直せる
        loaded.Record(8, ".raw", new RawFormat { Width = 2, Height = 2 }, null, T0);
        store.Save(loaded);
        Assert.Single(new FormatHistoryStore(_directory).Load().Entries);
    }
}
