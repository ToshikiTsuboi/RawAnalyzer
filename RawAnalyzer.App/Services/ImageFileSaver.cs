using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.App.Rendering;
using RawAnalyzer.App.Views;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 保存ダイアログで選んだ TIFF/PNG/JPEG へ、表示中の1フレームを書き出す(raw・float raw は対象外)。
/// </summary>
/// <remarks>
/// 16bit(TIFF/PNG)は無処理で、読み込んだ RGB 画像(JPEG/PNG/カラーTIFF)は輝度化せず RGB48、
/// それ以外は Gray16 で書く。8bit(PNG/JPEG)は選んだ処理を焼き込む。
/// WIC のエンコーダは全画素を1つのバッファで受け取るため、1億画素を超える TIFF は自前のライタ
/// (<see cref="TiffWriter"/>)で行単位に書く(PNG/JPEG は保存ダイアログが1億画素を超える画像では選ばせない)。
/// そこでも RGB 画像は RGB48 のまま書き、WIC で書く場合と同じ画素値にする(画像の大きさで色成分を失わない)。
/// HDR分割ビューから表示LUTを焼き込むときは、画面と同じく各段をその段の表示調整で焼き込む
/// (<see cref="HdrSplitAdjustments"/>)。
/// </remarks>
internal static class ImageFileSaver
{
    /// <summary>この画素数を超える TIFF は WIC を使わず、自前のライタで行単位に書く。</summary>
    internal const long StreamingPixelThreshold = RawLoader.DefaultInMemoryPixelThreshold;

    /// <summary>表示中の1フレームを保存する。</summary>
    /// <param name="image">保存する画像(読み込んだ RGB 画像では輝度)。</param>
    /// <param name="frame">フレーム番号。</param>
    /// <param name="path">出力先。</param>
    /// <param name="format">出力形式(TIFF/PNG/JPEG)。</param>
    /// <param name="mode">8bit 出力の表示モード(カラー現像するか)。</param>
    /// <param name="pattern">8bit 出力のカラー現像に使う Bayer パターン。</param>
    /// <param name="lut">8bit 出力に焼き込む表示LUT。</param>
    /// <param name="devLuts">8bit 出力のカラー現像に使う現像LUT。</param>
    /// <param name="trueColor">読み込んだ RGB 画像。なければ null(派生ビューの表示中も null)。</param>
    /// <param name="progress">進捗(0〜1)。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <param name="streamingPixelThreshold">TIFF を自前のライタで書く画素数の境目(テスト用に下げられる)。</param>
    /// <param name="split">
    /// HDR分割ビューから表示LUTを焼き込むときの段ごとの表示調整。8bit 出力では <paramref name="lut"/>
    /// (スライダーの値=最後に調整した段)に代えて段ごとの表示LUTを、カラー現像では <paramref name="devLuts"/> の
    /// 表示調整の値に代えて段の値を使う。分割ビューでない、または表示LUTを焼き込まないときは null。
    /// </param>
    internal static void Save(
        RawImage image, int frame, string path, SaveFormat format, ViewportDisplayMode mode,
        BayerPattern pattern, DisplayLut lut, DevelopLuts devLuts, ColorImage? trueColor,
        IProgress<double> progress, CancellationToken ct,
        long streamingPixelThreshold = StreamingPixelThreshold, HdrSplitAdjustments? split = null)
    {
        if (format == SaveFormat.Tiff16 && (long)image.Width * image.Height > streamingPixelThreshold)
        {
            // 巨大画像は自前ライタで行単位ストリーミング。読み込んだRGB画像は輝度(image)ではなく
            // RGB48で書く(WIC経路と同じ。派生ビューの表示中は trueColor が null なので輝度のまま)
            if (trueColor is not null)
            {
                TiffWriter.SaveRgb48(trueColor, path, progress, ct);
            }
            else
            {
                TiffWriter.SaveGray16(image, frame, path, progress, ct);
            }

            return;
        }

        SaveWithWic(image, frame, path, format, mode, pattern, lut, devLuts, trueColor, split, progress, ct);
    }

