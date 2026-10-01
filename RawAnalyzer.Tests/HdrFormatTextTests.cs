using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// フォーマットのHDR設定の文字列(MainWindow の右パネルのフォーマット欄の要約)。
/// </summary>
public class HdrFormatTextTests
{
    [Fact]
    public void Panel_AutoResolvedToFrameSequential_OmitsLineBlockAndRowOffset_KeepsFractionalRatio()
    {
        // 回帰テスト: ライン単位・行オフセットを出すかをHDR方式の指定(解決前)で判定していたので、自動でフレーム連結に
        // 解決したとき(フレーム数=段数)も、分割で使わない「2行単位」「行オフセット+2」を適用中のように出していた。
        // 露光比も F0 で整数に丸めていた(2.5 が 3 に見える)
        var format = new RawFormat
        {
            Width = 8, Height = 8, BitDepth = 12, Bayer = BayerPattern.Rggb, FrameCount = 2,
            Hdr = HdrMode.Auto, HdrStages = 2, ExposureRatio = 2.5, HdrRowOffset = 2,
        };

        Assert.Equal("フレーム連結(自動) 2段 (露光比 2.5)", HdrFormatText.DescribePanel(format));
    }

    [Fact]
    public void Panel_LayoutUnknown_OmitsLineBlock()
    {
        // 自動でレイアウトを決められない(フレーム数が1でも段数でもない)ときも、ライン単位を使うかは分からない
        var format = new RawFormat
        {
            Width = 8, Height = 8, BitDepth = 12, FrameCount = 5, Hdr = HdrMode.Auto, HdrStages = 2,
            ExposureRatio = 16,
        };

        Assert.Equal("レイアウト不明 2段 (露光比 16)", HdrFormatText.DescribePanel(format));
    }

    [Theory]
    [InlineData(HdrMode.LineInterleaved, 1, "行交互 3段 (露光比 4.5) / 2行単位 / 行オフセット-2")]
    [InlineData(HdrMode.Auto, 1, "行交互(自動) 3段 (露光比 4.5) / 2行単位 / 行オフセット-2")]
    [InlineData(HdrMode.LineInterleaved, 3, "行交互 3段 (露光比 4.5) / 2行単位 / 行オフセット-2")]
    public void Panel_LineInterleaved_ShowsEffectiveLineBlockAndRowOffset(HdrMode hdr, int frames, string expected)
    {
        // 行交互ではライン単位(未指定なら Bayer で2)と行オフセットが分割を決めるので出す
        var format = new RawFormat
        {
            Width = 8, Height = 12, BitDepth = 12, Bayer = BayerPattern.Rggb, FrameCount = frames,
            Hdr = hdr, HdrStages = 3, ExposureRatio = 4.5, HdrRowOffset = -2,
        };

        Assert.Equal(expected, HdrFormatText.DescribePanel(format));
    }

    [Fact]
    public void Panel_DefaultLineBlockFollowsBayer()
    {
        // ライン単位が未指定なら Bayer で決まる(なし=1・あり=2)。右パネルで Bayer を変えたら要約も出し直す
        var format = new RawFormat
        {
            Width = 8, Height = 8, BitDepth = 12, Hdr = HdrMode.LineInterleaved, HdrStages = 2, ExposureRatio = 16,
        };

        Assert.Equal("行交互 2段 (露光比 16) / 1行単位", HdrFormatText.DescribePanel(format));
        Assert.Equal(
            "行交互 2段 (露光比 16) / 2行単位",
            HdrFormatText.DescribePanel(format with { Bayer = BayerPattern.Gbrg }));
    }

    [Fact]
    public void Panel_ExplicitFrameSequential_OmitsLineBlock()
    {
        var format = new RawFormat
        {
            Width = 8, Height = 8, BitDepth = 12, FrameCount = 3, Hdr = HdrMode.FrameSequential, HdrStages = 3,
            ExposureRatio = 16, HdrRowOffset = 4,
        };

        Assert.Equal("フレーム連結 3段 (露光比 16)", HdrFormatText.DescribePanel(format));
    }

    [Fact]
    public void Sidecar_LineInterleaved_RecordsEffectiveLineBlockAndRowOffset()
    {
        // 回帰テスト: 保存の付随テキストの[入力フォーマット]のHDR行は段数と露光比だけで、分割を決めるライン単位と
        // 行オフセットを書かなかった。Bayer でライン単位1と2(オフセット0)や、行オフセット+2と−2の画像は、派生画像の
        // 寸法も同じになり、どちらの整列で作った画像か判別できなかった。露光比は F1 から 0.### にそろえる
        var basis = new RawFormat
        {
            Width = 8, Height = 12, BitDepth = 12, Bayer = BayerPattern.Rggb,
            Hdr = HdrMode.LineInterleaved, HdrStages = 2, ExposureRatio = 4.5,
        };

        Assert.Equal("LineInterleaved 2段 露光比4.5 ライン単位2 行オフセット0", HdrFormatText.DescribeSidecar(basis));
        Assert.Equal(
            "LineInterleaved 2段 露光比4.5 ライン単位1 行オフセット0",
            HdrFormatText.DescribeSidecar(basis with { HdrLineBlock = 1 }));
        Assert.Equal(
            "LineInterleaved 2段 露光比4.5 ライン単位2 行オフセット+2",
            HdrFormatText.DescribeSidecar(basis with { HdrRowOffset = 2 }));
        Assert.Equal(
            "LineInterleaved 2段 露光比4.5 ライン単位2 行オフセット-2",
            HdrFormatText.DescribeSidecar(basis with { HdrRowOffset = -2 }));
    }

    [Fact]
    public void Sidecar_FrameSequentialOrNone_HasNoLineBlock()
    {
        var frames = new RawFormat
        {
            Width = 8, Height = 8, BitDepth = 12, FrameCount = 2, Hdr = HdrMode.Auto, HdrStages = 2,
            ExposureRatio = 16, HdrRowOffset = 2,
        };

        Assert.Equal("Auto 2段 露光比16", HdrFormatText.DescribeSidecar(frames));
        Assert.Equal("None", HdrFormatText.DescribeSidecar(new RawFormat { Width = 4, Height = 4, BitDepth = 12 }));
    }

    [Fact]
    public void Panel_None()
    {
        Assert.Equal("なし", HdrFormatText.DescribePanel(new RawFormat { Width = 4, Height = 4, BitDepth = 12 }));
    }
}
