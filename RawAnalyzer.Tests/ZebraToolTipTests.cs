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

    [Theory]
    [InlineData(8, 250, 249, 5, 6)]           // 0.98×255 = 249.9 → 250、0.02×255 = 5.1 → 5
    [InlineData(10, 1003, 1002, 20, 21)]      // 0.98×1023 = 1002.5 → 1003、0.02×1023 = 20.5 → 20
    [InlineData(12, 4014, 4013, 81, 82)]
    [InlineData(14, 16056, 16055, 327, 328)]
    [InlineData(16, 64225, 64224, 1310, 1311)] // 64224/65535 = 97.9995% は 98% 未満
    public void Zebra_ThresholdsAreCodesOfShownBitDepth(
        int bitDepth, int saturated, int belowSaturation, int crushed, int aboveCrushed)
    {
        // ツールチップの「飽和(≥98%)」「黒潰れ(≤2%)」を表示中の画像のビット深度の code で判定する。以前は16bit値の
        // 64224 と比べたため、8bit の code 250(250<<8 = 64000)・10bit の 1003 に縞が出なかった
        int shift = 16 - bitDepth;
        ushort[] codes = [(ushort)saturated, (ushort)belowSaturation, (ushort)crushed, (ushort)aboveCrushed];
        using RawImage image = TestImages.FromCodes(codes, 4, 1, bitDepth);
        var request = new RenderRequest
        {
            Source = new RawImageRenderSource(image, 0),
            Lut = DisplayLut.Create(new DisplayParameters()),
            ZebraEnabled = true,
            ZebraBitDepth = bitDepth,
        };
        var buffer = new byte[4 * 4];
        ViewportRenderer.Render(request, zoom: 1.0, originX: 0, originY: 0, 4, 1, buffer, default);

        // 行0では x=0〜3 がストライプの帯((x + y) & 7 < 4)。赤 = BGRA (0x3C, 0x50, 0xE6)、青 = (0xE6, 0x78, 0x3C)
        Assert.Equal(0xE6, buffer[2]);                 // 飽和 → 赤
        Assert.NotEqual(0xE6, buffer[4 + 2]);          // 98% 未満 → 縞なし
        Assert.Equal(0xE6, buffer[8]);                 // 黒潰れ → 青
        Assert.NotEqual(0xE6, buffer[12]);             // 2% 超 → 縞なし
        Assert.Equal(
            ((ushort)(saturated << shift), (ushort)((crushed << shift) | ((1 << shift) - 1))),
            ViewportRenderer.ZebraThresholds(bitDepth));
    }
}
