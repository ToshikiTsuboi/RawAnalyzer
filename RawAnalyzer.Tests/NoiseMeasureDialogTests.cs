using System.Windows.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// ノイズ測定ダイアログの、対象画像が替わったとき(<see cref="NoiseMeasureDialog.UpdateSource"/>)の表示と既定値、
/// 「測定実行」で依頼する測定の条件。
/// </summary>
/// <remarks>
/// MainWindow は開く・ファイル/ページ送り・処理結果での差し替え・HDR表示の出入りでダイアログを閉じずに
/// UpdateSource を呼ぶ。
/// </remarks>
[Collection("WPF UI")]
public class NoiseMeasureDialogTests
{
    private static NoiseMeasureDialog Open(int maxCode) =>
        new("dark_001.raw", null, maxCode, hasRoi: false, expectedReferenceSize: 0);

    private static TextBox Saturation(NoiseMeasureDialog dialog) => (TextBox)dialog.FindName("SaturationBox");

    [Fact]
    public Task DefaultSaturation_FollowsHigherBitDepth() => WpfTestHost.Run(() =>
    {
        // 12bit の画像で開いたダイアログのまま、ビニング・フィルタの結果(16bit)・16bit TIFF・HDR合成へ
        // 差し替えた。以前は上限を超えたときしか直さず、既定値 4095 が16bitのコード単位のまま残って
        // DR が約24dB(4stop)小さく出ていた
        NoiseMeasureDialog dialog = Open(4095);
        Assert.Equal("4095", Saturation(dialog).Text);

        dialog.UpdateSource("dark_001.raw [メディアン 3×3]", null, 65535, false, 0);

        Assert.Equal("65535", Saturation(dialog).Text);
        dialog.Close();
    });

    [Fact]
    public Task DefaultSaturation_FollowsLowerBitDepth() => WpfTestHost.Run(() =>
    {
        NoiseMeasureDialog dialog = Open(65535);

        dialog.UpdateSource("dark_12bit.raw", null, 4095, false, 0);

        Assert.Equal("4095", Saturation(dialog).Text);
        dialog.Close();
    });

    [Theory]
    [InlineData(4095, "3900", 65535, "62400")]   // 12bit で入れた飽和レベルを、16bit の同じ水準へ
    [InlineData(65535, "62400", 4095, "3900")]   // 16bit → 12bit
    [InlineData(1023, "1000.5", 4095, "4002")]   // 10bit → 12bit(小数も同じ比で)
    [InlineData(4095, "3900", 4095, "3900")]     // ビット深度が同じなら入れた値のまま
    public Task EnteredSaturation_IsRescaledToNewBitDepth(
        int previousMax, string entered, int newMax, string expected) => WpfTestHost.Run(() =>
    {
        // 飽和レベルは σ と同じく raw code(その画像のビット深度)の単位。ビット深度が変わったら、
        // 入れた値も同じ信号水準のコードへ換算する(そのまま残すと 2^Δbit 倍ずれた DR になる)
        NoiseMeasureDialog dialog = Open(previousMax);
        Saturation(dialog).Text = entered;

        dialog.UpdateSource("other.raw", null, newMax, false, 0);

        Assert.Equal(expected, Saturation(dialog).Text);
        dialog.Close();
    });

    private static TextBlock Result(NoiseMeasureDialog dialog) => (TextBlock)dialog.FindName("ResultText");

    private static readonly NoiseMeasurement Measured = new(10_000, 64.2, 3.1, 2.1, 2.28, 4095);

    [Fact]
    public Task ResultOfPreviousImage_IsClearedWhenTargetChanges() => WpfTestHost.Run(() =>
    {
        // dark_001 の測定結果を出したまま dark_002 へ送った(またはフィルタを適用した)。以前は対象の名前だけが
        // 替わり、結果欄とコピーされる値は dark_001 のまま残って、新しい画像の値として記録されてしまった
        var first = new object();
        var dialog = new NoiseMeasureDialog("dark_001.raw", null, 4095, false, 0, first);
        string initial = Result(dialog).Text;
        dialog.ShowResult(Measured, 12, "dark_100.raw");
        Assert.Contains("2.100", Result(dialog).Text);

        dialog.UpdateSource("dark_002.raw", null, 4095, false, 0, new object());

        Assert.Equal(initial, Result(dialog).Text);
        Assert.Equal("", dialog.CopyText);
        dialog.Close();
    });

