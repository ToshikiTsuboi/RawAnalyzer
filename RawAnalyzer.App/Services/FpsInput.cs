using System.Text;
using System.Text.RegularExpressions;

namespace RawAnalyzer.App.Services;

/// <summary>
/// フレームレート入力欄(編集可能コンボ)の文字列を数値へ解釈する。
/// </summary>
/// <remarks>
/// コンボの項目は「15 fps」のように単位付きなので、数字部分だけを取り出す。
/// 空欄や数字なし、0 以下の値は呼び出し側の既定値へ落とす(入力途中で再生が止まらないように)。
/// 数字部分の解釈は他の数値入力欄と同じく <see cref="NumericInput"/> を通し、IME がオンのまま打った
/// 全角の数字・記号と3桁区切りも読む。
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
    /// <remarks>
    /// 以前は符号を取らずに数字だけを探したので "-5" を 5 fps と読み、全角の数字は見つけても読めずに黙って既定値へ
    /// 落としていた。全角を半角へ寄せてから符号付きの数を探し、負の値は 0 と同じく既定値へ落とす。
    /// </remarks>
    /// <param name="text">コンボの表示文字列(例: "15 fps"、"7.5")。</param>
    /// <param name="fallback">解釈できない場合に返す値。</param>
    /// <returns><see cref="MinFps"/>〜<see cref="MaxFps"/> のフレームレート。</returns>
    internal static double Parse(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        // NFKC で全角の数字・記号(－ ． ，)を半角へ寄せる。かな入力の「ー」(負号)と「。」(小数点)は
        // NFKC では変わらないので、そのまま拾って NumericInput に読ませる
        Match match = NumberPattern().Match(text.Normalize(NormalizationForm.FormKC));
        if (!match.Success || !NumericInput.TryParsePositive(match.Value, out double value))
        {
            return fallback;
        }

        return Math.Clamp(value, MinFps, MaxFps);
    }

    /// <summary>
    /// 動画書き出し用に整数のフレームレートを取り出す(ライタが整数を要求するため)。
    /// </summary>
    /// <remarks>
    /// .5 は常に大きい方へ丸める(12.5 → 13、13.5 → 14)。以前は既定の銀行丸めで 12.5 → 12、13.5 → 14 と
    /// 値によって向きが変わった。
    /// </remarks>
    /// <param name="text">コンボの表示文字列。</param>
    /// <param name="fallback">解釈できない場合に返す値。</param>
    /// <returns>1〜240のフレームレート。</returns>
    internal static int ParseInteger(string? text, int fallback)
    {
        double value = Parse(text, fallback);
        return (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 1, MaxFps);
    }

    [GeneratedRegex(@"[+\-−ー]?[0-9]+(,[0-9]{3})*([.。][0-9]+)?")]
    private static partial Regex NumberPattern();
}
