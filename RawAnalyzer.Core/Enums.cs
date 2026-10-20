using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RawAnalyzer.Core;

/// <summary>
/// Nビット画素値の16bitコンテナ内での詰め方向。
/// </summary>
[JsonConverter(typeof(EnumNameJsonConverter<BitPacking>))]
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
[JsonConverter(typeof(EnumNameJsonConverter<Endianness>))]
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
[JsonConverter(typeof(EnumNameJsonConverter<BayerPattern>))]
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
        // 数値と数値の文字列は定義された値だけを読み、それ以外(未定義の値・知らない名前・
        // "Auto, LineInterleaved" のような組み合わせ)は既定の None へ戻す
        // (Enum.TryParse は "99" を未定義の値のまま、組み合わせをフラグの論理和 1|2 として通してしまう)
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.TryGetInt32(out int number) && Enum.IsDefined((HdrMode)number)
                ? (HdrMode)number
                : HdrMode.None;
        }

        string? text = reader.GetString();
        if (EnumJson.TryParseInteger(text, out int numberText))
        {
            return Enum.IsDefined((HdrMode)numberText) ? (HdrMode)numberText : HdrMode.None;
        }

        return text switch
        {
            null or "" => HdrMode.None,

            // 旧プリセット/セッションの値。当時は両者とも FrameCount から推定していた
            "Dol" or "Staggered" => HdrMode.Auto,
            _ => EnumJson.TryParseName(text, out HdrMode value) ? value : HdrMode.None,
        };
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, HdrMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// 列挙値を名前の文字列で読み書きする(<see cref="BitPacking"/>・<see cref="Endianness"/>・<see cref="BayerPattern"/>)。
/// </summary>
/// <remarks>
/// <see cref="JsonStringEnumConverter"/> は "Rggb, Bggr" のようなカンマ区切りをフラグの論理和(1|2 = Grbg)として
/// 黙って通してしまう。ここでは定義された名前(大文字小文字は区別しない)だけを受け付け、それ以外の文字列は
/// 知らない名前と同じく <see cref="JsonException"/> にする。数値と数値の文字列は値のまま読み、
/// 未定義の値は <see cref="RawFormat.Validate"/> で弾く(記憶の読み込みはその記憶だけを捨てる)。
/// 書き出しは名前の文字列(未定義の値だけは数値)。
/// </remarks>
/// <typeparam name="TEnum">列挙型。</typeparam>
internal sealed class EnumNameJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <inheritdoc />
    public override TEnum Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.TryGetInt32(out int number)
                ? (TEnum)Enum.ToObject(typeof(TEnum), number)
                : throw new JsonException($"{typeof(TEnum).Name} として読めない数値です。");
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            string? text = reader.GetString();
            if (EnumJson.TryParseInteger(text, out int numberText))
            {
                return (TEnum)Enum.ToObject(typeof(TEnum), numberText);
            }

            if (EnumJson.TryParseName(text, out TEnum value))
            {
                return value;
            }
        }

        throw new JsonException($"{typeof(TEnum).Name} として読めない値です。");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (Enum.IsDefined(value))
        {
            writer.WriteStringValue(value.ToString());
        }
        else
        {
            writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>列挙値の JSON 変換の共通処理。</summary>
internal static class EnumJson
{
    /// <summary>整数だけからなる文字列(前後の空白と符号は可)を int として読む。</summary>
    /// <param name="text">文字列。</param>
    /// <param name="value">読んだ値。</param>
    /// <returns>整数として読めたら true。</returns>
    internal static bool TryParseInteger(string? text, out int value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(text)
            && int.TryParse(
                text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// 定義された名前(大文字小文字は区別しない)だけを列挙値として読む。
    /// Enum.TryParse と違い、数値やカンマ区切りの組み合わせは受け付けない。
    /// </summary>
    /// <typeparam name="TEnum">列挙型。</typeparam>
    /// <param name="text">文字列。</param>
    /// <param name="value">読んだ値。</param>
    /// <returns>定義された名前なら true。</returns>
    internal static bool TryParseName<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        string name = text?.Trim() ?? "";
        foreach (string candidate in Enum.GetNames<TEnum>())
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                value = Enum.Parse<TEnum>(candidate);
                return true;
            }
        }

        value = default;
        return false;
    }
}
