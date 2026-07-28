namespace RawAnalyzer.Core;

/// <summary>
/// 数値部分を数値として比較する自然順ソート比較器
/// (例: img2.raw &lt; img10.raw)。連番ファイルの並びに使う。
/// </summary>
public sealed class NaturalOrderComparer : IComparer<string>
{
    /// <summary>共有インスタンス。</summary>
    public static readonly NaturalOrderComparer Instance = new();

    /// <summary>
    /// 2つの文字列を自然順で比較する。
    /// </summary>
    /// <param name="x">左辺。</param>
    /// <param name="y">右辺。</param>
    /// <returns>比較結果(-1/0/1)。</returns>
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.CompareOrdinal(x, y);
        }

        int i = 0;
        int j = 0;
        while (i < x.Length && j < y.Length)
        {
            char cx = x[i];
            char cy = y[j];
            if (char.IsAsciiDigit(cx) && char.IsAsciiDigit(cy))
            {
                // 数値ブロックを取り出して数値として比較(先頭0はスキップして桁数→辞書順)
                int startX = i;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                int startY = j;
                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                ReadOnlySpan<char> numX = x.AsSpan(startX, i - startX).TrimStart('0');
                ReadOnlySpan<char> numY = y.AsSpan(startY, j - startY).TrimStart('0');
                if (numX.Length != numY.Length)
                {
                    return numX.Length - numY.Length;
                }

                int digitCompare = numX.CompareTo(numY, StringComparison.Ordinal);
                if (digitCompare != 0)
                {
                    return digitCompare;
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
