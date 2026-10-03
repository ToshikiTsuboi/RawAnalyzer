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
        // RGGB → なし では行交互の既定の行単位も2行 → 1行に替わり、分割の仕方そのものが違う。
        // 照合は値で比べる。替えて同じ値へ戻した(同値の別インスタンスになった)だけなら断らない
        Assert.Equal(HdrViewReplacement.Refusal.None,
            HdrViewReplacement.Check(modeReselected: false, sourceReplaced: false,
                Started, Started with { Bayer = BayerPattern.None } with { Bayer = Started.Bayer }));

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

    // 以下は派生ビューへ差し替える直前(旧画像を読んでいる描画の停止を待った後)の判定(MainWindow の ApplyDerivedViewAsync)。
    // 描画の停止のためにビューポートから画像を外して待つので、その間も表示モードは選び直せる

    [Theory]
    [InlineData(0)] // Raw表示
    [InlineData(1)] // Bayerカラー
    [InlineData(3)] // チャネル分割
    public void ModeReselectedWhileStoppingRender_DiscardsAndShowsSourceAgain(int selected)
    {
        // レビュー 2026-10-03 R4。Raw表示からHDR合成を選び、計算の後で描画の停止を待つ間に Raw表示を選び直した。
        // 以前は停止を待った後に元画像・フレームしか照合せず、選択は Raw表示なのに合成ビューを表示した(保存・解析の
        // 対象にもなった)。選び直した側は(派生ビューがないので)外された元画像を表示し直さない。結果を捨てた側が、
        // 外した元画像を選び直した表示モードで表示し直す
        Assert.Equal(HdrViewReplacement.Adoption.DiscardAndShowSource,
            HdrViewReplacement.CheckAdoption(sourceReplaced: false, selected, computedIndex: 5,
                viewportEmpty: true, derivedViewShown: false, mergedViewShown: false));
    }

    [Fact]
    public void ModeReselectedFromDerivedView_LeavesRedisplayToRestore()
    {
        // 分割ビューから合成を計算し、停止を待つ間に Raw表示を選び直した。選び直した側(Raw表示への復帰)が分割ビューを
        // 捨てて元画像を表示し直すので、結果を捨てるだけにする(表示し直すと、復帰がこの後で捨てる分割ビューを
        // ビューポートへ入れてしまう)
        Assert.Equal(HdrViewReplacement.Adoption.Discard,
            HdrViewReplacement.CheckAdoption(sourceReplaced: false, selectedIndex: 0, computedIndex: 5,
                viewportEmpty: true, derivedViewShown: true, mergedViewShown: false));

        // 復帰が先に済んでいた(ビューポートに元画像がある)ときも、結果を捨てるだけ
        Assert.Equal(HdrViewReplacement.Adoption.Discard,
            HdrViewReplacement.CheckAdoption(sourceReplaced: false, selectedIndex: 0, computedIndex: 5,
                viewportEmpty: false, derivedViewShown: false, mergedViewShown: false));
    }

    [Theory]
    [InlineData(1)] // 合成ビューの Bayerカラー
    [InlineData(2)] // 合成ビューのカラー現像
    [InlineData(3)] // 合成ビューのチャネル分割
    [InlineData(5)] // 合成ビュー(Raw表示)
    public void ModeReselectedOnMergedView_ShowsMergedViewAgain(int selected)
    {
        // 合成ビューから分割を計算し、停止を待つ間に合成ビューの表示を選び直した。選び直した側は合成ビューのまま
        // 表示モードだけを替える(外された合成ビューを表示し直さない)ので、結果を捨てた側が合成ビューを表示し直す
        Assert.Equal(HdrViewReplacement.Adoption.DiscardAndShowMergedView,
            HdrViewReplacement.CheckAdoption(sourceReplaced: false, selected, computedIndex: 4,
                viewportEmpty: true, derivedViewShown: true, mergedViewShown: true));

        // Raw表示を選び直したときは、Raw表示への復帰が元画像を表示し直す
        Assert.Equal(HdrViewReplacement.Adoption.Discard,
            HdrViewReplacement.CheckAdoption(sourceReplaced: false, selectedIndex: 0, computedIndex: 4,
                viewportEmpty: true, derivedViewShown: true, mergedViewShown: true));
    }

    [Fact]
    public void SameModeAndSource_IsAdopted_ReplacedSource_IsDiscarded()
    {
        Assert.Equal(HdrViewReplacement.Adoption.Adopt,
            HdrViewReplacement.CheckAdoption(sourceReplaced: false, selectedIndex: 5, computedIndex: 5,
                viewportEmpty: true, derivedViewShown: false, mergedViewShown: false));

        // 元画像を差し替えた側(開き直した画像)が表示しているので、選び直しがあっても結果を捨てるだけ
        Assert.Equal(HdrViewReplacement.Adoption.Discard,
            HdrViewReplacement.CheckAdoption(sourceReplaced: true, selectedIndex: 0, computedIndex: 5,
                viewportEmpty: false, derivedViewShown: false, mergedViewShown: false));
    }
}
