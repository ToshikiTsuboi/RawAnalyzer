namespace RawAnalyzer.Core;

/// <summary>
/// 数値部分を数値として比較する自然順ソート比較器
/// (例: img2.raw &lt; img10.raw)。連番ファイルの並びに使う。
/// </summary>
/// <remarks>
/// 全角数字(０〜９)も同じ値の半角数字として扱う(例: 暗室２.raw &lt; 暗室１０.raw)。
/// </remarks>
public sealed class NaturalOrderComparer : IComparer<string>
{
    /// <summary>共有インスタンス。</summary>
    public static readonly NaturalOrderComparer Instance = new();

    /// <summary>
    /// 2つの文字列を自然順で比較する。
    /// </summary>
    /// <param name="x">左辺。</param>
    /// <param name="y">右辺。</param>
    /// <returns>
    /// <paramref name="x"/> が <paramref name="y"/> より前なら負、同順なら 0、後なら正。
    /// <see cref="IComparer{T}"/> の慣例どおり符号だけが意味を持ち、-1/1 とは限らない(桁数や文字コードの差を返す)。
    /// </returns>
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.CompareOrdinal(x, y);
        }

        // 全角数字(IME がオンのまま付けた名前)は同じ値の半角数字として扱う。数値ブロックの判定だけでなく、
        // 数字と他の文字の大小にも半角数字の文字コードを使い、半角・全角が混ざっても推移律を保つ
        // (比較は全角数字を半角へ写した文字列どうしの比較と同じになる)
        int i = 0;
        int j = 0;
        while (i < x.Length && j < y.Length)
        {
            char cx = DigitChar.ToAscii(x[i]);
            char cy = DigitChar.ToAscii(y[j]);
            if (char.IsAsciiDigit(cx) && char.IsAsciiDigit(cy))
            {
                // 数値ブロックを取り出して数値として比較(先頭0はスキップして桁数→上の桁から)
                int startX = i;
                while (i < x.Length && DigitChar.IsDigit(x[i]))
                {
                    i++;
                }

                int startY = j;
                while (j < y.Length && DigitChar.IsDigit(y[j]))
                {
                    j++;
                }

                while (startX < i && DigitChar.ToAscii(x[startX]) == '0')
                {
                    startX++;
                }

                while (startY < j && DigitChar.ToAscii(y[startY]) == '0')
                {
                    startY++;
                }

                int lengthX = i - startX;
                int lengthY = j - startY;
                if (lengthX != lengthY)
                {
                    return lengthX - lengthY;
                }

                for (int k = 0; k < lengthX; k++)
                {
                    int digitCompare = DigitChar.ToAscii(x[startX + k]) - DigitChar.ToAscii(y[startY + k]);
                    if (digitCompare != 0)
                    {
                        return digitCompare;
                    }
                }
            }
            else
            {
                int charCompare = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
                if (charCompare != 0)
                {
                    return charCompare;
                }

                i++;
                j++;
            }
        }

        return (x.Length - i) - (y.Length - j);
    }
}
