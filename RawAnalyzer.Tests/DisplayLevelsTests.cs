using RawAnalyzer.App.Services;
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
        // 12bit素材を開いた直後(白=4095 → 内部65535)に16bitのHDR合成へ切り替える
        (ushort black, ushort white) = DisplayLevels.ToPoints(0, 4095, bitDepth: 12);
        Assert.Equal(65535, white);

        (int blackCode, int whiteCode) = DisplayLevels.ToCodes(black, white, bitDepth: 16);

        Assert.Equal(65535, DisplayLevels.MaxCode(16));
        Assert.Equal((0, 65535), (blackCode, whiteCode));

        // 白レベルを1だけ下げても白点は1codeしか動かない(以前は4094へ急落した)
        (_, ushort nudged) = DisplayLevels.ToPoints(blackCode, whiteCode - 1, bitDepth: 16);
        Assert.Equal(65534, nudged);
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
    [InlineData(8)]  // shift ≥ 1(白点の下位ビットを埋める)。10/12/14bit は shift の定数が違うだけ
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
    public void ToPoints_WhiteIncludesWholeCode_AndClamps()
    {
        // 白点はそのcodeの上端まで含める(12bitの code 100 → 1600..1615 の 1615)
        Assert.Equal((ushort)1600, DisplayLevels.ToPoints(100, 100, 12).BlackPoint);
        Assert.Equal((ushort)1615, DisplayLevels.ToPoints(100, 100, 12).WhitePoint);

        // 旧ビット深度の大きなコード値が残っていても桁あふれせず上端で止まる
        Assert.Equal((ushort)65535, DisplayLevels.ToPoints(65535, 65535, 12).BlackPoint);
        Assert.Equal((ushort)65535, DisplayLevels.ToPoints(0, 65535, 12).WhitePoint);
    }
}
