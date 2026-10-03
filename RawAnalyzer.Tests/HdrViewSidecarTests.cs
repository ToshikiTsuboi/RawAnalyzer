using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// HDR派生ビュー(分割・合成)の表示中に保存したとき、保存の付随テキストへ書く来歴の節
/// (MainWindow の WriteProcessingSidecarAsync が使う)。
/// </summary>
public class HdrViewSidecarTests
{
    [Fact]
    public void Merge_RecordsDerivedViewAndBlackSubtractedInMerge()
    {
        // 以前は合成ビューから保存しても派生ビュー由来であることが書かれず、合成ビューの表示黒点は減算済みの0
        // なので、合成で減算した黒点も記録から消えていた。黒点は合成結果が持つ合成に使った値を書く
        // (元12bitの黒コード64=内部1024、2段・露光比16)
        var frameFormat = new RawFormat { Width = 2, Height = 2, BitDepth = 12, Bayer = BayerPattern.Rggb };
        using RawImage longFrame = TestImages.FromCodes([1000, 1000, 1000, 1000], frameFormat);
        using RawImage shortFrame = TestImages.FromCodes([62, 62, 62, 62], frameFormat);
        HdrImage merged = HdrMerger.Merge([longFrame, shortFrame], new HdrMergeParameters(16, 64 << 4));
        using RawImage quantized = merged.ToRawImage16();

        // 行交互の25フレームのうち3フレーム目から作った
        var source = frameFormat with
        {
            Height = 4, FrameCount = 25, Hdr = HdrMode.LineInterleaved, HdrStages = 2, ExposureRatio = 16,
        };

        string text = HdrViewSidecar.DescribeMerge(
            quantized.Format, merged, source, sourceFrame: 2, floatRawOutput: false);

        Assert.StartsWith("[HDR派生ビュー]", text);
        Assert.Contains("種類: HDR合成 (2段, 露光比 16)", text);
        Assert.Contains("元画像のフレーム: 3/25", text);
        Assert.Contains("合成で減算した黒点: 1024 (12bitのcode 64)", text);
        Assert.Contains("合成域のフルスケール: 1032176", text); // (65535 − 1024) × 16
        Assert.Contains("派生画像: 2×2 · 16bit · Bayer Rggb", text);
        Assert.Contains("量子化の1LSB: 15.7 (元素材比で損失なし)", text); // 1032176 / 65535 = 15.74999…
    }

    [Fact]
    public void Merge_NotesQuantizationLoss_AndUnquantizedFloatRawOutput()
    {
        // 14bit・2段・露光比16は16bitへの量子化で約2bit落ちる(合成ビューの表示と同じ判定)。
        // float raw で保存したときは量子化前の値なので、量子化の1LSBを書かない
        var frameFormat = new RawFormat { Width = 2, Height = 2, BitDepth = 14 };
        using RawImage longFrame = TestImages.FromCodes(new ushort[4], frameFormat);
        using RawImage shortFrame = TestImages.FromCodes(new ushort[4], frameFormat);
        HdrImage merged = HdrMerger.Merge([longFrame, shortFrame], new HdrMergeParameters(16));
        using RawImage quantized = merged.ToRawImage16();
        var source = frameFormat with { FrameCount = 2, Hdr = HdrMode.FrameSequential, HdrStages = 2 };

        string sixteenBit = HdrViewSidecar.DescribeMerge(
            quantized.Format, merged, source, sourceFrame: 0, floatRawOutput: false);
        string floatRaw = HdrViewSidecar.DescribeMerge(
            quantized.Format, merged, source, sourceFrame: 0, floatRawOutput: true);

        Assert.Contains("量子化の1LSB: 16.0 (元素材比 約2bit損失)", sixteenBit);
        Assert.Contains("保存した値: 量子化前の合成値", floatRaw);
        Assert.DoesNotContain("量子化の1LSB", floatRaw);
    }

