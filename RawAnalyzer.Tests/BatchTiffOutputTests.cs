using System.Windows;
using System.Windows.Controls;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 一括書き出しの16bitグレーTIFFの2つの形式(16bitフルスケール / raw の code のまま)。残課題 2026-10-02 K4。
/// </summary>
public class BatchTiffOutputTests
{
    [Theory]
    [InlineData(BatchFormat.Tiff16, 4, "16bitフルスケール")]       // 12bit の code 100 は 1600
    [InlineData(BatchFormat.Tiff16Code, 0, "raw の code のまま")]   // code 100 は 100
    public void Save_WritesValuesByFormatAndCompletionSaysWhich(BatchFormat format, int shift, string note)
    {
        const int width = 5;
        const int height = 3;
        var codes = new ushort[width * height];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(100 + i * 271);
        }

        codes[^1] = 4095;
        using RawImage image = TestImages.FromCodes(codes, width, height, bitDepth: 12);
        using TempTiff file = TempTiff.Write(Array.Empty<byte>());

        BatchTiffOutput.Save(format, image, 0, file.Path, default);

        using RawImage reloaded = ImageFileLoader.Load(file.Path).Luminance;
        Assert.Equal(16, reloaded.Format.BitDepth);
        for (int i = 0; i < codes.Length; i++)
        {
            Assert.Equal(codes[i] << shift, reloaded.GetPixel(i % width, i / width));
        }

        Assert.True(BatchTiffOutput.IsTiff(format));
        Assert.Contains(note, BatchTiffOutput.CompletionNote(format));
    }

    [Fact]
    public void OtherFormats_AreNotTiffAndAddNoNote()
    {
        foreach (BatchFormat format in new[]
                 { BatchFormat.Png8, BatchFormat.Jpeg8, BatchFormat.AviMjpeg, BatchFormat.Mp4H264 })
        {
            Assert.False(BatchTiffOutput.IsTiff(format));
            Assert.Null(BatchTiffOutput.CompletionNote(format));
        }
    }
}

/// <summary>
/// 一括書き出しダイアログの形式の選択肢と、形式ごとに出す設定。
/// </summary>
[Collection("WPF UI")]
public class BatchExportDialogFormatTests
{
    [Fact]
    public Task FormatChoices_MapToFormatsAndShowOnlyTheirSettings() => WpfTestHost.Run(() =>
    {
        // raw code のまま書く TIFF(K4)を 16bitフルスケールの TIFF の隣に足した。選択肢の並びと形式の対応、
        // TIFF ではどちらも表示調整の焼き込みを出さず、動画だけフレームレート・品質を出すこと
        var dialog = new BatchExportDialog(3, Path.Combine(Path.GetTempPath(), "export"));
        try
        {
            var combo = (ComboBox)dialog.FindName("FormatCombo");
            var lut = (FrameworkElement)dialog.FindName("DisplayLutCheck");
            var fps = (FrameworkElement)dialog.FindName("FpsPanel");
            var qualityNote = (TextBlock)dialog.FindName("QualityNoteText");
            BatchFormat[] expected =
            {
                BatchFormat.Png8, BatchFormat.Jpeg8, BatchFormat.Tiff16, BatchFormat.Tiff16Code,
                BatchFormat.AviMjpeg, BatchFormat.Mp4H264,
            };
            Assert.Equal(expected.Length, combo.Items.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                combo.SelectedIndex = i;
                Assert.Equal(expected[i], dialog.SelectedFormat);
                Assert.Equal(
                    BatchTiffOutput.IsTiff(expected[i]) ? Visibility.Collapsed : Visibility.Visible, lut.Visibility);
                bool video = expected[i] is BatchFormat.AviMjpeg or BatchFormat.Mp4H264;
                Assert.Equal(video ? Visibility.Visible : Visibility.Collapsed, fps.Visibility);
                if (video)
                {
                    Assert.Contains(expected[i] == BatchFormat.Mp4H264 ? "H.264" : "MJPEG", qualityNote.Text);
                }
            }

            Assert.Contains("16bitフルスケール", (string)((ComboBoxItem)combo.Items[2]).Content);
            Assert.Contains("raw code のまま", (string)((ComboBoxItem)combo.Items[3]).Content);
        }
        finally
        {
            dialog.Close();
        }
    });
}
