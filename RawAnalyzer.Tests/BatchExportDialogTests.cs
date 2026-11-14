using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 一括書き出しダイアログのフレームレート欄(編集可能コンボ)の入力の知らせ方。
/// </summary>
[Collection("WPF UI")]
public class BatchExportDialogTests
{
    [Fact]
    public Task TiffEstimateUsesEveryPageSizeAndSharesThePageSuffix() => WpfTestHost.Run(async () =>
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tif");
        await Task.Run(() =>
        {
            var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.None };
            foreach (var (width, height) in new[] { (24, 12), (31, 23) })
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
                    PixelFormats.Gray16, null, new ushort[width * height], width * 2)));
            using var stream = File.Create(path);
            encoder.Save(stream);
        });
        var dialog = new BatchExportDialog([path], Path.GetTempPath(), rawTargets: false);
        try
        {
            ((ComboBox)dialog.FindName("FormatCombo")).SelectedIndex = 2;
            dialog.Show();
            var example = (TextBlock)dialog.FindName("OutputExample");
            var total = (TextBlock)dialog.FindName("TotalEstimate");
            for (int i = 0; i < 100 && !total.Text.Contains("バイト"); i++)
                await Task.Delay(20);
            Assert.EndsWith("_p0001.tif", example.Text);
            Assert.Contains("2,350 バイト", total.Text);
        }
        finally
        {
            dialog.Close();
            await Task.Run(() => File.Delete(path));
        }
    });

    [Fact]
    public Task TargetsAreVirtualizedAndExamplesFollowTheOutputFormat() => WpfTestHost.Run(() =>
    {
        string[] targets = Enumerable.Range(1, 3000).Select(i => $@"C:\capture\scene_{i:D4}.raw").ToArray();
        long? bytes = ExportSize.ForBatchTiff(64, 48, targets.Length * 2);
        var dialog = new BatchExportDialog(targets, @"C:\capture\export", rawFrameCount: 2, knownTiffBytes: bytes);
        try
        {
            ((Expander)dialog.FindName("TargetsSection")).IsExpanded = true;
            dialog.Show();
            dialog.UpdateLayout();
            var list = (ListBox)dialog.FindName("TargetList");
            Assert.Equal(targets.Length, list.Items.Count);
            Assert.InRange(Enumerable.Range(0, targets.Length)
                .Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null), 1, 60);
            var format = (ComboBox)dialog.FindName("FormatCombo");
            var example = (TextBlock)dialog.FindName("OutputExample");
            var total = (TextBlock)dialog.FindName("TotalEstimate");
            format.SelectedIndex = 5;
            Assert.EndsWith("scene_0001_seq.mp4", example.Text);
            Assert.Contains("圧縮率", total.Text);
            format.SelectedIndex = 2;
            Assert.EndsWith("scene_0001_f000.tif", example.Text);
            Assert.Contains($"{bytes:N0} バイト", total.Text);
            WpfTestHost.CaptureDialogIfRequested(dialog, "d2-batch-export");
        }
        finally
        {
            dialog.Close();
        }
    });

    [Theory]
    [InlineData("abc", "既定の 15 fps")]  // 読めない入力
    [InlineData("-5", "既定の 15 fps")]   // 負の値
    [InlineData("0", "既定の 15 fps")]
    [InlineData("500", "240 fps")]       // 上限を超える値
    [InlineData("0.2", "1 fps")]         // 書き出せる下限(整数で 1 fps)を下回る値
    public Task FpsCombo_UnusableInput_ShowsWhatFrameRateIsUsed(string text, string expected) => WpfTestHost.Run(() =>
    {
        // 読めない・0 以下の入力は既定値へ、範囲外は範囲へ落として書き出す(挙動は従来どおり)。
        // 以前は黙って落としたので、打った値と違うフレームレートの動画になったことが見えなかった。
        // ファイル一覧の絞り込み欄と同じく、赤枠とツールチップの理由で知らせる
        var dialog = new BatchExportDialog(new[] { @"C:\capture\scene_001.raw", @"C:\capture\scene_002.raw", @"C:\capture\scene_003.raw" }, @"C:\capture\out", frameWidth: 640, frameHeight: 480);
        try
        {
            var format = (ComboBox)dialog.FindName("FormatCombo");
            var fps = (ComboBox)dialog.FindName("FpsCombo");
            format.SelectedIndex = 4; // MP4(フレームレート欄が出る)
            fps.ApplyTemplate();
            object help = fps.ToolTip;
            FieldFeedback.AssertValid(fps);

            fps.Text = text;
            Assert.Contains(expected, FieldFeedback.AssertInvalid(fps));

            fps.Text = "24 fps"; // 使える値へ直せば消え、元の説明へ戻る
            FieldFeedback.AssertValid(fps);
            Assert.Equal(help, fps.ToolTip);
        }
        finally
        {
            dialog.Close();
        }
    });
}
