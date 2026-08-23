using System.Globalization;
using System.Text.RegularExpressions;

namespace RawAnalyzer.App.Services;

/// <summary>
/// フレームレート入力欄(編集可能コンボ)の文字列を数値へ解釈する。
/// </summary>
/// <remarks>
/// コンボの項目は「15 fps」のように単位付きなので、数字部分だけを取り出す。
/// 空欄や数字なしは呼び出し側の既定値へ落とす(入力途中で再生が止まらないように)。
/// </remarks>
internal static partial class FpsInput
{
    /// <summary>指定できる最小フレームレート。</summary>
    internal const double MinFps = 0.1;

    /// <summary>指定できる最大フレームレート。</summary>
    internal const double MaxFps = 240;

    /// <summary>
    /// 入力文字列からフレームレートを取り出す。
    /// </summary>
    /// <param name="text">コンボの表示文字列(例: "15 fps"、"7.5")。</param>
    /// <param name="fallback">解釈できない場合に返す値。</param>
    /// <returns><see cref="MinFps"/>〜<see cref="MaxFps"/> のフレームレート。</returns>
    internal static double Parse(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        Match match = NumberPattern().Match(text);
        if (!match.Success
            || !double.TryParse(
                match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || double.IsNaN(value)
            || value <= 0)
        {
            return fallback;
        }

        return Math.Clamp(value, MinFps, MaxFps);
    }

    /// <summary>
    /// 動画書き出し用に整数のフレームレートを取り出す(ライタが整数を要求するため)。
    /// </summary>
    /// <param name="text">コンボの表示文字列。</param>
    /// <param name="fallback">解釈できない場合に返す値。</param>
    /// <returns>1〜240のフレームレート。</returns>
    internal static int ParseInteger(string? text, int fallback)
    {
        double value = Parse(text, fallback);
        return (int)Math.Clamp(Math.Round(value), 1, MaxFps);
    }

    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex NumberPattern();
}
