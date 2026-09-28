using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// HDR分割・合成の計算結果で派生ビューへ差し替えてよいか(MainWindow の CanApplyHdrView が使う)。
/// </summary>
public class HdrViewReplacementTests
{
    // 行交互2段・RGGB の12bit raw(行単位は自動: Bayer ありは2行)
    private static readonly RawFormat Started = new()
    {
        Width = 8, Height = 8, BitDepth = 12, Bayer = BayerPattern.Rggb,
        Hdr = HdrMode.LineInterleaved, HdrStages = 2,
    };

    [Fact]
    public void FormatChangedDuringComputation_IsRefused_AndReturnsToRawWithReason()
    {
        // 計算中に右パネルの Bayer が替わった(計算中は選べなくしているが、多重防御として照合する)。以前は照合せず、
        // 開始時のパターンで作った派生ビューを、右パネルが替えた後のパターンを示したまま表示した。
        // RGGB → なし では行交互の既定の行単位も2行 → 1行に替わり、分割の仕方そのものが違う
        Assert.Equal(HdrViewReplacement.Refusal.None,
            HdrViewReplacement.Check(modeReselected: false, sourceReplaced: false, Started, Started));

        Assert.Equal(HdrViewReplacement.Refusal.FormatChanged,
            HdrViewReplacement.Check(modeReselected: false, sourceReplaced: false,
                Started, Started with { Bayer = BayerPattern.None }));

        string text = HdrViewReplacement.Explain(HdrViewReplacement.Refusal.FormatChanged, "HDR合成");
        Assert.StartsWith("HDR合成の計算中に Bayer パターンなどのフォーマットが変わったため", text);
        Assert.Contains("Raw表示に戻しました", text);
    }

    [Fact]
    public void ReselectedModeOrReplacedSource_IsSupersededSilently_EvenIfFormatAlsoChanged()
    {
        // 表示モードを選び直した・元画像が替わったときは、選び直した側・差し替えた側が表示している。
        // フォーマットの違いより先に見て黙って捨てる(Raw表示へ戻すと、利用者が選び直した表示を上書きする)
        RawFormat changed = Started with { Bayer = BayerPattern.Bggr };

        Assert.Equal(HdrViewReplacement.Refusal.Superseded,
            HdrViewReplacement.Check(modeReselected: true, sourceReplaced: false, Started, changed));
        Assert.Equal(HdrViewReplacement.Refusal.Superseded,
            HdrViewReplacement.Check(modeReselected: false, sourceReplaced: true, Started, changed));
        Assert.Equal("", HdrViewReplacement.Explain(HdrViewReplacement.Refusal.Superseded, "HDR分割"));
    }
}