    [Fact]
    public Task ResultOfSameImage_IsKeptWhenReopened() => WpfTestHost.Run(() =>
    {
        // 同じ画像のままメニューから開き直した(ROI の有無も同じ経路で更新される)。結果は消さない
        var image = new object();
        var dialog = new NoiseMeasureDialog("dark_001.raw", null, 4095, false, 0, image);
        dialog.ShowResult(Measured, 12, null);
        string shown = Result(dialog).Text;

        dialog.UpdateSource("dark_001.raw", null, 4095, true, 0, image);

        Assert.Equal(shown, Result(dialog).Text);
        Assert.Equal(shown, dialog.CopyText);
        dialog.Close();
    });

    [Fact]
    public Task ResultOfPreviousFrame_IsClearedWhenFrameChanges() => WpfTestHost.Run(() =>
    {
        // 残課題 2026-10-02 A1。マルチフレーム raw のフレーム送りでは画像はそのままでフレームだけが替わる。
        // 対象を画像だけで見分けていたため、送った後も前のフレームの結果が残り、「結果をコピー」で送った先の
        // フレームの値として記録されてしまった。MainWindow は対象を画像とフレームの組(AnalysisSource)で渡し、
        // ダイアログは値で照合する(同じ画像・同じフレームなら、作り直した組でも同じ対象として結果を残す)
        using RawImage image = TestImages.FromCodes(new ushort[8], new RawFormat
        {
            Width = 2, Height = 2, BitDepth = 12, FrameCount = 2,
        });
        var dialog = new NoiseMeasureDialog(
            "dark.raw [フレーム 1/2]", null, 4095, false, 0, new AnalysisSource(image, 0));
        dialog.ShowResult(Measured, 12, null);
        string shown = Result(dialog).Text;

        dialog.UpdateSource("dark.raw [フレーム 1/2]", null, 4095, true, 0, new AnalysisSource(image, 0));
        Assert.Equal(shown, dialog.CopyText);

        dialog.UpdateSource("dark.raw [フレーム 2/2]", null, 4095, false, 0, new AnalysisSource(image, 1));
        Assert.Equal("", dialog.CopyText);
        Assert.DoesNotContain("2.100", Result(dialog).Text);
        Assert.Equal("対象 A: dark.raw [フレーム 2/2]", ((TextBlock)dialog.FindName("SourceText")).Text);
        dialog.Close();
    });

    [Fact]
    public Task Result_NamesMeasuredTarget() => WpfTestHost.Run(() =>
    {
        // コピーした値の出典が分かるよう、結果の本文にも測った対象の名前を入れる
        var dialog = new NoiseMeasureDialog("dark_001.raw [フレーム 3/8]", null, 4095, false, 0, new object());

        dialog.ShowResult(Measured, 12, "dark_100.raw");

        Assert.StartsWith("対象 A: dark_001.raw [フレーム 3/8]", dialog.CopyText);
        dialog.Close();
    });

    /// <summary>2枚目の確認を、テストが完了させるまで待たせるダイアログを作る。</summary>
    private static NoiseMeasureDialog OpenWithPendingReferenceCheck(
        int maxCode, out TaskCompletionSource<(bool, long)> check, out List<NoiseMeasureRequest> requests)
    {
        var dialog = new NoiseMeasureDialog("8bit-A.raw", null, maxCode, hasRoi: true, 0, new object());
        ((TextBox)dialog.FindName("ReferenceBox")).Text = @"Z:\dark_b.raw";
        var pending = new TaskCompletionSource<(bool, long)>();
        dialog.ReferenceProbe = (_, _) => pending.Task;
        check = pending;
        var sent = new List<NoiseMeasureRequest>();
        dialog.MeasureRequested += sent.Add;
        requests = sent;
        return dialog;
    }

