namespace RawAnalyzer.Core;

/// <summary>
/// ファイル名の連番に使う数字(半角 0〜9 と全角 ０〜９)の判定。
/// </summary>
/// <remarks>
/// IME がオンのまま付けた名前は「暗室１２.raw」のように全角数字になる。自然順の並べ替え
/// (<see cref="NaturalOrderComparer"/>)と連番の判定(<see cref="SequenceScanner"/>)で同じ数字の範囲を使う。
/// 他の書記体系の数字(char.IsDigit が true のもの全般)は含めない。
/// </remarks>
internal static class DigitChar
{
    /// <summary>半角または全角の数字か。</summary>
    /// <param name="c">文字。</param>
    /// <returns>0〜9 または ０〜９ なら true。</returns>
    internal static bool IsDigit(char c) => char.IsAsciiDigit(c) || c is >= '０' and <= '９';

    /// <summary>
    /// 全角数字を同じ値の半角数字へ写す。それ以外の文字はそのまま返す。
    /// </summary>
    /// <param name="c">文字。</param>
    /// <returns>半角へ寄せた文字。</returns>
    internal static char ToAscii(char c) => c is >= '０' and <= '９' ? (char)('0' + (c - '０')) : c;
}
