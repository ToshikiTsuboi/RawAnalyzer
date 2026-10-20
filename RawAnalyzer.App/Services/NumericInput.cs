using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 数値入力欄の解釈。
/// </summary>
/// <remarks>
/// <c>double.TryParse</c> は "NaN" や "Infinity" も受け付ける。
/// <c>NaN &lt;= 0</c> は false なので、大小比較だけの検証はすり抜けてしまう。
/// 解釈は必ずここを通し、有限値であることを保証する。
/// IME がオンのまま打った全角の数字・記号と、3桁区切りのカンマ(かな入力の読点「、」を含む)も受け付ける。
/// </remarks>
internal static partial class NumericInput
{
    /// <summary>有限の数値として解釈する。</summary>
    /// <param name="text">入力文字列。</param>
    /// <param name="value">解釈できた値。</param>
    /// <returns>有限の数値ならtrue。</returns>
    internal static bool TryParseFinite(string? text, out double value)
    {
        return double.TryParse(
                   Normalize(text), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && double.IsFinite(value);
    }

    /// <summary>
    /// 全角の数字・記号を半角へ寄せ、3桁区切りのカンマを除く。
    /// </summary>
    /// <remarks>
    /// IME がオンのままだと「１２８」「１．５」「－５」のように全角で入り、かな入力では
    /// "." が「。」、"-" が「ー」、"," が「、」になる。どれも InvariantCulture の解釈では失敗し、
    /// 入力が黙って元の値へ戻されていた。カンマは3桁ごとの位置にあるときだけ区切りとみなし、
    /// "1,5" のような小数点のつもりかもしれない書き方は 15 と誤読せずに失敗のままにする。
    /// 先頭の群が 0 で始まる "0,125" も、3桁区切りの数はこう書かないので小数点のつもりのカンマとみなし、
    /// 125 と誤読せずに失敗のままにする(以前は区切りとみなして 1000 倍の値で通した)。
    /// </remarks>
    private static string? Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // NFKC は全角英数・記号・スペースを半角へ寄せる(－ → -、． → .、，→ ,)
        string normalized = ToHalfWidth(text)
            .Replace('\u2212', '-') // MINUS SIGN(−)
            .Replace('ー', '-')
            .Replace('。', '.')
            .Replace('、', ','); // 半角の読点(U+FF64)も NFKC でこれになる
        return GroupedThousands().IsMatch(normalized) ? normalized.Replace(",", "") : normalized;
    }

    /// <summary>
    /// NFKC で全角の英数・記号・スペースを半角へ寄せる。
    /// </summary>
    /// <remarks>
    /// 対になっていないサロゲートを含む(壊れた UTF-16 の)文字列では <see cref="string.Normalize(NormalizationForm)"/> が
    /// ArgumentException を投げる。以前はそれが Try の解釈から漏れ、貼り付けた入力で「予期しないエラー」になった。
    /// そうした文字列は寄せずにそのまま返す(どの数値の解釈も読めないので、"abc" と同じく解釈失敗になる)。
    /// </remarks>
    /// <param name="text">入力文字列。</param>
    /// <returns>寄せた文字列。不正な UTF-16 ならそのまま。</returns>
    internal static string ToHalfWidth(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    [GeneratedRegex(@"^\s*[+-]?[1-9][0-9]{0,2}(,[0-9]{3})+(\.[0-9]*)?\s*$")]
    private static partial Regex GroupedThousands();

    /// <summary>有限かつ正の数値として解釈する。</summary>
    /// <param name="text">入力文字列。</param>
    /// <param name="value">解釈できた値。</param>
    /// <returns>有限かつ正ならtrue。</returns>
    internal static bool TryParsePositive(string? text, out double value)
    {
        return TryParseFinite(text, out value) && value > 0;
    }

    /// <summary>整数として解釈する(全角の数字・符号と3桁区切りも受け付ける。小数・指数表記は断る)。</summary>
    /// <param name="text">入力文字列。</param>
    /// <param name="value">解釈できた値。</param>
    /// <returns>long の範囲の整数ならtrue。</returns>
    internal static bool TryParseInteger(string? text, out long value)
    {
        return long.TryParse(Normalize(text), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>整数として解釈する(全角の数字・符号と3桁区切りも受け付ける。小数・指数表記は断る)。</summary>
    /// <param name="text">入力文字列。</param>
    /// <param name="value">解釈できた値。</param>
    /// <returns>int の範囲の整数ならtrue。</returns>
    internal static bool TryParseInteger(string? text, out int value)
    {
        return int.TryParse(Normalize(text), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