    [Fact]
    public Task TargetReplacedWhileCheckingReference_DoesNotMeasureWithPreviousSaturation() => WpfTestHost.Run(async () =>
    {
        // レビュー 2026-10-03 R1。8bit の画像で「測定実行」を押し、2枚目の実在を確かめる間に 16bit の画像へ替えた。
        // 以前は確かめた後に2枚目のパスしか照合せず、押したときに読んだ飽和信号レベルで依頼したため、画面は 65535 なのに
        // 255 で測り、DR を約 48dB 小さく出した。確かめる間に対象が替わったら測定を始めない(押し直せる)
        NoiseMeasureDialog dialog = OpenWithPendingReferenceCheck(
            255, out TaskCompletionSource<(bool, long)> check, out List<NoiseMeasureRequest> requests);

        Task run = dialog.RunAsync();
        dialog.UpdateSource("16bit-B.raw", null, 65535, true, 0, new object());
        check.SetResult((true, -1));
        await run;

        Assert.Empty(requests);
        Assert.True(((Button)dialog.FindName("RunButton")).IsEnabled);

        // 押し直すと替わった後の対象の飽和信号レベルで測る
        dialog.ReferenceProbe = (_, _) => Task.FromResult((true, -1L));
        await dialog.RunAsync();
        Assert.Equal(65535, Assert.Single(requests).SaturationCode);
        dialog.Close();
    });

    [Theory]
    [InlineData("saturation")]
    [InlineData("roi")]
    public Task SettingChangedWhileCheckingReference_DoesNotMeasure(string changed) => WpfTestHost.Run(async () =>
    {
        // 確かめる間に飽和信号レベル・「ROI内のみ」が変わったら、押したときの設定でも変わった後の設定でも測らない
        // (押したときの値で測ると変えた入力が無視され、変わった後の値で測ると押したときと別の条件になる)
        NoiseMeasureDialog dialog = OpenWithPendingReferenceCheck(
            4095, out TaskCompletionSource<(bool, long)> check, out List<NoiseMeasureRequest> requests);
        ((CheckBox)dialog.FindName("RoiCheck")).IsChecked = true;

        Task run = dialog.RunAsync();
        if (changed == "saturation")
        {
            Saturation(dialog).Text = "3900";
        }
        else
        {
            dialog.SetRoiAvailability(false); // ROI を消した(チェックも外れる)
        }

        check.SetResult((true, -1));
        await run;

        Assert.Empty(requests);
        dialog.Close();
    });

    [Fact]
    public Task UnchangedWhileCheckingReference_MeasuresAsRequested() => WpfTestHost.Run(async () =>
    {
        NoiseMeasureDialog dialog = OpenWithPendingReferenceCheck(
            4095, out TaskCompletionSource<(bool, long)> check, out List<NoiseMeasureRequest> requests);
        ((CheckBox)dialog.FindName("RoiCheck")).IsChecked = true;

        Task run = dialog.RunAsync();
        check.SetResult((true, -1));
        await run;

        Assert.Equal(new NoiseMeasureRequest(@"Z:\dark_b.raw", 4095, UseRoi: true), Assert.Single(requests));
        dialog.Close();
    });

    [Fact]
    public Task SaturationAboveNewMaximum_IsClampedToMaximum() => WpfTestHost.Run(() =>
    {
        // 同じビット深度でも上限を超える値(手入力の誤りなど)は従来どおり上限へ戻す
        NoiseMeasureDialog dialog = Open(4095);
        Saturation(dialog).Text = "5000";

        dialog.UpdateSource("other.raw", null, 4095, false, 0);

        Assert.Equal("4095", Saturation(dialog).Text);
        dialog.Close();
    });

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-100")]
    [InlineData("")]
    public Task InvalidSaturation_IsMarkedWhileTyping(string text) => WpfTestHost.Run(() =>
    {
        // 以前は「測定実行」を押すまで何も示さなかった。ファイル一覧の絞り込み欄と同じく、打った時点で
        // 赤枠とツールチップの理由で示す(実行時の確認は従来どおり)
        NoiseMeasureDialog dialog = Open(4095);
        FieldFeedback.AssertValid(Saturation(dialog));

        Saturation(dialog).Text = text;
        Assert.Equal("飽和信号レベルは正の数値で指定してください。", FieldFeedback.AssertInvalid(Saturation(dialog)));

        Saturation(dialog).Text = "３９００"; // 全角も他の数値欄と同じく読む
        FieldFeedback.AssertValid(Saturation(dialog));
        dialog.Close();
    });
}