    [Fact]
    public void Merge_NotesBelowBlackClampedInQuantizedImage_KeptNegativeInFloatRaw()
    {
        // 合成値は黒点を減算済みで、黒点未満は負になる。float raw は負値のまま残すが、16bitの派生画像は
        // 負値を表せないので0に切り詰める(暗部の統計が偏る)。どちらの値を保存したかを書く
        var frameFormat = new RawFormat { Width = 2, Height = 2, BitDepth = 12 };
        using RawImage longFrame = TestImages.FromCodes([60, 70, 60, 70], frameFormat);
        using RawImage shortFrame = TestImages.FromCodes([64, 64, 64, 64], frameFormat);
        HdrImage merged = HdrMerger.Merge([longFrame, shortFrame], new HdrMergeParameters(16, 64 << 4));
        using RawImage quantized = merged.ToRawImage16();
        var source = frameFormat with { FrameCount = 2, Hdr = HdrMode.FrameSequential, HdrStages = 2 };

        string sixteenBit = HdrViewSidecar.DescribeMerge(
            quantized.Format, merged, source, sourceFrame: 0, floatRawOutput: false);
        string floatRaw = HdrViewSidecar.DescribeMerge(
            quantized.Format, merged, source, sourceFrame: 0, floatRawOutput: true);

        Assert.Contains("(合成域のフルスケールを65535へ量子化・黒点未満は0に切り詰め)", sixteenBit);
        Assert.Contains("・黒点減算済み・黒点未満は負値のまま)", floatRaw);
    }

    [Fact]
    public void Split_RecordsDerivedView_FrameSequentialHasNoSourceFrame()
    {
        // 分割ビューから保存した画像は各段(長秒→短秒)を左から並置したもの。フレーム連結はフレームそのものが
        // 各露光で全フレームを使うので、元画像のフレームは書かない(行交互の複数フレームだけ書く)
        var derived = new RawFormat { Width = 12, Height = 2, BitDepth = 12, Bayer = BayerPattern.Rggb };
        var source = new RawFormat
        {
            Width = 4, Height = 2, BitDepth = 12, Bayer = BayerPattern.Rggb, FrameCount = 3,
            Hdr = HdrMode.FrameSequential, HdrStages = 3,
        };

        string text = HdrViewSidecar.DescribeSplit(derived, stages: 3, source, sourceFrame: 1);

        Assert.StartsWith("[HDR派生ビュー]", text);
        Assert.Contains("種類: HDR分割 (左: 長秒 → 右: 短秒, 3段)", text);
        Assert.Contains("派生画像: 12×2 · 12bit · Bayer Rggb (各段 4×2 を左から並置)", text);
        Assert.DoesNotContain("元画像のフレーム", text);
        Assert.DoesNotContain("黒点", text); // 分割は黒レベルを減算しない
    }

    [Fact]
    public void Split_RecordsStagesWidthExposureAndBayerCountedFromEachStage()
    {
        // 分割ビューから保存した画像は各段を左から並置したもの。開き直すと1枚の画像に見えるので、段の数・段の幅・
        // 各段の露光(左から長秒 → 短秒、フォーマットの露光比)と、各段の Bayer は段の左端から数えること(段の幅が
        // 奇数だと並置画像の列の偶奇と逆になる段がある)を書く。行交互 5×8・2段・露光比 16 → 段の幅 5 の 10×4
        var derived = new RawFormat { Width = 10, Height = 4, BitDepth = 12, Bayer = BayerPattern.Rggb };
        var source = new RawFormat
        {
            Width = 5, Height = 8, BitDepth = 12, Bayer = BayerPattern.Rggb, Hdr = HdrMode.LineInterleaved,
            HdrStages = 2, ExposureRatio = 16,
        };

        string text = HdrViewSidecar.DescribeSplit(derived, stages: 2, source, sourceFrame: 0, segmentWidth: 5);

        string nl = Environment.NewLine;
        Assert.Contains(
            $"  段の数: 2{nl}" +
            $"  段の幅: 5 px (各段 5×4){nl}" +
            $"  各段の露光 (左から):{nl}" +
            $"    段1 (x 0〜4): 長秒{nl}" +
            $"    段2 (x 5〜9): 短秒 (長秒の 1/16。フォーマットの露光比 16){nl}" +
            $"  各段の Bayer: 段の左端を列0として数える (段の幅が奇数でも、各段の左上が Rggb の並びの始まり){nl}",
            text);
    }

