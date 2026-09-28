using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 一括書き出し(PNG/JPEG・動画)の1枚ぶんの焼き込みの検証。
/// </summary>
public class BatchFrameRendererTests
{
    /// <summary>行の和が1(白→白)で非対角が負の、典型的なカメラRGB→表示RGBの行列。</summary>
    private static readonly ColorMatrix TypicalCcm = new(
        1.6, -0.4, -0.2,
        -0.2, 1.4, -0.2,
        0.0, -0.5, 1.5);

    /// <summary>1枚ぶんの出力先。</summary>
    public enum Output
    {
        /// <summary>静止画(PNG)。</summary>
        Png,

        /// <summary>MJPEG AVI のフレーム(JPEG)。</summary>
        AviJpeg,

        /// <summary>MP4 のフレーム(RGB24)。</summary>
        Mp4Rgb24,
    }

    [Theory]
    [InlineData(Output.Png)]
    [InlineData(Output.AviJpeg)]
    [InlineData(Output.Mp4Rgb24)]
    public void MixedBitDepthTiff_DevelopsSaturatedPageWithItsOwnBitDepth(Output output)
    {
        // Codexレビュー(2026-09-29)#6。16bit→12bit の2ページTIFFを Bayer(RGGB)としてカラー現像で書き出す。
        // 12bitページは全画素が白レベルの 4095(内部値 4095<<4 = 65520)で飽和している。
        // 現像LUTの白飛びの判定は画素のビット深度で決まるのに、書き出しの開始時に表示していた16bitページの
        // LUTを全ページに使い回していたため、65520 を16bitの白点 65535 未満とみなし、WB(2,1,1.5)と
        // 典型的なCCMで RGB(255,178,255) に着色していた。ページのビット深度(12bit)のLUTなら白のまま
        const int size = 4;
        byte[] sixteenBit = TiffBuilder.SampleBytes(
            Enumerable.Repeat((ushort)30000, size * size).ToArray(), 16);
        byte[] twelveBit = TiffBuilder.PackRows(Enumerable.Repeat(4095, size * size).ToArray(), size, 12);
        using TempTiff file = TempTiff.Write(new TiffBuilder().Build(
            TiffBuilder.GrayPage(size, size, 16, sixteenBit),
            TiffBuilder.GrayPage(size, size, 12, twelveBit)));

        // 開始時のパラメータ(ビット深度を含む)は表示中の先頭ページ(16bit)のもの
        var renderer = new BatchFrameRenderer(
            BayerPattern.Rggb,
            new DevelopParameters(
                GainR: 2.0, GainB: 1.5, Gamma: 1.0, Matrix: TypicalCcm, SourceBitDepth: 16),
            DisplayLut.Create(new DisplayParameters()));

        // 実際の書き出しと同じく全ページを順に焼き込む
        byte[]? saturatedPage = null;
        foreach (FileFrame entry in FileFrameReader.Read(file.Path, new RawFormat { Width = 1, Height = 1 }))
        {
            Assert.Equal(entry.Index == 0 ? 16 : 12, entry.Image.Format.BitDepth);
            byte[] rgb = Render(renderer, entry, output);
            if (entry.Index == 1)
            {
                saturatedPage = rgb;
            }
        }

        Assert.NotNull(saturatedPage);
        Assert.Equal(size * size * 3, saturatedPage.Length);

        // JPEG は非可逆なので数コードの誤差を許す(着色していれば G は 178 前後)
        int tolerance = output == Output.AviJpeg ? 3 : 0;
        Assert.All(saturatedPage, v => Assert.InRange((int)v, 255 - tolerance, 255));
    }

    [Theory]
    [InlineData(Output.Png)]
    [InlineData(Output.AviJpeg)]
    [InlineData(Output.Mp4Rgb24)]
    public void PageOverPixelLimit_IsRefusedWithFilePageAndSize(Output output)
    {
        // 開始前の1億画素の判定は表示中の画像の寸法だけなので、寸法の異なるページ・連番のファイルに上限を
        // 超えるものがあると、そのまま1枚を丸ごと8bitへ焼き込もうとしてメモリ不足や ImageExport の上限で
        // 止まっていた。1枚ごとに判定し、どのファイルの何ページ目で寸法がいくつか、TIFF16 なら書き出せることを
        // 示して断る。巨大な画像を作らずに試せるよう、上限を 4×4=16画素へ下げる(ちょうど上限の1ページ目は通す)
        using TempTiff file = TempTiff.Write(new TiffBuilder().Build(
            TiffBuilder.GrayPage(4, 4, 16, TiffBuilder.SampleBytes(new ushort[16], 16)),
            TiffBuilder.GrayPage(5, 4, 16, TiffBuilder.SampleBytes(new ushort[20], 16))));
        var renderer = new BatchFrameRenderer(
            BayerPattern.None, new DevelopParameters(), DisplayLut.Create(new DisplayParameters()), maxPixels: 16);

        var rendered = new List<int>();
        NotSupportedException? refused = null;
        foreach (FileFrame entry in FileFrameReader.Read(file.Path, new RawFormat { Width = 1, Height = 1 }))
        {
            try
            {
                Render(renderer, entry, output);
                rendered.Add(entry.Index);
            }
            catch (NotSupportedException ex)
            {
                refused = ex;
            }
        }

        Assert.Equal(new[] { 0 }, rendered);
        Assert.NotNull(refused);
        Assert.Contains(Path.GetFileName(file.Path), refused.Message);
        Assert.Contains("2ページ目", refused.Message);
        Assert.Contains("5×4", refused.Message);
        Assert.Contains("TIFF", refused.Message);
    }

    private static byte[] Render(BatchFrameRenderer renderer, FileFrame entry, Output output)
    {
        switch (output)
        {
            case Output.Png:
                {
                    string directory = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
                    try
                    {
                        renderer.Save(entry, path, jpeg: false, CancellationToken.None);
                        using FileStream stream = File.OpenRead(path);
                        return DecodeRgb24(stream);
                    }
                    finally
                    {
                        File.Delete(path);
                    }
                }

            case Output.AviJpeg:
                {
                    byte[] jpeg = renderer.EncodeJpeg(
                        entry, VideoQualitySettings.JpegQuality(VideoQuality.High), CancellationToken.None);
                    using var stream = new MemoryStream(jpeg);
                    return DecodeRgb24(stream);
                }

            default:
                // 返す配列は次の呼び出しで上書きされるので複製する
                return renderer.RenderRgb24(entry, CancellationToken.None).ToArray();
        }
    }

    private static byte[] DecodeRgb24(Stream stream)
    {
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var rgb24 = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Rgb24, null, 0);
        var pixels = new byte[rgb24.PixelWidth * rgb24.PixelHeight * 3];
        rgb24.CopyPixels(pixels, rgb24.PixelWidth * 3, 0);
        return pixels;
    }
}
