using System.Text.Json.Serialization;

namespace RawViewer.Core;

/// <summary>
/// Nビット画素値の16bitコンテナ内での詰め方向。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BitPacking
{
    /// <summary>下詰め(LSB側に値が入る)。正規化は value &lt;&lt; (16-N)。</summary>
    Lsb,

    /// <summary>上詰め(MSB側に値が入る)。値はすでにフルスケール位置にある。</summary>
    Msb,
}

/// <summary>
/// マルチバイト画素値のバイト順。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Endianness
{
    /// <summary>リトルエンディアン。</summary>
    Little,

    /// <summary>ビッグエンディアン。</summary>
    Big,
}

/// <summary>
/// センサのBayerカラーフィルタ配列パターン。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BayerPattern
{
    /// <summary>モノクロ(Bayerなし)。</summary>
    None,

    /// <summary>R G / G B 配列。</summary>
    Rggb,

    /// <summary>B G / G R 配列。</summary>
    Bggr,

    /// <summary>G R / B G 配列。</summary>
    Grbg,

    /// <summary>G B / R G 配列。</summary>
    Gbrg,
}

/// <summary>
/// HDRフレームの格納方式。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HdrMode
{
    /// <summary>HDRなし(単一露光)。</summary>
    None,

    /// <summary>Sony DOL方式。長秒/短秒フレームがライン交互またはフレーム連結で格納される。</summary>
    Dol,

    /// <summary>Omnivision Staggered方式。長秒/短秒フレームがライン交互に格納される。</summary>
    Staggered,
}
