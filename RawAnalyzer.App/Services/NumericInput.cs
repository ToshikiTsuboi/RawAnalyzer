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
/// IME がオンのまま打った全角の数字・記号と、3桁区切りのカンマも受け付ける。
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
    /// "." が「。」、"-" が「ー」になる。どれも InvariantCulture の解釈では失敗し、
    /// 入力が黙って元の値へ戻されていた。カンマは3桁ごとの位置にあるときだけ区切りとみなし、
    /// "1,5" のような小数点のつもりかもしれない書き方は 15 と誤読せずに失敗のままにする。
    /// </remarks>
    private static string? Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // NFKC は全角英数・記号・スペースを半角へ寄せる(－ → -、． → .、，→ ,)
        string normalized = text.Normalize(NormalizationForm.FormKC)
            .Replace('\u2212', '-') // MINUS SIGN(−)
            .Replace('ー', '-')
            .Replace('。', '.');
        return GroupedThousands().IsMatch(normalized) ? normalized.Replace(",", "") : normalized;
    }

    [GeneratedRegex(@"^\s*[+-]?[0-9]{1,3}(,[0-9]{3})+(\.[0-9]*)?\s*$")]
    private static partial Regex GroupedThousands();

    /// <summary>有限かつ正の数値として解釈する。</summary>
    /// <param name="text">入力文字列。</param>
    /// <param name="value">解釈できた値。</param>
    /// <returns>有限かつ正ならtrue。</returns>
    internal static bool TryParsePositive(string? text, out double value)
    {
        return TryParseFinite(text, out value) && value > 0;
    }
}
