using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

public class ZebraToolTipTests
{
    public static TheoryData<ViewportDisplayMode> AllModes()
    {
        var modes = new TheoryData<ViewportDisplayMode>();
        foreach (ViewportDisplayMode mode in Enum.GetValues<ViewportDisplayMode>())
        {
            modes.Add(mode);
        }

        return modes;
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void IsDrawn_MatchesWhetherRendererDrawsZebra(ViewportDisplayMode mode)
    {
        // ツールチップの「描かない」の判定が描画の実際とずれないこと。全画素飽和の画像を
        // ゼブラON/OFFで描き、縞が乗る(出力が変わる)のは IsDrawn がtrueのモードだけ
        const int size = 8;
        var codes = new ushort[size * size];
        Array.Fill(codes, ushort.MaxValue);
        using RawImage image = TestImages.FromCodes(codes, size, size, bayer: BayerPattern.Rggb);
        var rgb = new ushort[size * size * 3];
        Array.Fill(rgb, ushort.MaxValue);
        ColorImage color = ColorImage.FromInterleaved(size, size, 16, rgb);

        byte[] Render(bool zebra)
        {
            var request = new RenderRequest
            {
                Source = new RawImageRenderSource(image, 0),
                Lut = DisplayLut.Create(new DisplayParameters()),
                Mode = mode,
                Pattern = BayerPattern.Rggb,
                DevelopLuts = DevelopLuts.Create(new DevelopParameters()),
                ZebraEnabled = zebra,
                Color = color,
            };
            var buffer = new byte[size * size * 4];
            ViewportRenderer.Render(request, zoom: 1.0, originX: 0, originY: 0, size, size, buffer, default);
            return buffer;
        }

        Assert.Equal(ZebraToolTip.IsDrawn(mode), !Render(zebra: true).AsSpan().SequenceEqual(Render(zebra: false)));
    }

    [Theory]
    [InlineData(ViewportDisplayMode.BayerColor, "Bayerカラーの表示ではゼブラを描きません")]
    [InlineData(ViewportDisplayMode.ColorDevelop, "カラー現像の表示ではゼブラを描きません")]
    [InlineData(ViewportDisplayMode.Raw, "飽和(≥98%)を赤")]
    [InlineData(ViewportDisplayMode.ChannelSplit, "飽和(≥98%)を赤")]
    [InlineData(ViewportDisplayMode.TrueColor, "飽和(≥98%)を赤")]
    public void For_LeadsWithReasonOnlyWhereZebraIsNotDrawn(ViewportDisplayMode mode, string expectedStart)
    {
        // 描かない表示では理由を先頭に出し、どの表示でも描く範囲(README と同じ)を示す
        string text = ZebraToolTip.For(mode);
        Assert.StartsWith(expectedStart, text);
        Assert.Contains("Raw・チャネル分割・カラー画像の表示のみ", text);
        Assert.Equal(ZebraToolTip.IsDrawn(mode), !text.Contains("描きません"));
    }
}
