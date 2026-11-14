using System.Windows.Controls;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RawAnalyzer.App.Services;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FooterButtons_FitClientAreaWithoutOverlap(bool minimumSize) => WpfTestHost.Run(async () =>
    {
        var window = new DefectPixelWindow { ShowActivated = false };
        if (minimumSize)
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
        }

        try
        {
            window.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var client = (FrameworkElement)window.Content;
            var footer = (Panel)window.FindName("FooterPanel");
            var buttons = footer.Children.OfType<Panel>().SelectMany(row => row.Children.OfType<Button>()).ToArray();
            Assert.Equal(4, buttons.Length);

            Rect Bounds(FrameworkElement element) =>
                element.TransformToAncestor(client).TransformBounds(new Rect(element.RenderSize));

            var area = new Rect(client.RenderSize);
            Assert.All(buttons, button =>
            {
                Assert.True(button.ActualWidth > 0);
                Assert.True(area.Contains(Bounds(button)));
            });
            var correct = (Button)window.FindName("CorrectButton");
            var copy = (Button)window.FindName("CopyButton");
            Assert.False(Bounds(correct).IntersectsWith(Bounds(copy)));

            // 結果表示後のフッターを、実測した幅でウィンドウ枠から独立して撮る。
            var snapshot = new DefectPixelWindow();
            try
            {
                snapshot.ShowResult(DetectTwoDefects(), 4095);
                var snapshotFooter = (StackPanel)snapshot.FindName("FooterPanel");
                ((DockPanel)snapshot.Content).Children.Remove(snapshotFooter);
                snapshotFooter.Margin = new Thickness(0);
                var frame = new Border
                {
                    Background = snapshot.Background,
                    Padding = new Thickness(12),
                    Width = footer.ActualWidth + 24,
                    Child = snapshotFooter,
                };
                TextElement.SetForeground(frame, snapshot.Foreground);
                frame.Measure(new Size(frame.Width, double.PositiveInfinity));
                frame.Arrange(new Rect(frame.DesiredSize));
                frame.UpdateLayout();
                WpfTestHost.CaptureIfRequested(frame, minimumSize ? "defect-footer-minimum" : "defect-footer-default");
            }
            finally
            {
                snapshot.Close();
            }
        }
        finally
        {
            window.Close();
        }
    });

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

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    public Task InvalidSigma_IsMarkedWhileTyping(string text) => WpfTestHost.Run(() =>
    {
        // 以前は「検出実行」を押すまで何も示さなかった。ファイル一覧の絞り込み欄と同じく、打った時点で
        // 赤枠とツールチップの理由で示す(実行時の確認は従来どおり)
        var window = new DefectPixelWindow();
        var sigma = (TextBox)window.FindName("SigmaBox");
        FieldFeedback.AssertValid(sigma);

        sigma.Text = text;
        Assert.Equal("σ係数は正の数値で指定してください。", FieldFeedback.AssertInvalid(sigma));

        sigma.Text = "５．５";
        FieldFeedback.AssertValid(sigma);
        window.Close();
    });

    [Fact]
    public Task CorrectionApplied_DiscardsListAndAsksToDetectAgain_UntilImageChangesAgain() => WpfTestHost.Run(() =>
    {
        var window = new DefectPixelWindow();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        var list = (ListView)window.FindName("DefectList");
        var correct = (Button)window.FindName("CorrectButton");
        var run = (Button)window.FindName("RunButton");
        var summary = (TextBlock)window.FindName("SummaryText");
        window.ShowResult(DetectTwoDefects(), 4095);

        // 補正で表示中の画像が補正後のものへ差し替わった(MainWindow は差し替えで補正の案内とともに
        // 一覧を破棄し、補正の完了後に ResetRunButton も呼ぶ)
        window.DiscardResult(DefectPixelWindow.CorrectionAppliedNotice(2, "メディアン"));
        window.ResetRunButton();

        // 以前は補正前の一覧が残り、「この欠陥を補正」が再び押せた(押すと「表示中の画像のものではない」と
        // 断られるだけ)。一覧を消して補正を押せなくし、次にすること(検出し直す)を示す
        Assert.False(closed);
        Assert.Empty(list.Items);
        Assert.False(correct.IsEnabled);
        Assert.True(run.IsEnabled);
        Assert.Contains("2 個", summary.Text);
        Assert.Contains("メディアン", summary.Text);
        Assert.Contains("「検出実行」", summary.Text);

        // 補正の案内は補正した画像についてのもの。別の画像へ差し替えたら差し替えの案内に替える
        // (一覧は破棄済みでも、2 回目の破棄を素通りさせない)
        window.DiscardResult();

        Assert.DoesNotContain("補正しました", summary.Text);
        Assert.Contains("画像が替わった", summary.Text);
        Assert.False(correct.IsEnabled);
        window.Close();
    });

    [Fact]
    public Task UncorrectableResult_ListsDefectsButDoesNotOfferCorrection() => WpfTestHost.Run(() =>
    {
        // HDR表示(派生ビュー)中の検出結果。補正は HDR 表示中は断られるので、以前のように
        // 「この欠陥を補正」を有効にせず(押すと必ず断られた)、理由と次にすることを一覧の上に出す
        var window = new DefectPixelWindow();
        var list = (ListView)window.FindName("DefectList");
        var correct = (Button)window.FindName("CorrectButton");
        var run = (Button)window.FindName("RunButton");
        var summary = (TextBlock)window.FindName("SummaryText");

        window.ShowResult(DetectTwoDefects(), 4095, correctionUnavailableReason: HdrReason);

        Assert.False(correct.IsEnabled);
        Assert.True(run.IsEnabled);
        Assert.Contains(HdrReason, summary.Text);

        // 検出結果・一覧・コピー/CSV の表は従来どおり
        Assert.Contains("白点 1 / 黒点 1", summary.Text);
        Assert.Equal(2, list.Items.Count);

        // 表は見出し+欠陥ごとに x,y,raw_code,type の1行(並び順は検出器しだいなので行の有無で見る)
        string[] rows = window.BuildTable(',')!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, rows.Length);
        Assert.Contains("5,7,4000,hot", rows);
        Assert.Contains("20,15,10,dead", rows);

        // 検出・補正の失敗やキャンセルで操作可能へ戻しても、補正は押させない
        window.ResetRunButton();
        Assert.False(correct.IsEnabled);
        Assert.True(run.IsEnabled);
        window.Close();
    });

    [Fact]
    public Task CorrectableResult_AfterUncorrectableOne_ShowsListAndOffersCorrectionAgain() => WpfTestHost.Run(() =>
    {
        // Raw表示へ戻して検出し直した一覧は補正できる(HDR表示中の理由を持ち越さない)。
        // 破棄した後の「検出実行」は表示中の画像を検出し、同じウィンドウに一覧を出す
        var window = new DefectPixelWindow();
        var list = (ListView)window.FindName("DefectList");
        var correct = (Button)window.FindName("CorrectButton");
        var summary = (TextBlock)window.FindName("SummaryText");
        window.ShowResult(DetectTwoDefects(), 4095, correctionUnavailableReason: HdrReason);
        window.DiscardResult();

        window.ShowResult(DetectTwoDefects(), 4095);

        Assert.Equal(2, list.Items.Count);
        Assert.Contains("白点 1 / 黒点 1", summary.Text);
        Assert.True(correct.IsEnabled);
        Assert.DoesNotContain(HdrReason, summary.Text);
        window.ResetRunButton();
        Assert.True(correct.IsEnabled);
        window.Close();
    });

    [Fact]
    public Task ExportButtons_AreEnabledOnlyWhileADetectionResultIsShown() => WpfTestHost.Run(() =>
    {
        // 「コピー」「CSVで保存…」は表示中の一覧を書き出す。以前は一覧がなくても(未実行・破棄した後)
        // 押せて、押しても何も起きなかった
        var window = new DefectPixelWindow();
        var copy = (Button)window.FindName("CopyButton");
        var save = (Button)window.FindName("SaveCsvButton");

        // 未実行
        Assert.False(copy.IsEnabled);
        Assert.False(save.IsEnabled);
        Assert.Null(window.BuildTable(','));

        window.ShowResult(DetectTwoDefects(), 4095);
        Assert.True(copy.IsEnabled);
        Assert.True(save.IsEnabled);

        // 画像の差し替えで破棄した後
        window.DiscardResult();
        Assert.False(copy.IsEnabled);
        Assert.False(save.IsEnabled);
        Assert.Null(window.BuildTable(','));

        // 検出し直せば再び使え、補正の後に一覧を破棄したときも使えなくなる
        window.ShowResult(DetectTwoDefects(), 4095);
        Assert.True(copy.IsEnabled);
        window.DiscardResult(DefectPixelWindow.CorrectionAppliedNotice(2, "メディアン"));
        Assert.False(copy.IsEnabled);
        Assert.False(save.IsEnabled);

        // 検出・補正の失敗やキャンセルで操作可能へ戻しても、破棄した一覧は書き出させない
        window.ResetRunButton();
        Assert.False(copy.IsEnabled);
        Assert.False(save.IsEnabled);

        // HDR表示中の検出結果(補正はできない)も一覧は書き出せる
        window.ShowResult(DetectTwoDefects(), 4095, correctionUnavailableReason: HdrReason);
        Assert.True(copy.IsEnabled);
        Assert.True(save.IsEnabled);
        window.Close();
    });

    [Theory]
    [InlineData(BayerPattern.Rggb, "段1 R", "段2 B")]
    [InlineData(BayerPattern.None, "段1", "段2")]
    public Task SegmentedResult_ShowsThresholdsPerSegment(
        BayerPattern pattern, string firstLine, string lastLine) => WpfTestHost.Run(() =>
    {
        // HDR分割ビューの検出は段ごと(Bayer は段×チャネルごと)に閾値を求める。どの段の閾値かを示す
        // (段を示さないと同じチャネル名の行が段の数だけ並び、モノクロは閾値が1つも出なかった)
        var window = new DefectPixelWindow();
        var summary = (TextBlock)window.FindName("SummaryText");
        var thresholds = (ListView)window.FindName("ThresholdList");
        const int segment = 16;
        var codes = new ushort[segment * 2 * 16];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)((i % (segment * 2) < segment ? 2000 : 500) + (i % 2));
        }

        using RawImage image = TestImages.FromCodes(codes, segment * 2, 16, 12, pattern);
        window.ShowResult(DefectPixelDetector.Detect(image, pattern: pattern, segmentWidth: segment), 4095);

        var labels = thresholds.Items.Cast<DefectPixelWindow.ThresholdRow>().Select(row => row.Label);
        Assert.Contains(firstLine, labels);
        Assert.Contains(lastLine, labels);
        Assert.Contains("段は左から", summary.Text);
        window.Close();
    });

    [Fact]
    public Task ExportButtons_ZeroDefects_ExportHeaderOnlyTable() => WpfTestHost.Run(() =>
    {
        // 0 件も検出結果。見出しだけの表をコピー・保存でき、「検出して 0 件」を未実行(ファイルなし)と
        // 区別して、欠陥がある場合と同じ形式で残せる。補正するものはないので補正は押せない
        var window = new DefectPixelWindow();
        var list = (ListView)window.FindName("DefectList");

        window.ShowResult(DetectNoDefects(), 4095);

        Assert.Empty(list.Items);
        Assert.True(((Button)window.FindName("CopyButton")).IsEnabled);
        Assert.True(((Button)window.FindName("SaveCsvButton")).IsEnabled);
        Assert.False(((Button)window.FindName("CorrectButton")).IsEnabled);
        Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("EmptyResult")).Visibility);
        Assert.Equal("x,y,raw_code,type", window.BuildTable(',')!.TrimEnd());
        window.Close();
    });

    [Fact]
    public Task SortedRowsKeepTheSamePixelsAndExportOrder() => WpfTestHost.Run(async () =>
    {
        var window = new DefectPixelWindow { ShowActivated = false };
        try
        {
            window.Show();
            window.ShowResult(DetectNoDefects(), 4095);
            WpfTestHost.CaptureDialogIfRequested(window, "d5-defects-empty");
            DefectDetectionResult result = DetectTwoDefects();
            window.ShowResult(result, 4095);
            var list = (ListView)window.FindName("DefectList");
            Assert.Equal(result.Defects, list.Items.Cast<DefectPixelWindow.DefectRow>().Select(row => row.Source));
            string? csv = window.BuildTable(',');
            window.UpdateLayout();
            var header = Descendants(list).OfType<GridViewColumnHeader>()
                .Single(item => Equals(item.Content, "偏差 (σ)"));
            ((IInvokeProvider)new GridViewColumnHeaderAutomationPeer(header).GetPattern(PatternInterface.Invoke)!).Invoke();
            for (int i = 0; i < 100 && ((DefectPixelWindow.DefectRow)list.Items[0]).Source.Type != DefectType.Dead; i++)
                await Task.Delay(20);
            var sorted = list.Items.Cast<DefectPixelWindow.DefectRow>().ToArray();
            Assert.Equal(DefectType.Dead, sorted[0].Source.Type);
            Assert.Equal(result.Deviation(sorted[0].Source), sorted[0].Deviation);
            Assert.Equal(csv, window.BuildTable(','));
            WpfTestHost.CaptureDialogIfRequested(window, "d5-defects-sorted");

            window.UpdateLayout();
            var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            DefectPixel? activated = null;
            window.DefectActivated += pixel => activated = pixel;
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Control.MouseDoubleClickEvent,
                Source = row,
            });
            Assert.Equal(sorted[0].Source, activated);
        }
        finally
        {
            window.Close();
        }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject nested in Descendants(child))
                yield return nested;
        }
    }

    [Fact]
    public Task CappedResultsKeepTheNoticeAndOnlyRealizeVisibleRows() => WpfTestHost.Run(async () =>
    {
        var (result, rows) = await Task.Run(() =>
        {
            const int size = 340;
            using RawImage image = TestImages.FromCodes(
                Enumerable.Range(0, size * size).Select(i => (ushort)(i % 2 == 0 ? 200 : 1800)).ToArray(),
                size, size, bitDepth: 12);
            var result = DefectPixelDetector.Detect(image, sigmaFactor: 0.5);
            return (result, DefectPixelWindow.PrepareRows(result));
        });
        var window = new DefectPixelWindow { ShowActivated = false };
        try
        {
            window.Show();
            window.ShowResult(result, rows, 4095);
            await window.SortAsync(nameof(DefectPixelWindow.DefectRow.Code));
            window.UpdateLayout();
            var list = (ListView)window.FindName("DefectList");
            Assert.Equal(100_000, list.Items.Count);
            Assert.Contains("上限で打ち切り", ((TextBlock)window.FindName("SummaryText")).Text);
            Assert.InRange(Descendants(list).OfType<ListViewItem>().Count(), 1, 100);
            Assert.Equal(200, ((DefectPixelWindow.DefectRow)list.Items[0]).Code);
            Assert.Equal(1800, ((DefectPixelWindow.DefectRow)list.Items[^1]).Code);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task DoubleClickOnRow_JumpsButScrollBarHeaderAndRightButtonDoNot() => WpfTestHost.Run(() =>
    {
        // 一覧の行をダブルクリックするとその欠陥へ移動する。以前は ListView 全体の MouseDoubleClick で受けて
        // いたため、スクロールバーの▼を素早く2回押す・列見出しの境界をダブルクリックして列幅を合わせる・
        // 右ボタンでダブルクリックするだけでも、選択中の行の欠陥へ移動して32倍に拡大していた
        var cell = new TextBlock();
        cell.Inlines.Add(new System.Windows.Documents.Run("12"));
        var row = new ListViewItem { Content = cell };
        var run = (System.Windows.Documents.Run)cell.Inlines.FirstInline;

        Assert.True(DefectPixelWindow.IsRowDoubleClick(cell, MouseButton.Left));
        Assert.True(DefectPixelWindow.IsRowDoubleClick(run, MouseButton.Left));
        Assert.True(DefectPixelWindow.IsRowDoubleClick(row, MouseButton.Left));
        Assert.False(DefectPixelWindow.IsRowDoubleClick(cell, MouseButton.Right));

        var list = new ListView();
        var scrollBar = new ScrollBar();
        var header = new GridViewColumnHeader { Content = scrollBar };
        Assert.False(DefectPixelWindow.IsRowDoubleClick(scrollBar, MouseButton.Left));
        Assert.False(DefectPixelWindow.IsRowDoubleClick(header, MouseButton.Left));
        Assert.False(DefectPixelWindow.IsRowDoubleClick(list, MouseButton.Left));
        Assert.False(DefectPixelWindow.IsRowDoubleClick(null, MouseButton.Left));
    });

    private const string HdrReason = DefectCorrectionAvailability.HdrRefusal;

    /// <summary>欠陥を埋め込んでいない12bitの平坦な画像から検出した結果(0 件)。</summary>
    private static DefectDetectionResult DetectNoDefects()
    {
        const int size = 32;
        var codes = new ushort[size * size];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(1000 + (i % 2)); // σ>0にするためのディザ
        }

        using RawImage image = TestImages.FromCodes(codes, size, size, bitDepth: 12);
        DefectDetectionResult result = DefectPixelDetector.Detect(image);
        Assert.Empty(result.Defects);
        return result;
    }

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
