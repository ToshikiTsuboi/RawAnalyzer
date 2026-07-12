using RawViewer.Core;
using Xunit;

namespace RawViewer.Tests;

public class FormatPresetStoreTests : IDisposable
{
    private readonly string _directory;

    public FormatPresetStoreTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "RawViewerTests", Guid.NewGuid().ToString("N"));
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
                Hdr = HdrMode.Dol,
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
}
