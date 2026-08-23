using System.Globalization;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 数値入力欄の解釈。
/// </summary>
/// <remarks>
/// <c>double.TryParse</c> は "NaN" や "Infinity" も受け付ける。
/// <c>NaN &lt;= 0</c> は false なので、大小比較だけの検証はすり抜けてしまう。
/// 解釈は必ずここを通し、有限値であることを保証する。
/// </remarks>
internal static class NumericInput
{
    /// <summary>有限の数値として解釈する。</summary>
    /// <param name="text">入力文字列。</param>
    /// <param name="value">解釈できた値。</param>
    /// <returns>有限の数値ならtrue。</returns>
    internal static bool TryParseFinite(string? text, out double value)
    {
        return double.TryParse(
                   text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && double.IsFinite(value);
    }

    /// <summary>有限かつ正の数値として解釈する。</summary>
    /// <param name="text">入力文字列。</param>
    /// <param name="value">解釈できた値。</param>
    /// <returns>有限かつ正ならtrue。</returns>
    internal static bool TryParsePositive(string? text, out double value)
    {
        return TryParseFinite(text, out value) && value > 0;
    }
}