    [Fact]
    public void Split_ThreeStages_MiddleIsOneRatioStepAndMonochromeHasNoBayerLine()
    {
        // 3段は 長秒 → 中秒 → 短秒。露光比は1段あたり(中秒は長秒の 1/4、短秒は 1/16)
        var derived = new RawFormat { Width = 12, Height = 2, BitDepth = 12 };
        var source = new RawFormat
        {
            Width = 4, Height = 2, BitDepth = 12, FrameCount = 3, Hdr = HdrMode.FrameSequential, HdrStages = 3,
            ExposureRatio = 4,
        };

        string text = HdrViewSidecar.DescribeSplit(derived, stages: 3, source, sourceFrame: 0, segmentWidth: 4);

        Assert.Contains("  段の幅: 4 px (各段 4×2)", text);
        Assert.Contains("    段1 (x 0〜3): 長秒" + Environment.NewLine, text);
        Assert.Contains("    段2 (x 4〜7): 中秒 (長秒の 1/4。フォーマットの露光比 4)", text);
        Assert.Contains("    段3 (x 8〜11): 短秒 (長秒の 1/16。フォーマットの露光比 4)", text);
        Assert.DoesNotContain("各段の Bayer", text);
    }

    [Theory]
    [InlineData(SaveFormat.Raw)]
    [InlineData(SaveFormat.Tiff16)]
    [InlineData(SaveFormat.Png16)]
    [InlineData(SaveFormat.Png8)]
    [InlineData(SaveFormat.Jpeg8)]
    public void DerivedView_SplitStageLayoutIsWrittenForEveryOutputFormat(SaveFormat output)
    {
        // raw だけでなく TIFF/PNG/JPEG で保存したときも、並置した段の構成を付随テキストに書く
        var derived = new RawFormat { Width = 10, Height = 4, BitDepth = 12, Bayer = BayerPattern.Grbg };
        var source = new RawFormat
        {
            Width = 5, Height = 8, BitDepth = 12, Bayer = BayerPattern.Grbg, Hdr = HdrMode.LineInterleaved,
        };

        string? text = HdrViewSidecar.DescribeDerivedView(
            derived, segmentWidth: 5, merged: null, stages: 2, source, sourceFrame: 0, output);

        Assert.NotNull(text);
        Assert.StartsWith("[HDR派生ビュー]", text);
        Assert.Contains("段の数: 2", text);
        Assert.Contains("段の幅: 5 px", text);
        Assert.Contains("段2 (x 5〜9): 短秒", text);
        Assert.Contains("各段の左上が Grbg の並びの始まり", text);
        Assert.Null(HdrViewSidecar.DescribeDerivedView(null, 0, null, 2, source, 0, output));
    }

    [Fact]
    public void SplitDisplayLut_RecordsEachStagesAdjustment_LongToShort()
    {
        // 分割ビューから表示LUTを焼き込むと、各段をその段の表示調整で焼き込む。スライダーが示すのは最後に調整した段の
        // 値だけなので、それを1組だけ[適用処理]へ書くと保存した画像と食い違う。段ごとに、分割ビューでないときと
        // 同じ書式で書く。2段は長秒・短秒(中秒と取り違えない)
        DisplayParameters[] stages =
        [
            new(BlackPoint: 1024),
            new(BlackPoint: 1024, WhitePoint: 32767, Gain: 4.0, Gamma: 2.2),
            new(BlackPoint: 256, WhitePoint: 8191, Gain: 16.0, Contrast: 1.5),
        ];

        string text = HdrViewSidecar.DescribeSplitDisplayLut(stages);
        string twoStages = HdrViewSidecar.DescribeSplitDisplayLut([stages[0], stages[2]]);

        string[] expected =
        [
            "  表示LUT: 適用 (HDR分割の段ごと)",
            "    長秒:",
            "      黒点/白点: 1024 / 65535",
            "      ゲイン: 0.0 dB (×1.000)",
            "      ガンマ: 1.000",
            "      コントラスト: 1.000",
            "    中秒:",
            "      黒点/白点: 1024 / 32767",
            "      ゲイン: 12.0 dB (×4.000)",
            "      ガンマ: 2.200",
            "      コントラスト: 1.000",
            "    短秒:",
            "      黒点/白点: 256 / 8191",
            "      ゲイン: 24.1 dB (×16.000)",
            "      ガンマ: 1.000",
            "      コントラスト: 1.500",
        ];
        Assert.Equal(string.Join(Environment.NewLine, expected) + Environment.NewLine, text);
        Assert.Contains("    長秒:", twoStages);
        Assert.Contains("    短秒:" + Environment.NewLine + "      黒点/白点: 256 / 8191", twoStages);
        Assert.DoesNotContain("中秒", twoStages);
    }
}