    private static void SaveWithWic(
        RawImage image, int frame, string path, SaveFormat format, ViewportDisplayMode mode,
        BayerPattern pattern, DisplayLut lut, DevelopLuts devLuts, ColorImage? trueColor,
        HdrSplitAdjustments? split, IProgress<double> progress, CancellationToken ct)
    {
        int width = image.Width;
        int height = image.Height;
        BitmapSource source;
        switch (format)
        {
            case SaveFormat.Tiff16:
            case SaveFormat.Png16:
                {
                    // 読み込んだRGB画像は輝度化せずチャネルを保って書き出す
                    if (trueColor is not null)
                    {
                        ushort[] rgb48 = ImageExport.RenderColorRgb48(trueColor, ct);
                        progress.Report(0.7);
                        source = BitmapSource.Create(
                            trueColor.Width, trueColor.Height, 96, 96, PixelFormats.Rgb48, null,
                            rgb48, trueColor.Width * 6);
                        break;
                    }

                    {
                        var pixels = new ushort[(long)width * height];
                        for (int y = 0; y < height; y++)
                        {
                            ct.ThrowIfCancellationRequested();
                            image.CopyRegion(frame, 0, y, width, 1, pixels.AsSpan(y * width, width));
                            if ((y & 511) == 0)
                            {
                                progress.Report(0.5 * y / height);
                            }
                        }

                        source = BitmapSource.Create(
                            width, height, 96, 96, PixelFormats.Gray16, null, pixels, width * 2);
                    }

                    break;
                }

            default:
                {
                    // 8bit系は選択された処理を焼き込む
                    if (trueColor is not null)
                    {
                        // 読み込んだRGB画像はデモザイクせずLUTだけ適用する
                        byte[] rgb = ImageExport.RenderColorRgb24(trueColor, lut, ct);
                        progress.Report(0.7);
                        source = BitmapSource.Create(
                            trueColor.Width, trueColor.Height, 96, 96, PixelFormats.Rgb24, null,
                            rgb, trueColor.Width * 3);
                        break;
                    }

                    // HDR分割ビューからは、画面と同じく各段をその段の表示調整で焼き込む
                    // (段ごとのLUTは保存のこのスレッドで作り、UIスレッドを塞がない)
                    bool color = mode == ViewportDisplayMode.ColorDevelop
                        && pattern != BayerPattern.None;
                    if (color)
                    {
                        var developProgress = new Progress<double>(p => progress.Report(p * 0.7));
                        byte[] rgb = split is null
                            ? ImageExport.DevelopRgb24(image, frame, pattern, devLuts, developProgress, ct)
                            : ImageExport.DevelopRgb24(
                                image, frame, pattern, split.CreateDevelopLuts(devLuts.Parameters),
                                split.SegmentWidth, developProgress, ct);
                        source = BitmapSource.Create(
                            width, height, 96, 96, PixelFormats.Rgb24, null, rgb, width * 3);
                    }
                    else
                    {
                        byte[] gray = split is null
                            ? ImageExport.RenderGray8(image, frame, lut, ct)
                            : ImageExport.RenderGray8(
                                image, frame, split.CreateDisplayLuts(), split.SegmentWidth, ct);
                        progress.Report(0.7);
                        source = BitmapSource.Create(
                            width, height, 96, 96, PixelFormats.Gray8, null, gray, width);
                    }

                    break;
                }
        }

        ct.ThrowIfCancellationRequested();
        BitmapEncoder encoder = format switch
        {
            SaveFormat.Tiff16 => new TiffBitmapEncoder { Compression = TiffCompressOption.None },
            SaveFormat.Jpeg8 => new JpegBitmapEncoder { QualityLevel = 95 },
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(source));

        // 一時ファイルへ書き切ってから置換する。直接書くと、失敗・キャンセル時に
        // 上書き対象だった既存ファイルを失う
        AtomicFileWriter.Write(path, encoder.Save);
        progress.Report(1.0);
    }
}
