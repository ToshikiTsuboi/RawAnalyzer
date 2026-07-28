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
    public void Load_MissingFile_ReturnsEmpty()
    {
        var store = new FormatPresetStore(_directory);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void SaveAndLoad_RoundTripsPresets()
    {
        var store = new FormatPresetStore(_directory);
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

        var loaded = new FormatPresetStore(_directory).Load();
        Assert.Equal(presets.Count, loaded.Count);
        foreach ((string name, RawFormat format) in presets)
        {
            Assert.Equal(format, loaded[name]);
        }
    }

    [Fact]
    public void Save_SerializesEnumsAsStrings()
    {
        var store = new FormatPresetStore(_directory);
        store.Save(new Dictionary<string, RawFormat>
        {
            ["p"] = new RawFormat { Width = 1, Height = 1, Bayer = BayerPattern.Rggb },
        });

        string json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"Rggb\"", json);
    }

    [Fact]
    public void Save_ReplacesAtomicallyAndLeavesNoTempFile()
    {
        var store = new FormatPresetStore(_directory);
        store.Save(new Dictionary<string, RawFormat>
        {
            ["first"] = new RawFormat { Width = 8, Height = 8 },
        });
        store.Save(new Dictionary<string, RawFormat>
        {
            ["second"] = new RawFormat { Width = 16, Height = 16 },
        });

        Assert.False(File.Exists(store.FilePath + ".tmp"), "一時ファイルが残らないこと");
        IReadOnlyDictionary<string, RawFormat> loaded = store.Load();
        Assert.True(loaded.ContainsKey("second"));
        Assert.False(loaded.ContainsKey("first"));
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
    [InlineData("Dol")]
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

        // 旧挙動(フレーム数から推定)が保たれること
        Assert.Equal(HdrMode.FrameSequential, HdrSplitter.ResolveLayout(format, format.HdrStages));
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

    [Fact]
    public void Save_WritesNewHdrModeName()
    {
        var store = new FormatPresetStore(_directory);
        store.Save(new Dictionary<string, RawFormat>
        {
            ["p"] = new RawFormat
            {
                Width = 8, Height = 8, Hdr = HdrMode.LineInterleaved,
            },
        });

        Assert.Contains("\"LineInterleaved\"", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void LoadOrQuarantine_ValidFile_LeavesFileIntact()
    {
        var store = new FormatPresetStore(_directory);
        store.Save(new Dictionary<string, RawFormat>
        {
            ["p"] = new RawFormat { Width = 4, Height = 2, BitDepth = 10 },
        });

        IReadOnlyDictionary<string, RawFormat> loaded =
            store.LoadOrQuarantine(out bool corrupted);

        Assert.False(corrupted);
        Assert.Single(loaded);
        Assert.True(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.BackupPath));
    }
}
