using System.Text.Json.Serialization;

namespace RawAnalyzer.Core;

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

    /// <summary>
    /// HDRの段数(2または3)。FrameCountが段数と等しい場合はフレーム連結、
    /// FrameCount=1 の場合はライン交互として解釈される。
    /// </summary>
    public int HdrStages { get; init; } = 2;

    /// <summary>HDRの露光比(長秒:短秒、1段あたり)。HDR合成時のスケーリングに使う。</summary>
    public double ExposureRatio { get; init; } = 16.0;

    /// <summary>
    /// ライン交互DOLで1露光あたりに連続する行数。0なら自動
    /// (Bayerありは2行、モノクロは1行)。
    /// </summary>
    /// <remarks>
    /// センサによっては物理ライン1本ごとに長秒・短秒を読み出すため、
    /// Bayerでも1行単位になる。その場合は1を指定する
    /// (部分画像側では2行ごとに色位相が進むためモザイクは保たれる)。
    /// </remarks>
    public int HdrLineBlock { get; init; }

    /// <summary>
    /// 段ごとに累積する行オフセット(1段あたり)。読み出しのパイプライン遅延で
    /// 短秒側の画像が縦にずれている場合に、合成前の位置合わせへ使う。
    /// </summary>
    public int HdrRowOffset { get; init; }

    /// <summary>ライン交互DOLで実際に使う1露光あたりの行数。</summary>
    [JsonIgnore]
    public int EffectiveHdrLineBlock =>
        HdrLineBlock > 0 ? HdrLineBlock : Bayer != BayerPattern.None ? 2 : 1;

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

        if (Hdr != HdrMode.None && HdrStages is < 2 or > 3)
        {
            throw new ArgumentException($"HDR段数は2または3である必要があります: {HdrStages}");
        }

        if (HdrLineBlock < 0)
        {
            throw new ArgumentException(
                $"HDRのライン単位は非負である必要があります: {HdrLineBlock}");
        }
    }
}
