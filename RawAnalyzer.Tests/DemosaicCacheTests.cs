using RawAnalyzer.App.Rendering;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// カラー現像描画のデモザイク結果キャッシュ。
/// </summary>
public class DemosaicCacheTests
{
    private const int Width = 16;
    private const int Height = 12;

    private static RawImage MakeBayerImage()
    {
        var codes = new ushort[Width * Height];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)((i * 4099) % 65536);
        }

        var format = new RawFormat
        {
            Width = Width,
            Height = Height,
            BitDepth = 16,
            Bayer = BayerPattern.Rggb,
        };
        return RawImage.FromPixels(format, codes);
    }

    private static byte[] Render(RawImage image, DevelopLuts luts, DemosaicCache? cache)
    {
        var request = new RenderRequest
        {
            Source = new RawImageRenderSource(image, 0),
            Lut = DisplayLut.Create(new DisplayParameters()),
            Mode = ViewportDisplayMode.ColorDevelop,
            Pattern = BayerPattern.Rggb,
            DevelopLuts = luts,
            DemosaicCache = cache,
        };

        var destination = new byte[Width * Height * 4];
        ViewportRenderer.Render(
            request, zoom: 1.0, originX: 0, originY: 0, Width, Height, destination, default);
        return destination;
    }

    [Fact]
    public void CachedRender_MatchesUncachedRender()
    {
        using RawImage image = MakeBayerImage();
        var luts = DevelopLuts.Create(new DevelopParameters(Gamma: 1.0));
        var cache = new DemosaicCache();

        byte[] uncached = Render(image, luts, cache: null);
        byte[] first = Render(image, luts, cache);
        byte[] second = Render(image, luts, cache); // 2回目はキャッシュ命中

        Assert.Equal(uncached, first);
        Assert.Equal(uncached, second);
    }

    [Fact]
    public void CachedRender_StillAppliesChangedLut()
    {
        // キャッシュするのはデモザイクまで。LUTの変更は毎回反映されないといけない
        using RawImage image = MakeBayerImage();
        var cache = new DemosaicCache();

        byte[] flat = Render(image, DevelopLuts.Create(new DevelopParameters(Gamma: 1.0)), cache);
        byte[] gained = Render(
            image, DevelopLuts.Create(new DevelopParameters(Gamma: 1.0, Gain: 2.0)), cache);
        byte[] expected = Render(
            image, DevelopLuts.Create(new DevelopParameters(Gamma: 1.0, Gain: 2.0)), cache: null);

        Assert.NotEqual(flat, gained);
        Assert.Equal(expected, gained);
    }

    [Fact]
    public void Cache_IsKeyedByImageIdentity()
    {
        // 別画像に切り替わったら、前の画像のデモザイク結果を使ってはいけない
        using RawImage first = MakeBayerImage();
        using RawImage second = RawImage.FromPixels(
            new RawFormat
            {
                Width = Width,
                Height = Height,
                BitDepth = 16,
                Bayer = BayerPattern.Rggb,
            },
            new ushort[Width * Height]);

        var luts = DevelopLuts.Create(new DevelopParameters(Gamma: 1.0));
        var cache = new DemosaicCache();

        Render(first, luts, cache);
        byte[] actual = Render(second, luts, cache);
        byte[] expected = Render(second, luts, cache: null);

        Assert.Equal(expected, actual);
    }
}
