using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RawAnalyzer.App.Services;

/// <summary>
/// フレームレート入力欄(編集可能コンボ)の文字列を数値へ解釈する。
/// </summary>
/// <remarks>
/// コンボの項目は「15 fps」のように単位付きなので、数字部分だけを取り出す。
/// 空欄や数字なし、0 以下の値は呼び出し側の既定値へ落とす(入力途中で再生が止まらないように)。
/// 落としたこと・範囲へ収めたことは説明を返し、入力欄に赤枠とツールチップで示す(黙って落とさない)。
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
    internal static double Parse(string? text, double fallback) => Interpret(text, fallback, MinFps, out _);

    /// <summary>
    /// 入力文字列からフレームレートを取り出し、打ったとおりに使えないとき(既定値へ落とした・範囲へ収めた)は
    /// その説明を返す。
    /// </summary>
    /// <remarks>
    /// 既定値・範囲へ落とす挙動は <see cref="Parse(string?, double)"/> と同じ。以前は黙って落としたので、
    /// 打った値と違う速さで再生・書き出しになったことが見えなかった。説明は入力欄の不正の表示(赤枠と
    /// ツールチップ)に出す。
    /// </remarks>
    /// <param name="text">コンボの表示文字列。</param>
    /// <param name="fallback">解釈できない場合に返す値。</param>
    /// <param name="notice">打ったとおりに使えないときの説明。そのまま使えるなら null。</param>
    /// <returns><see cref="MinFps"/>〜<see cref="MaxFps"/> のフレームレート。</returns>
    internal static double Parse(string? text, double fallback, out string? notice) =>
        Interpret(text, fallback, MinFps, out notice);

    private static double Interpret(string? text, double fallback, double minimum, out string? notice)
    {
        notice = null;
        string fallbackText = $"既定の {Format(fallback)} fps を使います";
        if (string.IsNullOrWhiteSpace(text))
        {
            notice = $"フレームレートが空のため、{fallbackText}";
            return fallback;
        }

        // NFKC で全角の数字・記号(－ ． ，)を半角へ寄せる。かな入力の「ー」(負号)と「。」(小数点)は
        // NFKC では変わらないので、そのまま拾って NumericInput に読ませる
        string normalized = text.Normalize(NormalizationForm.FormKC);
        Match match = NumberPattern().Match(normalized);
        if (!match.Success || IsCutAtAmbiguousComma(normalized, match)
            || !NumericInput.TryParseFinite(match.Value, out double value))
        {
            notice = $"「{text.Trim()}」からフレームレートを読めないため、{fallbackText}";
            return fallback;
        }

        if (value <= 0)
        {
            notice = $"{Format(value)} fps は使えない(0 より大きい値が必要な)ため、{fallbackText}";
            return fallback;
        }

        double clamped = Math.Clamp(value, minimum, MaxFps);
        if (clamped != value)
        {
            notice = $"{Format(value)} fps は範囲 {Format(minimum)}〜{Format(MaxFps)} fps の外のため、"
                + $"{Format(clamped)} fps を使います";
        }

        return clamped;
    }

    private static string Format(double fps) => fps.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// 取り出した数が、3桁区切りではないカンマの手前で切れているか("7,5" の "7"、"1,5000" の "1,500")。
    /// </summary>
    /// <remarks>
    /// 小数点のつもりかもしれないカンマは、他の数値入力欄(<see cref="NumericInput"/>)と同じく読めない入力とする。
    /// 以前はカンマの手前だけを読み、"7,5" を 7 fps と黙って誤読した。
    /// </remarks>
    private static bool IsCutAtAmbiguousComma(string text, Match match)
    {
        int end = match.Index + match.Length;
        return end < text.Length
            && (char.IsAsciiDigit(text[end])
                || (text[end] == ',' && end + 1 < text.Length && char.IsAsciiDigit(text[end + 1])));
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
    internal static int ParseInteger(string? text, int fallback) => ParseInteger(text, fallback, out _);

    /// <summary>
    /// 動画書き出し用に整数のフレームレートを取り出し、打ったとおりに使えないとき(既定値へ落とした・
    /// 1〜240 fps へ収めた)はその説明を返す。整数への丸め(29.97 → 30)は知らせない。
    /// </summary>
    /// <param name="text">コンボの表示文字列。</param>
    /// <param name="fallback">解釈できない場合に返す値。</param>
    /// <param name="notice">打ったとおりに使えないときの説明。そのまま使えるなら null。</param>
    /// <returns>1〜240のフレームレート。</returns>
    internal static int ParseInteger(string? text, int fallback, out string? notice)
    {
        // 書き出せる下限は整数の 1 fps。先に 1 へ収めても、従来の(0.1 へ収めてから丸め、1 へ収める)結果と同じ
        double value = Interpret(text, fallback, 1, out notice);
        return (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 1, MaxFps);
    }

    [GeneratedRegex(@"[+\-−ー]?[0-9]+(,[0-9]{3})*([.。][0-9]+)?")]
    private static partial Regex NumberPattern();
}
