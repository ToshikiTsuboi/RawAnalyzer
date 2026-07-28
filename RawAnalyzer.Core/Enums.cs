using System.Text.Json;
using System.Text.Json.Serialization;

namespace RawAnalyzer.Core;

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
/// HDRフレームのファイル上の格納レイアウト。
/// </summary>
/// <remarks>
/// DOL / Staggered といったベンダー名はセンサ側の露光・読み出しの時間関係を指すもので、
/// バッファに落ちた画素の並びからは判別できない(どちらの方式でも両レイアウトがあり得る)。
/// ここではファイルを解釈するのに必要な「並び」だけを持つ。
/// ベンダー名を記録したい場合はプリセット名に含める。
/// </remarks>
[JsonConverter(typeof(HdrModeJsonConverter))]
public enum HdrMode
{
    /// <summary>HDRなし(単一露光)。</summary>
    None,

    /// <summary>フレーム数から推定する(段数と一致すればフレーム連結、1ならライン交互)。</summary>
    Auto,

    /// <summary>ライン交互。1枚の中に長秒行と短秒行が交互に並ぶ。</summary>
    LineInterleaved,

    /// <summary>フレーム連結。長秒フレームの後ろに短秒フレームが続く。</summary>
    FrameSequential,
}

/// <summary>
/// <see cref="HdrMode"/> の文字列変換。旧名 Dol / Staggered を
/// <see cref="HdrMode.Auto"/> として読み込む(どちらも挙動は同じだったため互換)。
/// </summary>
internal sealed class HdrModeJsonConverter : JsonConverter<HdrMode>
{
    /// <inheritdoc />
    public override HdrMode Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return Enum.IsDefined(typeof(HdrMode), reader.GetInt32())
                ? (HdrMode)reader.GetInt32()
                : HdrMode.None;
        }

        string? text = reader.GetString();
        return text switch
        {
            null or "" => HdrMode.None,

            // 旧プリセット/セッションの値。当時は両者とも FrameCount から推定していた
            "Dol" or "Staggered" => HdrMode.Auto,
            _ => Enum.TryParse(text, ignoreCase: true, out HdrMode value) ? value : HdrMode.None,
        };
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, HdrMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
