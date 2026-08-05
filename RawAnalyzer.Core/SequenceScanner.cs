namespace RawAnalyzer.Core;

/// <summary>仮想スタック判定の入力となるファイル情報。</summary>
/// <param name="Path">ファイルのフルパス。</param>
/// <param name="Length">ファイルサイズ(バイト)。不明なら負値。</param>
public readonly record struct SequenceFile(string Path, long Length);

/// <summary>
/// 同一フォルダ内の連番ファイル群(仮想スタック)を判定する。
/// </summary>
/// <remarks>
/// 再生とバッチ書き出しで別々に実装されていると、片方だけ直したときに
/// 対象ファイルが食い違うため、判定はここへ一本化する。
/// </remarks>
public static class SequenceScanner
{
    /// <summary>
    /// 基準ファイルと同一拡張子・同一サイズのファイル群を自然順で返す。
    /// </summary>
    /// <param name="referencePath">基準ファイルのパス。</param>
    /// <param name="referenceLength">基準ファイルのサイズ。0以下なら判定不能として空を返す。</param>
    /// <param name="candidates">同一フォルダ内の候補ファイル。</param>
    /// <returns>スタックを構成するファイルパス(自然順)。</returns>
    public static IReadOnlyList<string> FindStack(
        string referencePath, long referenceLength, IEnumerable<SequenceFile> candidates)
    {
        if (referenceLength <= 0)
        {
            return Array.Empty<string>();
        }

        string extension = Path.GetExtension(referencePath);
        return candidates
            .Where(c => c.Length == referenceLength && string.Equals(
                Path.GetExtension(c.Path), extension, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Path)
            .OrderBy(p => p, NaturalOrderComparer.Instance)
            .ToArray();
    }

    /// <summary>
    /// 基準ファイルと同じ命名の連番ファイル群を自然順で返す。
    /// ファイル名の末尾側にある数字の並びを連番とみなし、
    /// その前後(接頭辞・接尾辞)と拡張子が一致するものを集める。
    /// </summary>
    /// <remarks>
    /// 画像ファイル用。rawと違い解像度はヘッダにあるためファイルサイズ一致に
    /// 意味がなく、圧縮形式ではフレームごとにサイズが変わってしまう。
    /// 判定にファイルの中身を読まないので、低速なストレージでも待たされない。
    /// </remarks>
    /// <param name="referencePath">基準ファイルのパス。</param>
    /// <param name="candidates">同一フォルダ内の候補ファイル。</param>
    /// <returns>スタックを構成するファイルパス(自然順)。連番でなければ空。</returns>
    public static IReadOnlyList<string> FindNumberedStack(
        string referencePath, IEnumerable<SequenceFile> candidates)
    {
        if (!TrySplitNumbering(referencePath, out string prefix, out string suffix))
        {
            return Array.Empty<string>();
        }

        string extension = Path.GetExtension(referencePath);
        return candidates
            .Where(c => string.Equals(
                    Path.GetExtension(c.Path), extension, StringComparison.OrdinalIgnoreCase)
                && TrySplitNumbering(c.Path, out string p, out string s)
                && string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s, suffix, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Path)
            .OrderBy(p => p, NaturalOrderComparer.Instance)
            .ToArray();
    }

    /// <summary>
    /// ファイル名(拡張子を除く)を「連番部分の前」と「後ろ」に分ける。
    /// 連番は末尾側にある数字の並びを採る(例 2026-08-04_012 → "2026-08-04_" と "")。
    /// </summary>
    /// <param name="path">ファイルパス。</param>
    /// <param name="prefix">連番より前の部分。</param>
    /// <param name="suffix">連番より後ろの部分。</param>
    /// <returns>数字を含まない名前ならfalse。</returns>
    private static bool TrySplitNumbering(string path, out string prefix, out string suffix)
    {
        prefix = "";
        suffix = "";
        string stem = Path.GetFileNameWithoutExtension(path);
        int end = -1;
        for (int i = stem.Length - 1; i >= 0; i--)
        {
            if (char.IsAsciiDigit(stem[i]))
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            return false;
        }

        int start = end;
        while (start > 0 && char.IsAsciiDigit(stem[start - 1]))
        {
            start--;
        }

        prefix = stem[..start];
        suffix = stem[(end + 1)..];
        return true;
    }

    /// <summary>
    /// スタック内での基準ファイルの位置を返す。見つからない場合は0。
    /// </summary>
    /// <param name="stack">スタックを構成するファイルパス。</param>
    /// <param name="referencePath">基準ファイルのパス。</param>
    /// <returns>0起点のインデックス。</returns>
    public static int IndexOf(IReadOnlyList<string> stack, string referencePath)
    {
        for (int i = 0; i < stack.Count; i++)
        {
            if (string.Equals(stack[i], referencePath, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }
}
