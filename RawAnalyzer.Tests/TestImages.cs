using RawAnalyzer.Core;

namespace RawAnalyzer.Tests;

/// <summary>
/// ファイルを経由せずに <see cref="RawImage"/> を組み立てるテスト用ヘルパ。
/// ローダを試験しないテストで、一時ファイルへの書き出しと <see cref="RawLoader"/> による
/// 読み戻しの代わりに使う(ディスク I/O なし)。
/// </summary>
internal static class TestImages
{
    /// <summary>
    /// Nビットの生コード値から画像を作る。ローダと同じく <c>code &lt;&lt; (16 - N)</c> で
    /// 16bitフルスケールへ正規化するので、同じ値をファイル経由で読み込んだ場合と画素値が一致する。
    /// </summary>
    /// <param name="codes">生コード値(幅×高さ個以上)。</param>
    /// <param name="width">幅。</param>
    /// <param name="height">高さ。</param>
    /// <param name="bitDepth">ビット深度(8/10/12/14/16)。</param>
    /// <param name="bayer">Bayerパターン。</param>
    /// <returns>ヒープ上に画素を持つ画像。</returns>
    public static RawImage FromCodes(
        ushort[] codes, int width, int height, int bitDepth = 16,
        BayerPattern bayer = BayerPattern.None)
    {
        return FromCodes(codes, new RawFormat
        {
            Width = width, Height = height, BitDepth = bitDepth, Bayer = bayer,
        });
    }

    /// <summary>
    /// フォーマットを指定して生コード値から画像を作る(複数フレーム・HDR指定向け)。
    /// 複数フレームはフレーム0の全画素、フレーム1の全画素…の順に連結して渡す。
    /// 詰め方向・エンディアン・ヘッダオフセットはファイル上の表現にだけ関わるため
    /// 画素値には影響しない(フォーマット記述子にはそのまま保持される)。
    /// </summary>
    /// <param name="codes">生コード値(幅×高さ×フレーム数個以上)。</param>
    /// <param name="format">画像のフォーマット。</param>
    /// <returns>ヒープ上に画素を持つ画像。</returns>
    /// <exception cref="ArgumentException">フォーマットが不正、または配列長が不足する場合。</exception>
    public static RawImage FromCodes(ushort[] codes, RawFormat format)
    {
        // ローダと同じ正規化(下詰めNbit → <<(16-N)、8bit → <<8)。
        // 呼び出し側が配列を後から書き換えても画像が変わらないよう複製する
        int shift = 16 - format.BitDepth;
        var pixels = new ushort[codes.Length];
        for (int i = 0; i < codes.Length; i++)
        {
            pixels[i] = (ushort)(codes[i] << shift);
        }

        return RawImage.FromPixels(format, pixels);
    }
}
