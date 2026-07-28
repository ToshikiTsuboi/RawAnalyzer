using RawAnalyzer.Core;
using Xunit;
using Xunit.Abstractions;

namespace RawAnalyzer.Tests;

/// <summary>
/// tools/Generate-DolSample.ps1 が生成した実サンプルに対する分割・合成スモークテスト。
/// 環境変数 RAWANALYZER_DOL_SAMPLE_PATH にファイルパスを設定した場合のみ実行される。
/// </summary>
public class DolSampleSmokeTests
{
    private readonly ITestOutputHelper _output;

    public DolSampleSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DolSample_SplitAndMerge_RecoverHdrScene()
    {
        string? path = Environment.GetEnvironmentVariable("RAWANALYZER_DOL_SAMPLE_PATH");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _output.WriteLine("RAWANALYZER_DOL_SAMPLE_PATH 未設定のためスキップ");
            return;
        }

        var format = new RawFormat
        {
            Width = 1920,
            Height = 2160,
            BitDepth = 12,
            Hdr = HdrMode.Auto,
            HdrStages = 2,
            ExposureRatio = 16,
        };
        using RawImage image = RawLoader.Load(path, format);

        // 分割: 長秒/短秒 各1920×1080
        IReadOnlyList<RawImage> frames = HdrSplitter.Split(image);
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(1080, f.Height));

        // 中央スポット: 長秒は飽和、短秒は非飽和
        ushort longCenter = frames[0].GetPixel(960, 540);
        ushort shortCenter = frames[1].GetPixel(960, 540);
        Assert.Equal(4095, longCenter >> 4);
        Assert.Equal(4095 << 4, shortCenter);

        // 合成: スポットが短秒×露光比で復元される
        HdrImage merged = HdrMerger.Merge(frames, new HdrMergeParameters(ExposureRatio: 16));
        float center = merged.Pixels[540 * 1920 + 960];
        _output.WriteLine($"merged center = {center}, FullScale = {merged.FullScale}");
        Assert.True(center > 1_000_000, $"中央スポットがHDR復元されていること: {center}");

        // 左端(暗部)は長秒データがそのまま使われ低輝度
        Assert.True(merged.Pixels[540 * 1920] < 65535);

        foreach (RawImage frame in frames)
        {
            frame.Dispose();
        }
    }
}
