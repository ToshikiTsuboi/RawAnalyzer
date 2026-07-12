using System.Text.Json.Serialization;

namespace RawViewer.Core;

/// <summary>
/// Rawファイルの解釈方法を定義するフォーマット記述子。
/// System.Text.Json でシリアライズ可能(列挙型は文字列として保存される)。
/// </summary>
public sealed record RawFormat
{
    /// <summary>サポートされるビット深度の一覧。</summary>
    public static readonly IReadOnlyList<int> SupportedBitDepths = new[] { 8, 10, 12, 14, 16 };

    /// <summary>画像の幅(画素数)。</summary>
    public required int Width { get; init; }

    /// <summary>画像の高さ(画素数)。</summary>
    public required int Height { get; init; }

    /// <summary>ビット深度。8/10/12/14/16 のいずれか。</summary>
    public int BitDepth { get; init; } = 16;

    /// <summary>16bitコンテナ内での詰め方向(LSB/MSB)。</summary>
    public BitPacking Packing { get; init; } = BitPacking.Lsb;

    /// <summary>マルチバイト画素値のバイト順。</summary>
    public Endianness Endianness { get; init; } = Endianness.Little;

    /// <summary>ファイル先頭の読み飛ばすヘッダバイト数。</summary>
    public long HeaderOffset { get; init; }

    /// <summary>ファイルに連結されているフレーム数。</summary>
    public int FrameCount { get; init; } = 1;

    /// <summary>Bayerカラーフィルタ配列パターン。</summary>
    public BayerPattern Bayer { get; init; } = BayerPattern.None;

    /// <summary>HDRフレームの格納方式。</summary>
    public HdrMode Hdr { get; init; } = HdrMode.None;

    /// <summary>1画素あたりのファイル上のバイト数(8bit=1、それ以外=2)。</summary>
    [JsonIgnore]
    public int BytesPerPixel => BitDepth <= 8 ? 1 : 2;

    /// <summary>1フレームあたりのファイル上のバイト数。</summary>
    [JsonIgnore]
    public long FrameSizeInBytes => (long)Width * Height * BytesPerPixel;

    /// <summary>全フレーム合計の画素数。</summary>
    [JsonIgnore]
    public long TotalPixels => (long)Width * Height * FrameCount;

    /// <summary>
    /// フォーマット値の妥当性を検証する。
    /// </summary>
    /// <exception cref="ArgumentException">いずれかの値が不正な場合。</exception>
    public void Validate()
    {
        if (Width <= 0)
        {
            throw new ArgumentException($"幅は正の値である必要があります: {Width}");
        }

        if (Height <= 0)
        {
            throw new ArgumentException($"高さは正の値である必要があります: {Height}");
        }

        if (!SupportedBitDepths.Contains(BitDepth))
        {
            throw new ArgumentException($"サポートされないビット深度です: {BitDepth}");
        }

        if (HeaderOffset < 0)
        {
            throw new ArgumentException($"ヘッダオフセットは非負である必要があります: {HeaderOffset}");
        }

        if (FrameCount <= 0)
        {
            throw new ArgumentException($"フレーム数は正の値である必要があります: {FrameCount}");
        }
    }
}
