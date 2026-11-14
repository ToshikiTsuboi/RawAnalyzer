using System.Windows;
using System.Windows.Controls;
using RawAnalyzer.App.Views;
using RawAnalyzer.App.Services;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 保存ダイアログの raw 出力の表示と選択結果。
/// </summary>
[Collection("WPF UI")]
public class SaveDialogTests
{
    [Theory]
    [InlineData(SaveFormat.Raw, 1, false, false, 300)]
    [InlineData(SaveFormat.Raw, 2, false, false, 600)]
    [InlineData(SaveFormat.FloatRaw, 2, false, false, 400)]
    [InlineData(SaveFormat.Tiff16, 2, true, false, 600)]
    [InlineData(SaveFormat.Png16, 2, false, false, 200)]
    [InlineData(SaveFormat.Png8, 2, false, true, 300)]
    [InlineData(SaveFormat.Jpeg8, 2, false, false, 100)]
    public void EstimateMatchesExportPixelLayout(SaveFormat format, int bytes, bool color, bool demosaic, long expected)
    {
        var estimate = ExportSize.ForSave(format, 100, 3, bytes, color, demosaic)!.Value;
        Assert.Equal(expected, estimate.Bytes);
        Assert.Equal(format is SaveFormat.Png16 or SaveFormat.Png8 or SaveFormat.Jpeg8, estimate.Label.Contains("圧縮前"));
    }

    [Fact]
    public Task ProcessingOptions_FollowFormatAndBayerWithoutChangingChoice() => WpfTestHost.Run(() =>
    {
        var dialog = new SaveDialog(1920 * 1080, frameCount: 3);
        var bayer = new SaveDialog(1920 * 1080, hasBayer: true);
        try
        {
            var combo = (ComboBox)dialog.FindName("FormatCombo");
            var group = (FrameworkElement)dialog.FindName("ProcessingOptions");
            for (int i = 0; i < 3; i++)
            {
                combo.SelectedIndex = i;
                Assert.Equal(Visibility.Collapsed, group.Visibility);
                Assert.False(dialog.BuildChoice().ApplyDisplayLut);
                Assert.False(dialog.BuildChoice().ApplyDemosaic);
            }
            combo.SelectedIndex = 3;
            Assert.Equal(Visibility.Visible, group.Visibility);
            foreach (string name in new[] { "WbCheck", "MatrixCheck", "DemosaicCheck" })
            {
                Assert.Equal(Visibility.Collapsed, ((CheckBox)dialog.FindName(name)).Visibility);
            }
            Assert.True(dialog.BuildChoice().ApplyDisplayLut);
            Assert.False(dialog.BuildChoice().ApplyDemosaic);
            ((ComboBox)bayer.FindName("FormatCombo")).SelectedIndex = 3;
            Assert.True(bayer.BuildChoice().ApplyDemosaic);
            combo.SelectedIndex = 0;
            dialog.Show();
            dialog.UpdateLayout();
            WpfTestHost.CaptureDialogIfRequested(dialog, "d1-save");
        }
        finally
        {
            dialog.Close();
            bayer.Close();
        }
    });

    [Fact]
    public Task EightBitImage_RawIsDescribedAsOneBytePerPixelWithoutByteOrder()
    {
        // 全体レビュー 2026-10-01 B93。raw 保存は常に「16bitコンテナ」と表示し、詰め方向・エンディアンも選ばせて
        // サイドカーに記録していたが、RawSaver は 8bit 以下の画像を1画素1バイトで書き、詰め方向・エンディアンを
        // 使わない。要約・サイドカーを信じて 16bit として読むと壊れた画像になる。8bit の画像では1バイト/画素と示し、
        // 詰め方向・エンディアンを出さない
        return WpfTestHost.Run(() =>
        {
            var dialog = new SaveDialog(100, bitDepth: 8);
            try
            {
                var format = (ComboBox)dialog.FindName("FormatCombo");
                var options = (FrameworkElement)dialog.FindName("RawOptions");
                var summary = (TextBlock)dialog.FindName("SummaryText");
                Assert.Equal(0, format.SelectedIndex);

                Assert.DoesNotContain("16bit", (string)((ComboBoxItem)format.Items[0]).Content);
                Assert.Contains("1バイト/画素", (string)((ComboBoxItem)format.Items[0]).Content);
                Assert.Equal(Visibility.Collapsed, options.Visibility);
                Assert.DoesNotContain("16bitコンテナ", summary.Text);
                Assert.Contains("1バイト/画素", summary.Text);

                SaveChoice choice = dialog.BuildChoice();
                Assert.Equal(SaveFormat.Raw, choice.Format);
                Assert.Equal(1, choice.RawBytesPerPixel);
                Assert.Contains("1バイト/画素", choice.Summary);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(16)]
    public Task DeeperImage_RawKeepsSixteenBitContainerWithByteOrder(int bitDepth)
    {
        return WpfTestHost.Run(() =>
        {
            var dialog = new SaveDialog(100, bitDepth: bitDepth);
            try
            {
                var format = (ComboBox)dialog.FindName("FormatCombo");
                var options = (FrameworkElement)dialog.FindName("RawOptions");
                var summary = (TextBlock)dialog.FindName("SummaryText");

                Assert.Contains("16bitコンテナ", (string)((ComboBoxItem)format.Items[0]).Content);
                Assert.Equal(Visibility.Visible, options.Visibility);
                Assert.Contains("16bitコンテナ", summary.Text);
                ((ComboBox)dialog.FindName("EndianCombo")).SelectedIndex = 1;

                SaveChoice choice = dialog.BuildChoice();
                Assert.Equal(2, choice.RawBytesPerPixel);
                Assert.Equal(Endianness.Big, choice.Endianness);
            }
            finally
            {
                dialog.Close();
            }
        });
    }
}
