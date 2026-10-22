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
    [InlineData(Output.Png)] // Bake の経路(AVI の JPEG も同じ Bake の後で符号化するだけ)
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

        // 着色していれば G は 178 前後
        Assert.All(saturatedPage, v => Assert.Equal((byte)255, v));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithoutDisplayAdjustment_EachBitDepthMapsMaxCodeToWhite_AndEightBitIsIdentity(bool develop)
    {
        // 「表示調整を焼き込む」オフ(lut = null)では、1枚ごとのビット深度の既定(白=最大コード<<(16−N))で焼き込む。
        // 以前は白点65535の恒等LUTを全ビット深度に使い、8bit(255 = 65280)が254になっていた
        var codes8 = new ushort[256];
        var codes12 = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            codes8[i] = (ushort)i;
            codes12[i] = (ushort)(i * 4095 / 255);
        }

        var renderer = new BatchFrameRenderer(
            develop ? BayerPattern.Rggb : BayerPattern.None,
            new DevelopParameters(Gamma: 1.0, WhitePoint: 65535),
            lut: null);
        foreach ((ushort[] codes, int bitDepth) in new[] { (codes8, 8), (codes12, 12) })
        {
            using RawImage image = TestImages.FromCodes(codes, 16, 16, bitDepth);
            byte[] rgb = renderer.RenderRgb24(
                new FileFrame(image, null, 0, 0, 1, false, "x.raw"), CancellationToken.None).ToArray();
            if (!develop)
            {
                for (int i = 0; i < 256; i++)
                {
                    Assert.Equal((byte)Math.Round(codes[i] * 255.0 / ((1 << bitDepth) - 1)), rgb[i * 3]);
                }
            }

            // 最大コードの画素(右下)は白
            Assert.Equal(255, rgb[(255 * 3) + (develop ? 2 : 0)]);
        }
    }

    [Theory]
    [InlineData(Output.Png, 1, 0)]
    [InlineData(Output.AviJpeg, 3, 0)]
    [InlineData(Output.Mp4Rgb24, 2, 1)]
    [InlineData(Output.Mp4Rgb24, 2, -1)]
    public void InterleavedHdrWithMismatchedBayerPhase_RefusesDevelopment(
        Output output, int lineBlock, int rowOffset)
    {
        // 一括出力は表示モードを経由しないため、実際に焼き込む各入力で位相を検査する必要がある。
        // Bayer は読み込み時の None から一括出力の設定で指定される場合もある。
        var format = new RawFormat
        {
            Width = 8, Height = 24, BitDepth = 12, Bayer = BayerPattern.None,
            Hdr = HdrMode.LineInterleaved, HdrStages = 2,
            HdrLineBlock = lineBlock, HdrRowOffset = rowOffset,
        };
        using RawImage image = TestImages.FromCodes(new ushort[8 * 24], format);
        var entry = new FileFrame(image, null, 0, 0, 1, false, "hdr.raw");
        var renderer = new BatchFrameRenderer(BayerPattern.Rggb, new DevelopParameters(), lut: null);

        NotSupportedException error = Assert.Throws<NotSupportedException>(() => Render(renderer, entry, output));
        Assert.Contains("行交互HDR", error.Message);
        Assert.Contains("保存", error.Message);
    }

    [Theory]
    [InlineData(HdrMode.LineInterleaved, 2, BayerPattern.Rggb)]
    [InlineData(HdrMode.LineInterleaved, 1, BayerPattern.None)]
    [InlineData(HdrMode.None, 1, BayerPattern.Rggb)]
    public void PhaseCompatibleOrMonochromeInput_CanStillBeExported(
        HdrMode hdr, int lineBlock, BayerPattern pattern)
    {
        var format = new RawFormat
        {
            Width = 8, Height = 8, BitDepth = 12, Bayer = pattern,
            Hdr = hdr, HdrStages = 2, HdrLineBlock = lineBlock,
        };
        using RawImage image = TestImages.FromCodes(Enumerable.Repeat((ushort)4095, 64).ToArray(), format);
        var renderer = new BatchFrameRenderer(pattern, new DevelopParameters(Gamma: 1), lut: null);
        byte[] rgb = renderer.RenderRgb24(new FileFrame(image, null, 0, 0, 1, false, "input.raw"), default);
        Assert.All(rgb, value => Assert.Equal(255, value));
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
