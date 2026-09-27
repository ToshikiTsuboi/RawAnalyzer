using System.Windows.Controls;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 欠陥画素ウィンドウの一覧・案内・「この欠陥を補正」の状態の検証。
/// </summary>
/// <remarks>
/// 表示する画素が変わる差し替え(開く・ページ/連番/フレーム送り・処理結果・HDR表示の出入り)では、
/// MainWindow はウィンドウを閉じずに <see cref="DefectPixelWindow.DiscardResult"/> で検出結果だけを
/// 破棄する。再生中に開いたウィンドウが次の送りで閉じないようにするため。
/// </remarks>
[Collection("WPF UI")]
public class DefectPixelWindowTests
{
    [Fact]
    public Task DiscardResult_KeepsWindowOpenAndReturnsToNotRun() => WpfTestHost.Run(() =>
    {
        var window = new DefectPixelWindow();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        var list = (ListView)window.FindName("DefectList");
        var correct = (Button)window.FindName("CorrectButton");
        var run = (Button)window.FindName("RunButton");
        var summary = (TextBlock)window.FindName("SummaryText");

        window.ShowResult(DetectTwoDefects(), 4095);
        Assert.Equal(2, list.Items.Count);
        Assert.True(correct.IsEnabled);

        // 画像の差し替え(再生中の送りを含む)。以前は MainWindow がウィンドウごと閉じていた
        window.DiscardResult();

        Assert.False(closed);
        Assert.Empty(list.Items);
        Assert.False(correct.IsEnabled);
        Assert.True(run.IsEnabled);
        Assert.DoesNotContain("白点 1 / 黒点 1", summary.Text);
        Assert.Contains("「検出実行」", summary.Text);

        // 検出・補正のキャンセルなどで操作可能へ戻しても、破棄した一覧で補正は押させない
        window.ResetRunButton();
        Assert.False(correct.IsEnabled);
        Assert.True(run.IsEnabled);
        window.Close();
    });

    [Fact]
    public Task DiscardResult_BeforeDetection_KeepsInitialGuidance() => WpfTestHost.Run(() =>
    {
        // 再生中に開いただけのウィンドウ。送りのたびに破棄が呼ばれても、閉じずに最初の案内のまま
        var window = new DefectPixelWindow();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        var summary = (TextBlock)window.FindName("SummaryText");
        string initial = summary.Text;

        window.DiscardResult();
        window.DiscardResult();

        Assert.False(closed);
        Assert.Equal(initial, summary.Text);
        Assert.False(((Button)window.FindName("CorrectButton")).IsEnabled);
        Assert.True(((Button)window.FindName("RunButton")).IsEnabled);
        window.Close();
    });

    [Fact]
    public Task DiscardResult_ThenDetectAgain_ShowsResultForNewImage() => WpfTestHost.Run(() =>
    {
        // 破棄した後の「検出実行」は表示中の画像を検出し、同じウィンドウに一覧を出す
        var window = new DefectPixelWindow();
        var list = (ListView)window.FindName("DefectList");
        var correct = (Button)window.FindName("CorrectButton");
        var summary = (TextBlock)window.FindName("SummaryText");

        window.ShowResult(DetectTwoDefects(), 4095);
        window.DiscardResult();
        window.ShowResult(DetectTwoDefects(), 4095);

        Assert.Equal(2, list.Items.Count);
        Assert.True(correct.IsEnabled);
        Assert.Contains("白点 1 / 黒点 1", summary.Text);
        window.Close();
    });

    /// <summary>白点1・黒点1を埋め込んだ12bitの平坦な画像から検出した結果。</summary>
    private static DefectDetectionResult DetectTwoDefects()
    {
        const int size = 32;
        var codes = new ushort[size * size];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(1000 + (i % 2)); // σ>0にするためのディザ
        }

        codes[7 * size + 5] = 4000;
        codes[15 * size + 20] = 10;
        using RawImage image = TestImages.FromCodes(codes, size, size, bitDepth: 12);
        return DefectPixelDetector.Detect(image);
    }
}
