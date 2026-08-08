using System.Diagnostics;
using RawAnalyzer.Core;
using Xunit;
using Xunit.Abstractions;

namespace RawAnalyzer.Tests;

/// <summary>
/// 10億画素クラスの合成Raw(tools/Generate-TestRaw.ps1で生成)に対するスモークテスト。
/// 環境変数 RAWANALYZER_GIGAPIXEL_PATH にファイルパスを設定した場合のみ実行される。
/// </summary>
public class GigapixelSmokeTests
{
    private readonly ITestOutputHelper _output;

    public GigapixelSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>環境変数で指定されたギガピクセルRawのパス。未設定ならnull。</summary>
    internal static string? GigapixelPath
    {
        get
        {
            string? path = Environment.GetEnvironmentVariable("RAWANALYZER_GIGAPIXEL_PATH");
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }
    }

    [GigapixelFact]
    public void Gigapixel_LoadPyramidAndRegionReads_CompleteQuickly()
    {
        string path = GigapixelPath!;

        var format = new RawFormat { Width = 32768, Height = 32768, BitDepth = 12 };

        // 1) MMFロードは即時であること
        var sw = Stopwatch.StartNew();
        using RawImage image = RawLoader.Load(path, format);
        sw.Stop();
        _output.WriteLine($"Load (MMF): {sw.ElapsedMilliseconds} ms");
        Assert.True(image.IsMemoryMapped, "10億画素はMMF参照になること");
        Assert.True(sw.ElapsedMilliseconds < 2000, "MMFロードは2秒以内");

        // 2) ビューポート相当の領域読み出し(パン操作相当)が高速であること
        var region = new ushort[1920 * 1080];
        sw.Restart();
        image.CopyRegion(0, 12345, 23456, 1920, 1080, region);
        sw.Stop();
        _output.WriteLine($"CopyRegion 1920x1080 (L0/MMF): {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 3000, "L0領域読み出しは3秒以内");

        // 3) ピラミッド生成(バックグラウンド処理相当)
        sw.Restart();
        TilePyramid pyramid = TilePyramid.Create(image);
        sw.Stop();
        _output.WriteLine($"Pyramid build: {sw.ElapsedMilliseconds} ms, " +
            $"levels=[{string.Join(",", pyramid.Levels.Select(l => l.Factor))}]");
        Assert.True(sw.Elapsed.TotalMinutes < 3, "ピラミッド生成は3分以内");

        // 1/2は1億画素超なのでスキップされ、1/4以降が生成される
        Assert.Null(pyramid.GetLevel(2));
        Assert.NotNull(pyramid.GetLevel(4));
        Assert.NotNull(pyramid.GetLevel(64));

        // 4) 全体表示相当: 最粗レベルからの読み出しは瞬時であること
        PyramidLevel coarsest = pyramid.Levels[^1];
        var thumb = new ushort[coarsest.Width * coarsest.Height];
        sw.Restart();
        coarsest.CopyRegion(0, 0, coarsest.Width, coarsest.Height, thumb);
        sw.Stop();
        _output.WriteLine($"Coarsest level read ({coarsest.Width}x{coarsest.Height}): {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 200);

        // 5) 内容の妥当性: グリッド線(1024間隔)は最大値、それ以外は勾配値
        //    (x=0行はグリッド線なので raw code 4095 → 16bit正規化で 0xFFF0)
        Assert.Equal(0xFFF0, image.GetPixel(0, 0));
        Assert.Equal(0xFFF0, image.GetPixel(1024, 500));
    }
}
