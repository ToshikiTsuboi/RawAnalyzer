using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 一括書き出し(PNG/JPEG と動画の MJPEG AVI・MP4)で、1枚ぶん(ファイル・RAWのフレーム・TIFFのページ)を焼き込む。
/// Bayer の指定があればカラー現像し、読み込んだ RGB 画像は表示LUTだけ、モノクロは表示LUTを適用する。
/// </summary>
/// <remarks>
/// 表示LUTと現像パラメータ(WB・マトリクス・黒/白点など)は書き出しの開始時の値で固定する。
/// ただし現像LUTの白飛びの判定(<see cref="DevelopParameters.SourceBitDepth"/>)は画素のビット深度で決まる
/// (Nbit の白レベルの画素は code&lt;&lt;(16−N) で、白点と code で比べる)。TIFFのページや連番のファイルは
/// ビット深度が異なることがあるため、現像LUTは書き出す画像のビット深度ごとに1回だけ作って使い回す
/// (表示中の画像のLUTを使い回すと、飽和した12bitのページを16bitの白点未満とみなし、WBと行列で着色する)。
/// 表示LUTは16bitの内部値の領域で作るので、ビット深度によらず同じものを使う。
/// 1つの書き出しの中で順に呼ぶ(スレッドセーフではない)。
/// </remarks>
internal sealed class BatchFrameRenderer
{
    private readonly BayerPattern _pattern;
    private readonly DisplayLut _lut;
    private readonly DevelopParameters _developParameters;
    private readonly Dictionary<int, DevelopLuts> _developLuts = new();

    // MP4 のフレームごとに確保すると LOH を圧迫するので使い回す
    private byte[]? _gray;
    private byte[]? _rgb;

    /// <summary>書き出しの開始時の設定で作る。</summary>
    /// <param name="pattern">Bayer パターン。None ならカラー現像しない。</param>
    /// <param name="developParameters">
    /// 現像パラメータ。<see cref="DevelopParameters.SourceBitDepth"/> は使わず、書き出す画像のビット深度に置き換える。
    /// </param>
    /// <param name="lut">表示LUT。</param>
    internal BatchFrameRenderer(BayerPattern pattern, DevelopParameters developParameters, DisplayLut lut)
    {
        _pattern = pattern;
        _lut = lut;
        _developParameters = developParameters;
    }

    private bool Develops => _pattern != BayerPattern.None;

    /// <summary>MP4 用に1枚ぶんの RGB24 を作る。</summary>
    /// <remarks>返す配列は使い回すので、次の呼び出しで上書きされる。</remarks>
    /// <param name="entry">書き出す1枚。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <returns>width×height×3 の RGB24(長さはフレームぴったり)。</returns>
    internal byte[] RenderRgb24(FileFrame entry, CancellationToken ct)
    {
        if (entry.Color is { } trueColor)
        {
            EnsureBuffers((long)trueColor.Width * trueColor.Height);
            ImageExport.RenderColorRgb24(trueColor, _lut, _rgb!, ct);
            return _rgb!;
        }

        RawImage image = entry.Image;
        long pixels = (long)image.Width * image.Height;
        EnsureBuffers(pixels);
        if (Develops)
        {
            return ImageExport.DevelopRgb24(
                image, entry.Frame, _pattern, DevelopLutsFor(image), null, ct, _rgb);
        }

        byte[] gray = _gray!;
        byte[] rgb = _rgb!;
        ImageExport.RenderGray8(image, entry.Frame, _lut, gray, ct);
        for (long i = 0; i < pixels; i++)
        {
            byte v = gray[i];
            rgb[i * 3] = v;
            rgb[(i * 3) + 1] = v;
            rgb[(i * 3) + 2] = v;
        }

        return rgb;
    }

    /// <summary>MJPEG AVI 用に1枚ぶんを JPEG へ符号化する。</summary>
    /// <param name="entry">書き出す1枚。</param>
    /// <param name="quality">JPEG 品質(1〜100)。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <returns>JPEG のバイト列。</returns>
    internal byte[] EncodeJpeg(FileFrame entry, int quality, CancellationToken ct)
    {
        // MJPEG はグレースケールJPEG非対応のプレーヤがあるため RGB にする
        BitmapSource source = Bake(entry, forceRgb: true, ct);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>1枚ぶんを PNG/JPEG として保存する。</summary>
    /// <param name="entry">書き出す1枚。</param>
    /// <param name="path">出力先。</param>
    /// <param name="jpeg">JPEG なら true、PNG なら false。</param>
    /// <param name="ct">キャンセルトークン。</param>
    internal void Save(FileFrame entry, string path, bool jpeg, CancellationToken ct)
    {
        BitmapSource source = Bake(entry, forceRgb: false, ct);
        BitmapEncoder encoder = jpeg
            ? new JpegBitmapEncoder { QualityLevel = 95 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        // 途中で失敗しても壊れたファイルを出力フォルダに残さない
        // (同名の前回出力も、書き切るまでは差し替えない)
        AtomicFileWriter.Write(path, encoder.Save);
    }

    /// <summary>画像のビット深度に合わせた現像LUT。65536×3 の計算なのでビット深度ごとに1回だけ作る。</summary>
    private DevelopLuts DevelopLutsFor(RawImage image)
    {
        int bitDepth = image.Format.BitDepth;
        if (!_developLuts.TryGetValue(bitDepth, out DevelopLuts? luts))
        {
            luts = DevelopLuts.Create(_developParameters with { SourceBitDepth = bitDepth });
            _developLuts.Add(bitDepth, luts);
        }

        return luts;
    }

    private BitmapSource Bake(FileFrame entry, bool forceRgb, CancellationToken ct)
    {
        if (entry.Color is { } trueColor)
        {
            // デコード済みのカラー画像は現像せず、表示LUTだけを焼き込む
            byte[] colorRgb = ImageExport.RenderColorRgb24(trueColor, _lut, ct);
            return BitmapSource.Create(
                trueColor.Width, trueColor.Height, 96, 96, PixelFormats.Rgb24, null, colorRgb,
                trueColor.Width * 3);
        }

        RawImage image = entry.Image;
        int width = image.Width;
        int height = image.Height;
        if (Develops)
        {
            byte[] rgb = ImageExport.DevelopRgb24(
                image, entry.Frame, _pattern, DevelopLutsFor(image), null, ct);
            return BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Rgb24, null, rgb, width * 3);
        }

        byte[] gray = ImageExport.RenderGray8(image, entry.Frame, _lut, ct);
        if (!forceRgb)
        {
            return BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Gray8, null, gray, width);
        }

        var rgbGray = new byte[(long)width * height * 3];
        Parallel.For(0, height, y =>
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                byte v = gray[rowOffset + x];
                long o = ((long)rowOffset + x) * 3;
                rgbGray[o] = v;
                rgbGray[o + 1] = v;
                rgbGray[o + 2] = v;
            }
        });
        return BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Rgb24, null, rgbGray, width * 3);
    }

    /// <summary>
    /// 必要な大きさのバッファを用意する(同じ大きさなら使い回す)。
    /// </summary>
    /// <remarks>
    /// 動画ライタは「長さがフレームぴったり」であることを要求するので、
    /// 余りのある使い回しはせず、長さが違えば作り直す。
    /// </remarks>
    /// <param name="pixels">1フレームの画素数。</param>
    private void EnsureBuffers(long pixels)
    {
        if (_gray is null || _gray.LongLength != pixels)
        {
            _gray = new byte[pixels];
        }

        if (_rgb is null || _rgb.LongLength != pixels * 3)
        {
            _rgb = new byte[pixels * 3];
        }
    }
}
