using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

[Collection("WPF UI")]
public class ImageProcessingDialogTests
{
    [Fact]
    public Task Dialog_LoadsThemeUpdatesDimensionsAndValidatesOnlyVisibleSettings()
    {
        return WpfTestHost.Run(() =>
        {
            var dialog = new ImageProcessingDialog("test.raw", 19, 11, BayerPattern.Rggb, false, true);
            var factor = (ComboBox)dialog.FindName("FactorCombo");
            var operation = (ComboBox)dialog.FindName("OperationCombo");
            var preview = (TextBlock)dialog.FindName("PreviewText");
            var policy = (TextBlock)dialog.FindName("PolicyText");
            var run = (Button)dialog.FindName("RunButton");
            Assert.Contains("8×4", preview.Text);
            factor.SelectedIndex = 2;
            Assert.Contains("4×2", preview.Text);
            Assert.Contains("右端3列・下端3行", preview.Text);
            Assert.Contains("8×8", policy.Text);
            Assert.Equal(4, dialog.ReadChoice().Factor);
            ((ComboBox)dialog.FindName("AggregationCombo")).SelectedIndex = 1;
            Assert.Contains("最大16倍", policy.Text);
            Assert.Equal(BinningMode.Sum, dialog.ReadChoice().Mode);
            CaptureIfRequested(dialog, "binning");

            operation.SelectedIndex = 4;
            var sigma = (TextBox)dialog.FindName("SigmaBox");
            var amount = (TextBox)dialog.FindName("AmountBox");
            foreach (string invalid in new[] { "NaN", "-1", "abc" })
            {
                sigma.Text = invalid;
                Assert.False(run.IsEnabled);
            }

            sigma.Text = "1.3";
            amount.Text = "6";
            Assert.False(run.IsEnabled);
            amount.Text = "1.5";
            Assert.True(run.IsEnabled);
            Assert.Equal(new(ImageFilterKind.UnsharpMask, 1, 1.3, 1.5), dialog.ReadChoice().Filter);
            CaptureIfRequested(dialog, "filter");

            sigma.Text = "NaN";
            operation.SelectedIndex = 5;
            Assert.True(run.IsEnabled); // Sobelは非表示のσを使わない
            Assert.Equal(Visibility.Collapsed, ((StackPanel)dialog.FindName("KernelPanel")).Visibility);
            Assert.Equal(ImageFilterKind.Sobel, dialog.ReadChoice().Filter!.Kind);
            dialog.Close();

            var rgb = new ImageProcessingDialog("test.png", 18, 10, BayerPattern.Bggr, true, true);
            Assert.Contains("9×5", ((TextBlock)rgb.FindName("PreviewText")).Text);
            rgb.Close();

            var large = new ImageProcessingDialog("large.raw", 50000, 50000, BayerPattern.None, false, false);
            Assert.False(((Button)large.FindName("RunButton")).IsEnabled);
            ((ComboBox)large.FindName("OperationCombo")).SelectedIndex = 0;
            ((ComboBox)large.FindName("FactorCombo")).SelectedIndex = 3;
            Assert.True(((Button)large.FindName("RunButton")).IsEnabled);
            large.Close();
        });
    }

    [Fact]
    public Task Dialog_ReadsFullWidthNumbers()
    {
        // IME がオンのまま打った全角の σ・強度も、他の数値入力欄と同じく読む(以前は不正として実行できなかった)
        return WpfTestHost.Run(() =>
        {
            var dialog = new ImageProcessingDialog("test.raw", 19, 11, BayerPattern.Rggb, false, true);
            ((ComboBox)dialog.FindName("OperationCombo")).SelectedIndex = 4;
            ((TextBox)dialog.FindName("SigmaBox")).Text = "１．３";
            ((TextBox)dialog.FindName("AmountBox")).Text = "１。５";

            Assert.True(((Button)dialog.FindName("RunButton")).IsEnabled);
            Assert.Equal(new(ImageFilterKind.UnsharpMask, 1, 1.3, 1.5), dialog.ReadChoice().Filter);
            dialog.Close();
        });
    }

    [Theory]
    [InlineData("SigmaBox", "20", "σは0.1〜10の有限値です。")]
    [InlineData("AmountBox", "6", "強度は0〜5の有限値です。")]
    public Task Dialog_OutOfRangeMessage_HasNoDeveloperParameterName(string box, string text, string expected)
    {
        // 範囲外の σ・強度の説明は ArgumentOutOfRangeException の Message をそのまま出していたので、
        // 「σは0.1〜10の有限値です。 (Parameter 'Sigma')」と開発用の英語の引数名が付いていた
        return WpfTestHost.Run(() =>
        {
            var dialog = new ImageProcessingDialog("test.raw", 19, 11, BayerPattern.Rggb, false, true);
            ((ComboBox)dialog.FindName("OperationCombo")).SelectedIndex = 4;
            ((TextBox)dialog.FindName(box)).Text = text;

            Assert.False(((Button)dialog.FindName("RunButton")).IsEnabled);
            Assert.Equal(expected, ((TextBlock)dialog.FindName("ErrorText")).Text);
            dialog.Close();
        });
    }

    private static void CaptureIfRequested(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("RAWANALYZER_UI_SNAPSHOTS");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, double.PositiveInfinity));
        int height = (int)Math.Ceiling(content.DesiredSize.Height);
        content.Arrange(new Rect(0, 0, window.Width, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext dc = background.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, window.Width, height));
        }

        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, $"{name}-dialog.png"));
        encoder.Save(output);
    }
}
