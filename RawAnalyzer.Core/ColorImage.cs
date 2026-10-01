namespace RawAnalyzer.Core;

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
    /// <remarks>
    /// 16bitの画像は16bitの値からYを求める。8bitの画像は、解析が value &gt;&gt; 8 で raw code に戻すため、
    /// 8bitの code(各チャネル &gt;&gt; 8)からYを求めて四捨五入した整数 code にし、WIC経路の8bitと同じ
    /// v*257 の置き方で16bitへ置く。16bitで丸めてから &gt;&gt; 8 で切り捨てると、Yが水準により
    /// −1〜+1 code ずれ、カーソルの YCbCr(<see cref="ColorConvert.RgbToYCbCr"/>)とも食い違う。
    /// </remarks>
    /// <returns>輝度のRawImage(Bayerなし)。</returns>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    public RawImage ToLuminance(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var luminance = new ushort[(long)Width * Height];
        int shift = 16 - BitDepth;
        int maxCode = (1 << BitDepth) - 1;
        Parallel.For(0, Height, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            long source = (long)y * Width * 3;
            long dest = (long)y * Width;
            for (int x = 0; x < Width; x++)
            {
                if ((x & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                ushort r = _pixels[source + x * 3];
                ushort g = _pixels[source + x * 3 + 1];
                ushort b = _pixels[source + x * 3 + 2];
                if (shift == 0)
                {
                    luminance[dest + x] = ColorConvert.Luma(r, g, b);
                    continue;
                }

                // カーソルの RgbToYCbCr の Y と同じ式・同じ丸め。上位ビットを下位へ複製して置く(8bitは v*257)
                int rCode = r >> shift;
                int gCode = g >> shift;
                int bCode = b >> shift;
                int luma = (int)Math.Clamp(Math.Round(0.299 * rCode + 0.587 * gCode + 0.114 * bCode), 0, maxCode);
                luminance[dest + x] = (ushort)((luma << shift) | (luma >> (BitDepth - shift)));
            }
        });

        cancellationToken.ThrowIfCancellationRequested();
        var format = new RawFormat { Width = Width, Height = Height, BitDepth = BitDepth };
        return RawImage.FromPixels(format, luminance);
    }
}
