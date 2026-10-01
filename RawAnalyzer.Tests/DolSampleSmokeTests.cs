using RawAnalyzer.Core;
using Xunit;
using Xunit.Abstractions;

namespace RawAnalyzer.Tests;

/// <summary>
/// tools/Generate-DolSample.ps1 が生成した実サンプルに対する分割・合成スモークテスト。
/// 環境変数 RAWANALYZER_DOL_SAMPLE_PATH にファイルパスを設定した場合のみ実行され、
/// 未設定ならスキップとして報告される。
/// </summary>
public class DolSampleSmokeTests
{
    /// <summary>DOLサンプルのパスを保持する環境変数名。</summary>
    private const string PathVariable = "RAWANALYZER_DOL_SAMPLE_PATH";

    private readonly ITestOutputHelper _output;

    public DolSampleSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SampleFileFact(PathVariable)]
    public void DolSample_SplitAndMerge_RecoverHdrScene()
    {
        string path = SampleFileFactAttribute.GetPath(PathVariable)!;

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
        try
        {
            Assert.Equal(2, frames.Count);
            Assert.All(frames, f => Assert.Equal(1080, f.Height));

            // 中央スポット(半径60)はシーン輝度 = 露光比×4095。生成器は
            // 長秒 = clip(シーン, 4095) = 4095、短秒 = シーン/16 = 4095 と書くので、
            // 短秒もちょうど上限に達する(12bit → 16bit 正規化で 4095<<4)
            ushort longCenter = frames[0].GetPixel(960, 540);
            ushort shortCenter = frames[1].GetPixel(960, 540);
            Assert.Equal(4095 << 4, longCenter);
            Assert.Equal(4095 << 4, shortCenter);

            // 合成: 長秒が飽和しているので短秒×露光比 = (4095<<4)×16 で復元される
            HdrImage merged = HdrMerger.Merge(frames, new HdrMergeParameters(ExposureRatio: 16));
            float center = merged.Pixels[540 * 1920 + 960];
            _output.WriteLine($"merged center = {center}, FullScale = {merged.FullScale}");
            Assert.Equal((4095 << 4) * 16f, center);

            // 左端寄りの x=50(シーン = ウェッジ 1707 × リングの係数 0.68 ≈ 1159.4)は長秒が飽和しない暗部。
            // 生成器は長秒 1159・短秒 72(切り捨て)と書くので、分割の順(偶数行が長秒)もここで確かめる。
            // 合成は長秒をそのまま使う(短秒×露光比 72×16 = 1152 とは切り捨てのぶん値が違う)
            Assert.Equal(1159 << 4, frames[0].GetPixel(50, 540));
            Assert.Equal(72 << 4, frames[1].GetPixel(50, 540));
            Assert.Equal(1159f * 16, merged.Pixels[540 * 1920 + 50]);
        }
        finally
        {
            foreach (RawImage frame in frames)
            {
                frame.Dispose();
            }
        }
    }
}
