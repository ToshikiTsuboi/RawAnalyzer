using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class FormatPresetStoreTests : IDisposable
{
    private readonly string _directory;

    public FormatPresetStoreTests()
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
    public void SaveAndLoad_RoundTripsPresets()
    {
        // アプリは Load() を直接呼ばず、LoadOrQuarantine だけを使う
        var store = new FormatPresetStore(_directory);
        Assert.Empty(store.LoadOrQuarantine(out bool corrupted));
        Assert.False(corrupted, "ファイル未存在は破損ではないこと");

        var presets = new Dictionary<string, RawFormat>
        {
            ["IMX477 12bit"] = new RawFormat
            {
                Width = 4056,
                Height = 3040,
                BitDepth = 12,
                Packing = BitPacking.Lsb,
                Endianness = Endianness.Little,
                Bayer = BayerPattern.Rggb,
            },
            ["DOL 2frame"] = new RawFormat
            {
                Width = 1920,
                Height = 1080,
                BitDepth = 10,
                Packing = BitPacking.Msb,
                Endianness = Endianness.Big,
                HeaderOffset = 512,
                FrameCount = 2,
                Bayer = BayerPattern.Gbrg,
                Hdr = HdrMode.Auto,
            },
        };

        store.Save(presets);

        IReadOnlyDictionary<string, RawFormat> loaded =
            new FormatPresetStore(_directory).LoadOrQuarantine(out corrupted);
        Assert.False(corrupted);
        Assert.True(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.BackupPath), "正常なファイルは退避されないこと");
        Assert.Equal(presets.Count, loaded.Count);
        foreach ((string name, RawFormat format) in presets)
        {
            Assert.Equal(format, loaded[name]);
        }
    }

    [Fact]
    public void Update_AppliesChangeToLatestSavedPresetsNotToOwnSnapshot()
    {
        // 複数起動: 先に読んだインスタンス(A)の変更が、後から別のインスタンス(B)が保存したプリセットを消さない
        var a = new FormatPresetStore(_directory);
        IReadOnlyDictionary<string, RawFormat> snapshot = a.LoadOrQuarantine(out _);
        Assert.Empty(a.Update(_ => false));
        Assert.False(File.Exists(a.FilePath), "変更がなければ書かないこと");
        new FormatPresetStore(_directory).Update(presets =>
        {
            presets["B"] = new RawFormat { Width = 2, Height = 2 };
            return true;
        });

        IReadOnlyDictionary<string, RawFormat> updated = a.Update(presets =>
        {
            presets["A"] = new RawFormat { Width = 3, Height = 3 };
            return true;
        });

        Assert.Empty(snapshot);
        Assert.Equal(["A", "B"], updated.Keys.Order());
        Assert.Equal(["A", "B"], new FormatPresetStore(_directory).Load().Keys.Order());
    }

    [Fact]
    public void LoadOrQuarantine_CorruptedFile_MovesToBackupAndReturnsEmpty()
    {
        // 破損を握りつぶすと、次に1件保存したときに辞書全体が上書きされ
        // 全プリセットが復旧不能に消える
        var store = new FormatPresetStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, "{ this is not json");

        IReadOnlyDictionary<string, RawFormat> loaded =
            store.LoadOrQuarantine(out bool corrupted);

        Assert.True(corrupted);
        Assert.Empty(loaded);
        Assert.False(File.Exists(store.FilePath));
        Assert.True(File.Exists(store.BackupPath));
        Assert.Contains("this is not json", File.ReadAllText(store.BackupPath));
    }

    [Theory]
    [InlineData("Staggered")]
    public void Load_LegacyHdrModeName_MapsToAuto(string legacy)
    {
        // 旧版のプリセットは Dol / Staggered を保存していた。
        // どちらもフレーム数から推定する挙動だったので Auto に写す。
        var store = new FormatPresetStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath,
            $$"""
            {
              "旧プリセット": {
                "Width": 1920, "Height": 1080, "BitDepth": 12,
                "FrameCount": 2, "Hdr": "{{legacy}}", "HdrStages": 2
              }
            }
            """);

        IReadOnlyDictionary<string, RawFormat> loaded = store.Load();

        Assert.Single(loaded);
        RawFormat format = loaded["旧プリセット"];
        Assert.Equal(HdrMode.Auto, format.Hdr);
    }

    [Fact]
    public void Load_UnknownHdrModeName_FallsBackToNone()
    {
        var store = new FormatPresetStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath,
            """
            { "p": { "Width": 8, "Height": 8, "Hdr": "SomethingElse" } }
            """);

        Assert.Equal(HdrMode.None, store.Load()["p"].Hdr);
    }
}
