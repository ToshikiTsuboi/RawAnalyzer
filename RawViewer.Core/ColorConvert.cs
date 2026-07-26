namespace RawViewer.Core;

/// <summary>
/// 色空間変換(BT.601フルレンジのRGB↔YCbCr)。
/// センサ評価では8/10/12bitのcode値をそのまま扱うため、値域を指定して変換する。
/// </summary>
public static class ColorConvert
{
    /// <summary>
    /// RGB(16bitフルスケール)から輝度Y(16bitフルスケール)を求める。
    /// </summary>
    /// <param name="r">R値。</param>
    /// <param name="g">G値。</param>
    /// <param name="b">B値。</param>
    /// <returns>輝度値。</returns>
    public static ushort Luma(ushort r, ushort g, ushort b)
    {
        return (ushort)Math.Clamp(
            (int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b), 0, 65535);
    }

    /// <summary>
    /// RGB code値をYCbCr code値へ変換する(BT.601フルレンジ)。
    /// CbCrは (maxCode+1)/2 を中心とするオフセット表現で返す。
    /// </summary>
    /// <param name="r">R code値。</param>
    /// <param name="g">G code値。</param>
    /// <param name="b">B code値。</param>
    /// <param name="maxCode">値域の最大code値(8bitなら255)。</param>
    /// <returns>Y/Cb/Cr code値。</returns>
    public static (int Y, int Cb, int Cr) RgbToYCbCr(int r, int g, int b, int maxCode)
    {
        double half = (maxCode + 1) / 2.0;
        double y = 0.299 * r + 0.587 * g + 0.114 * b;
        double cb = -0.168736 * r - 0.331264 * g + 0.5 * b + half;
        double cr = 0.5 * r - 0.418688 * g - 0.081312 * b + half;
        return (
            (int)Math.Clamp(Math.Round(y), 0, maxCode),
            (int)Math.Clamp(Math.Round(cb), 0, maxCode),
            (int)Math.Clamp(Math.Round(cr), 0, maxCode));
    }
}
