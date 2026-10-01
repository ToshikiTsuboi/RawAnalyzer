using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// カーソル位置(元画像の座標)の画素値の表示文。キャンバス左上のオーバーレイとステータスバーに出す。
/// </summary>
/// <remarks>
/// マウス移動・キーボードの画素カーソルで位置が変わったときだけでなく、位置を保ったまま表示中の画像・
/// フレーム・フォーマットが替わったとき(フレーム・ページ・ファイルの送り、再生、右パネルの Bayer の変更)にも、
/// 表示中の画像・フレームから読み直して作る。作り直さないと、送った後も前の画像・フレームの値と前のパターンの
/// チャネル名を出し続ける(Ctrl+C でコピーする値は表示中のフレームから読むので、画面と食い違う)。
/// </remarks>
internal static class CursorReadout
{
    /// <summary>
    /// 表示文。
    /// </summary>
    /// <param name="Status">ステータスバーの文。</param>
    /// <param name="Overlay">キャンバス左上のオーバーレイの文。</param>
    internal readonly record struct Text(string Status, string Overlay);

    /// <summary>
    /// 元画像の座標 (x, y) の画素値の表示文を作る。
    /// </summary>
    /// <param name="image">表示中の画像(HDR派生ビューならその画像)。</param>
    /// <param name="format">表示中の画像のフォーマット(ビット深度と Bayer パターンを使う)。</param>
    /// <param name="color">デコード済みのカラー画像。あれば RGB と YCbCr を出す。</param>
    /// <param name="x">元画像の X 座標。</param>
    /// <param name="y">元画像の Y 座標。</param>
    /// <param name="frame">表示中のフレーム。</param>
    /// <returns>表示文。画像の範囲外・フレームの範囲外・破棄済みで読めないときは null。</returns>
    internal static Text? Compose(RawImage image, RawFormat format, ColorImage? color, int x, int y, int frame)
    {
        ushort value;
        try
        {
            value = image.GetPixel(x, y, frame);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        // カラー画像はRGBとYCbCrを表示する
        if (color is not null && x < color.Width && y < color.Height)
        {
            color.GetPixel(x, y, out ushort r16, out ushort g16, out ushort b16);
            int shift = 16 - color.BitDepth;
            int r = r16 >> shift;
            int g = g16 >> shift;
            int b = b16 >> shift;
            (int luma, int cb, int cr) = ColorConvert.RgbToYCbCr(r, g, b, (1 << color.BitDepth) - 1);
            return new Text(
                $"({x}, {y}) RGB=({r}, {g}, {b}) YCbCr=({luma}, {cb}, {cr})",
                $"({x}, {y})  RGB: {r} {g} {b}  YCbCr: {luma} {cb} {cr}");
        }

        int code = value >> (16 - format.BitDepth);
        int maxCode = (1 << format.BitDepth) - 1;
        string channel = BayerHelper.GetLabel(BayerHelper.GetChannel(format.Bayer, x, y));
        return new Text(
            $"({x}, {y}) raw={code}",
            $"({x}, {y})  raw: {code} / {maxCode}  {channel}");
    }
}
