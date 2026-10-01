using System.Windows.Controls;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// ノイズ測定ダイアログの、対象画像が替わったとき(<see cref="NoiseMeasureDialog.UpdateSource"/>)の表示と既定値。
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
}
