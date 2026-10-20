using RawAnalyzer.App.Compare;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 黒/白レベルの raw code(UI)と16bit内部値(表示LUT)の換算。
/// 表示中の画像のビット深度が変わっても、内部値を保ったままUIを表し直せること。
/// </summary>
public class DisplayLevelsTests
{
    [Fact]
    public void HdrMerge12To16Bit_RebasedCodesKeepWhitePoint()
    {
        // 12bit素材を開いた直後(白=4095 → 内部 4095<<4 = 65520。最大コードの画素と同じ値)に16bitの画像へ切り替える
        (ushort black, ushort white) = DisplayLevels.ToPoints(0, 4095, bitDepth: 12);
        Assert.Equal(65520, white);

        (int blackCode, int whiteCode) = DisplayLevels.ToCodes(black, white, bitDepth: 16);

        Assert.Equal(65535, DisplayLevels.MaxCode(16));
        Assert.Equal((0, 65520), (blackCode, whiteCode));

        // 白レベルを1だけ下げても白点は1codeしか動かない(以前は4094へ急落した)
        (_, ushort nudged) = DisplayLevels.ToPoints(blackCode, whiteCode - 1, bitDepth: 16);
        Assert.Equal(65519, nudged);
    }

    [Fact]
    public void BackTo12Bit_RebasedCodesKeepLevels()
    {
        // 16bitの画像で黒4096 / 白40000にした後、12bitの画像へ替わる(ビット深度の異なるTIFFのページ・連番の送り。
        // HDR合成ビューから元画像へ戻るときは、合成ビューの値ではなく合成ビューへ入る前の値へ戻す: MergedViewLevels)
        (ushort black, ushort white) = DisplayLevels.ToPoints(4096, 40000, bitDepth: 16);

        (int blackCode, int whiteCode) = DisplayLevels.ToCodes(black, white, bitDepth: 12);

        Assert.Equal(4095, DisplayLevels.MaxCode(12));
        Assert.Equal((256, 2500), (blackCode, whiteCode));

        // 戻した値をそのまま操作しても内部値は同じ位置(12bitの1code以内)に留まる
        (ushort blackAgain, ushort whiteAgain) =
            DisplayLevels.ToPoints(blackCode, whiteCode, bitDepth: 12);
        Assert.Equal(4096, blackAgain);
        Assert.InRange(whiteAgain, 40000, 40000 + 15);
    }

    [Theory]
    [InlineData(8)]  // shift ≥ 1。10/12/14bit は shift の定数が違うだけ
    [InlineData(16)] // shift 0
    public void CodesRoundTripAtSameBitDepth(int bitDepth)
    {
        int max = DisplayLevels.MaxCode(bitDepth);
        foreach ((int blackCode, int whiteCode) in new[] { (0, max), (1, max - 1), (max / 3, max / 2) })
        {
            (ushort black, ushort white) = DisplayLevels.ToPoints(blackCode, whiteCode, bitDepth);

            Assert.Equal((blackCode, whiteCode), DisplayLevels.ToCodes(black, white, bitDepth));
        }
    }

    [Fact]
    public void ToPoints_PlacesLevelsAtCodeValue_AndClamps()
    {
        // 黒点・白点ともそのcodeの画素と同じ値(12bitの code 100 → 1600。区間 1600..1615 の下端)
        Assert.Equal((ushort)1600, DisplayLevels.ToPoints(100, 100, 12).BlackPoint);
        Assert.Equal((ushort)1600, DisplayLevels.ToPoints(100, 100, 12).WhitePoint);

        // 旧ビット深度の大きなコード値が残っていても桁あふれせず65535で止まる
        Assert.Equal((ushort)65535, DisplayLevels.ToPoints(65535, 65535, 12).BlackPoint);
        Assert.Equal((ushort)65535, DisplayLevels.ToPoints(0, 65535, 12).WhitePoint);
    }

    [Fact]
    public void AutoLevels_IgnoreHistogramOfAnotherBitDepth()
    {
        // HDR合成ビュー(16bit)へ切り替えた直後、合成画像のヒストグラムができる前に「自動」を押すと、元画像(12bit)の
        // 4096ビンのコード(黒75/白3053)を16bitのコードとして使い、合成画像がほぼ全面白飛びになっていた。
        // 表示中の画像のビット深度と違うヒストグラムでは何もしない
        ushort[] codes = Enumerable.Range(75, 3053 - 75 + 1).Select(c => (ushort)c).ToArray();
        using RawImage image12 = TestImages.FromCodes(codes, codes.Length, 1, bitDepth: 12);
        HistogramResult histogram12 = ImageAnalysis.ComputeHistogram(image12, 0);

        Assert.Null(DisplayLevels.AutoLevelsFor(histogram12, shownBitDepth: 16));
        Assert.Null(DisplayLevels.AutoLevelsFor(null, shownBitDepth: 12));
        Assert.Equal(
            HistogramTools.ComputeAutoLevels(histogram12.Bins), DisplayLevels.AutoLevelsFor(histogram12, shownBitDepth: 12));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(16)]
    public void WhiteCodePixel_MapsToWhite_AtEveryBitDepth(int bitDepth)
    {
        // Nbit の画素は code<<(16−N)(code の区間の下端)にある。白レベルに指定したコードの画素は、
        // どのビット深度でも 255 になる(以前は白点を区間の上端に置き、12bit 黒100/白101 で code101 が 132 だった)
        int shift = 16 - bitDepth;
        foreach ((int blackCode, int whiteCode) in new[] { (100, 101), (100, 102), (93, 147) })
        {
            (ushort black, ushort white) = DisplayLevels.ToPoints(blackCode, whiteCode, bitDepth);
            DisplayLut lut = DisplayLut.Create(new DisplayParameters(black, white));

            Assert.Equal(255, lut.Map((ushort)(whiteCode << shift)));
            Assert.Equal(0, lut.Map((ushort)(blackCode << shift)));

            // 比較ペインの表示調整も同じ規約
            DisplayParameters pane = new DisplaySettings { BlackCode = blackCode, WhiteCode = whiteCode }
                .ToDisplayParameters(bitDepth);
            Assert.Equal((black, white), (pane.BlackPoint, pane.WhitePoint));
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(16)]
    public void DefaultLevels_MapEachCodeLinearly_AndEightBitIsIdentity(int bitDepth)
    {
        // 既定(黒0・白=最大コード)の表示と「表示調整を焼き込まない」保存の LUT は、各コード c を
        // round(c·255/最大コード) にする。8bit では恒等(以前は白点65535のため 255 が 254 になった)
        int shift = 16 - bitDepth;
        int max = DisplayLevels.MaxCode(bitDepth);
        DisplayParameters identity = DisplayLevels.IdentityParameters(bitDepth);
        Assert.Equal(DisplayLevels.ToPoints(0, max, bitDepth), (identity.BlackPoint, identity.WhitePoint));
        Assert.Equal(identity, DisplaySettings.CreateDefault(bitDepth).ToDisplayParameters(bitDepth));
        DisplayLut lut = DisplayLut.Create(identity);
        for (int code = 0; code <= max; code += bitDepth == 16 ? 97 : 1)
        {
            Assert.Equal((int)Math.Round(code * 255.0 / max), lut.Map((ushort)(code << shift)));
        }

        Assert.Equal(255, lut.Map((ushort)(max << shift)));
    }
}
