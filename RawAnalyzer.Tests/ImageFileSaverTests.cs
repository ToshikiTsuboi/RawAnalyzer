using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Services;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>
/// 保存(TIFF/PNG/JPEG)の書き出し経路の検証。
/// </summary>
public class ImageFileSaverTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(8)]
    public void Tiff16_ColorImage_StreamingWriterReloadsSameRgbAsWic(int bitDepth)
    {
        // Codexレビュー(2026-09-29)#7。1億画素を超える画像の TIFF は WIC を使わず自前ライタで行単位に書くが、
        // その経路は輝度画像を Gray16 で書いており、読み込んだ RGB 画像(JPEG/PNG/カラーTIFF)の色成分が
        // 画像の大きさだけで失われていた。巨大画像を作らずに境目を下げて自前ライタの経路を通し、1億画素以下の
        // WIC 経路と同じ RGB48(8bit 由来の画像も読込時の v×257 の16bit値のまま)で読み戻せることを確かめる
        const int width = 7;
        const int height = 5;
        ColorImage color = MakeColorImage(width, height, bitDepth);
        using RawImage luminance = color.ToLuminance();

        string wicPath = TempPath();
        string streamingPath = TempPath();
        try
        {
            SaveTiff16(luminance, color, wicPath, ImageFileSaver.StreamingPixelThreshold);
            SaveTiff16(luminance, color, streamingPath, streamingPixelThreshold: 0);

            DecodedImage wic = ImageFileLoader.Load(wicPath);
            DecodedImage streaming = ImageFileLoader.Load(streamingPath);
            using RawImage wicLuminance = wic.Luminance;
            using RawImage streamingLuminance = streaming.Luminance;
            Assert.NotNull(wic.Color);
            Assert.NotNull(streaming.Color);
            Assert.Equal(16, wic.Color.BitDepth);
            Assert.Equal(16, streaming.Color.BitDepth);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    color.GetPixel(x, y, out ushort r, out ushort g, out ushort b);
                    wic.Color.GetPixel(x, y, out ushort wicR, out ushort wicG, out ushort wicB);
                    streaming.Color.GetPixel(x, y, out ushort streamR, out ushort streamG, out ushort streamB);
                    Assert.Equal((r, g, b), (wicR, wicG, wicB));
                    Assert.Equal((r, g, b), (streamR, streamG, streamB));
                    Assert.Equal(wicLuminance.GetPixel(x, y), streamingLuminance.GetPixel(x, y));
                }
            }
        }
        finally
        {
            File.Delete(wicPath);
            File.Delete(streamingPath);
        }
    }

    [Fact]
    public void Png8_HdrSplitView_BurnsEachStageWithItsOwnDisplayAdjustment_SameAsScreen()
    {
        // 仕様変更(2026-09-29)。HDR分割ビューは表示調整の対象(全体/長秒/中秒/短秒)ごとに変えた表示パラメータから
        // 段ごとのLUTを作って描くが、焼き込み保存は最後にスライダーで設定した1本のLUTを全段に掛けており、画面と
        // 保存結果が食い違っていた。3段を段ごとに違う表示調整で保存し、読み戻した画素が等倍の画面の描画と
        // 段の境目の列まで一致することを確かめる
        const int stageWidth = 3;
        const int height = 2;
        DisplayParameters[] stages =
        [
            new(),                                                            // 長秒
            new(BlackPoint: 1024, WhitePoint: 32767, Gain: 2.0, Gamma: 2.2),  // 中秒
            new(BlackPoint: 256, WhitePoint: 8191, Gain: 4.0, Contrast: 1.5), // 短秒(最後に調整した段)
        ];
        int width = stageWidth * stages.Length;
        var codes = new ushort[width * height];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = (ushort)(((i * 457) + 37) % 4096);
        }

        using RawImage composite = TestImages.FromCodes(codes, width, height, bitDepth: 12);

        // スライダーの値(保存に渡す1本の表示LUT)は最後に調整した段のもの
        DisplayLut sliderLut = DisplayLut.Create(stages[^1]);
        string path = TempPath(".png");
        try
        {
            ImageFileSaver.Save(
                composite, 0, path, SaveFormat.Png8, ViewportDisplayMode.Raw, BayerPattern.None,
                sliderLut, DevelopLuts.Create(new DevelopParameters()), null, new Progress<double>(),
                CancellationToken.None,
                split: HdrSplitAdjustments.ForSave(stages, stageWidth, applyDisplayLut: true));
            byte[] saved = DecodePixels(path, PixelFormats.Gray8);

            // 分割ビューの画面: 段ごとの表示LUT(MainWindow の ApplySplitLuts と同じ作り方)で等倍に描く
            var screen = new byte[width * height * 4];
            ViewportRenderer.Render(
                new RenderRequest
                {
                    Source = new RawImageRenderSource(composite, 0),
                    Lut = sliderLut,
                    SegmentLuts = Array.ConvertAll(stages, DisplayLut.Create),
                    SegmentWidth = stageWidth,
                },
                zoom: 1.0, originX: 0, originY: 0, width, height, screen, CancellationToken.None);
            byte[] screenGray = Enumerable.Range(0, width * height).Select(i => screen[i * 4]).ToArray();

            Assert.Equal(screenGray, saved);
            Assert.NotEqual(ImageExport.RenderGray8(composite, 0, sliderLut), saved);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Png8_HdrSplitView_Demosaic_DevelopsEachStageAlone(int stageWidth, bool applyDisplayLut)
    {
        // 分割ビューの画面はRaw表示だけ(カラー現像などを選ぶと分割ビューを抜ける)だが、保存ではデモザイクも選べ、
        // BayerのHDRでは既定で選ばれる。そのときも各段の現像に段の表示調整を当て(WB・マトリクスは共通)、
        // 隣の段の画素を補間に混ぜない。混ぜると継ぎ目の列に露光比ぶん明るさの違う隣の段の値が入り、短秒側に明るい
        // 筋が出る(短秒に掛けた大きなゲインでさらに増幅される)。表示LUTを焼き込まないときも段ごとに現像し、表示調整は
        // 当てない。各段を単独で現像して左から並べた結果と一致することを確かめる
        // (段の幅が奇数だと並置画像の座標では後ろの段のBayer位相がずれるが、各段は同じ位相で撮られた別の画像)
        const int height = 4;
        var stageFormat = new RawFormat
        {
            Width = stageWidth, Height = height, BitDepth = 12, Bayer = BayerPattern.Rggb,
        };
        var longCodes = new ushort[stageWidth * height];
        var shortCodes = new ushort[stageWidth * height];
        for (int i = 0; i < longCodes.Length; i++)
        {
            longCodes[i] = (ushort)(1500 + ((i * 37) % 500));
            shortCodes[i] = (ushort)(longCodes[i] / 16); // 露光比16
        }

        // 並置画像(EnterHdrSplitAsync と同じく、各行に長秒 → 短秒の順で並べる)
        int width = stageWidth * 2;
        var compositeCodes = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(longCodes, y * stageWidth, compositeCodes, y * width, stageWidth);
            Array.Copy(shortCodes, y * stageWidth, compositeCodes, (y * width) + stageWidth, stageWidth);
        }

        using RawImage composite = TestImages.FromCodes(compositeCodes, stageFormat with { Width = width });
        DisplayParameters[] stages =
        [
            new(BlackPoint: 64 << 4),                                        // 長秒
            new(BlackPoint: 64 << 4, Gain: 16.0, Gamma: 2.2, Contrast: 1.2), // 短秒(最後に調整した段)
        ];
        var common = new DevelopParameters(
            GainR: 1.8, GainB: 1.4,
            Matrix: new ColorMatrix(1.2, -0.1, -0.1, -0.1, 1.2, -0.1, -0.1, -0.1, 1.2),
            SourceBitDepth: 12);

        // 保存に渡すLUTと段ごとの表示調整は MainWindow の ExecuteSave と同じく作る(スライダーの値は最後に調整した
        // 段のもの。表示LUTを焼き込まないなら表示調整は恒等)
        DisplayParameters slider = applyDisplayLut ? stages[^1] : new DisplayParameters();
        string path = TempPath(".png");
        try
        {
            ImageFileSaver.Save(
                composite, 0, path, SaveFormat.Png8, ViewportDisplayMode.ColorDevelop, BayerPattern.Rggb,
                DisplayLut.Create(slider), DevelopLuts.Create(WithDisplay(common, slider)), null,
                new Progress<double>(), CancellationToken.None,
                split: HdrSplitAdjustments.ForSave(stages, stageWidth, applyDisplayLut));
            byte[] saved = DecodePixels(path, PixelFormats.Rgb24);

            var expected = new byte[width * height * 3];
            for (int stage = 0; stage < stages.Length; stage++)
            {
                using RawImage alone = TestImages.FromCodes(stage == 0 ? longCodes : shortCodes, stageFormat);
                DisplayParameters display = applyDisplayLut ? stages[stage] : new DisplayParameters();
                byte[] developed = ImageExport.DevelopRgb24(
                    alone, 0, BayerPattern.Rggb, DevelopLuts.Create(WithDisplay(common, display)));
                for (int y = 0; y < height; y++)
                {
                    Array.Copy(developed, y * stageWidth * 3, expected,
                        ((y * width) + (stage * stageWidth)) * 3, stageWidth * 3);
                }
            }

            Assert.Equal(expected, saved);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>画面の表示調整から現像パラメータを作るとき(MainWindow の CurrentDevelopParameters)と同じ対応。</summary>
    private static DevelopParameters WithDisplay(DevelopParameters common, DisplayParameters display) =>
        common with
        {
            BlackLevel = display.BlackPoint,
            WhitePoint = display.WhitePoint,
            Gain = display.Gain,
            Gamma = display.Gamma,
            Contrast = display.Contrast,
        };

    /// <summary>保存した画像を読み戻し、指定の画素形式の画素列にする。</summary>
    private static byte[] DecodePixels(string path, PixelFormat format)
    {
        using FileStream stream = File.OpenRead(path);
        BitmapSource frame = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (frame.Format != format)
        {
            frame = new FormatConvertedBitmap(frame, format, null, 0);
        }

        int stride = ((frame.PixelWidth * format.BitsPerPixel) + 7) / 8;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void SaveTiff16(
        RawImage luminance, ColorImage color, string path, long streamingPixelThreshold)
    {
        ImageFileSaver.Save(
            luminance, 0, path, SaveFormat.Tiff16, ViewportDisplayMode.Raw, BayerPattern.None,
            DisplayLut.Create(new DisplayParameters()), DevelopLuts.Create(new DevelopParameters()),
            color, new Progress<double>(), CancellationToken.None, streamingPixelThreshold);
    }

    /// <summary>
    /// チャネルごとに値の異なるカラー画像。8bit は WIC の読込と同じく v×257 の16bit値で持つ。
    /// </summary>
    private static ColorImage MakeColorImage(int width, int height, int bitDepth)
    {
        var rgb = new ushort[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            if (bitDepth == 8)
            {
                rgb[i * 3] = (ushort)(((i * 37) & 0xFF) * 257);
                rgb[(i * 3) + 1] = (ushort)((255 - ((i * 11) & 0xFF)) * 257);
                rgb[(i * 3) + 2] = (ushort)((((i * 101) + 7) & 0xFF) * 257);
            }
            else
            {
                rgb[i * 3] = (ushort)(1000 + (i * 997));
                rgb[(i * 3) + 1] = (ushort)(60000 - (i * 1409));
                rgb[(i * 3) + 2] = (ushort)((i * 7919) & 0xFFFF);
            }
        }

        return ColorImage.FromInterleaved(width, height, bitDepth, rgb);
    }

    private static string TempPath(string extension = ".tif")
    {
        string directory = Path.Combine(Path.GetTempPath(), "RawAnalyzerTests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
    }
}
