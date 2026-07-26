namespace RawViewer.Core;

/// <summary>
/// RGBカラー画像(16bitフルスケールへ正規化、インターリーブ配置)。
/// JPEG/PNG/カラーTIFFなどデコード済み画像の保持に使う。
/// </summary>
public sealed class ColorImage
{
    private readonly ushort[] _pixels;

    private ColorImage(int width, int height, int bitDepth, ushort[] pixels)
    {
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        _pixels = pixels;
    }

    /// <summary>幅(画素数)。</summary>
    public int Width { get; }

    /// <summary>高さ(画素数)。</summary>
    public int Height { get; }

    /// <summary>元データのビット深度(8または16)。raw code表示に使う。</summary>
    public int BitDepth { get; }

    /// <summary>
    /// インターリーブRGB配列から画像を生成する。
    /// </summary>
    /// <param name="width">幅。</param>
    /// <param name="height">高さ。</param>
    /// <param name="bitDepth">元データのビット深度(8または16)。</param>
    /// <param name="interleavedRgb">width × height × 3 のRGB配列(16bitフルスケール)。</param>
    /// <returns>生成された画像。</returns>
    /// <exception cref="ArgumentException">サイズが不正、または配列長が不足する場合。</exception>
    public static ColorImage FromInterleaved(
        int width, int height, int bitDepth, ushort[] interleavedRgb)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("画像サイズが不正です。", nameof(width));
        }

        if (bitDepth is not (8 or 16))
        {
            throw new ArgumentException($"ビット深度は8または16である必要があります: {bitDepth}",
                nameof(bitDepth));
        }

        if (interleavedRgb.Length < (long)width * height * 3)
        {
            throw new ArgumentException("RGB配列が不足しています。", nameof(interleavedRgb));
        }

        return new ColorImage(width, height, bitDepth, interleavedRgb);
    }

    /// <summary>
    /// 指定座標のRGB値(16bitフルスケール)を取得する。
    /// </summary>
    /// <param name="x">X座標。</param>
    /// <param name="y">Y座標。</param>
    /// <param name="r">R値。</param>
    /// <param name="g">G値。</param>
    /// <param name="b">B値。</param>
    /// <exception cref="ArgumentOutOfRangeException">座標が範囲外の場合。</exception>
    public void GetPixel(int x, int y, out ushort r, out ushort g, out ushort b)
    {
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        long index = ((long)y * Width + x) * 3;
        r = _pixels[index];
        g = _pixels[index + 1];
        b = _pixels[index + 2];
    }

    /// <summary>
    /// 指定行の一部をインターリーブRGBとしてコピーする。
    /// </summary>
    /// <param name="y">行番号。</param>
    /// <param name="startX">開始X座標。</param>
    /// <param name="count">画素数。</param>
    /// <param name="destination">count × 3 以上の出力バッファ。</param>
    public void CopyRow(int y, int startX, int count, Span<ushort> destination)
    {
        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        if (startX < 0 || count <= 0 || startX + count > Width)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (destination.Length < count * 3)
        {
            throw new ArgumentException("出力バッファが不足しています。", nameof(destination));
        }

        int offset = (int)(((long)y * Width + startX) * 3);
        _pixels.AsSpan(offset, count * 3).CopyTo(destination);
    }

    /// <summary>
    /// 輝度(BT.601のY)画像を生成する。ヒストグラム等の解析に使う。
    /// </summary>
    /// <returns>輝度のRawImage(Bayerなし)。</returns>
    public RawImage ToLuminance()
    {
        var luminance = new ushort[(long)Width * Height];
        Parallel.For(0, Height, y =>
        {
            long source = (long)y * Width * 3;
            long dest = (long)y * Width;
            for (int x = 0; x < Width; x++)
            {
                luminance[dest + x] = ColorConvert.Luma(
                    _pixels[source + x * 3],
                    _pixels[source + x * 3 + 1],
                    _pixels[source + x * 3 + 2]);
            }
        });

        var format = new RawFormat { Width = Width, Height = Height, BitDepth = BitDepth };
        return RawImage.FromPixels(format, luminance);
    }
}
