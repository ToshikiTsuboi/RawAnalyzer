namespace RawAnalyzer.Core;

/// <summary>仮想スタック判定の入力となるファイル情報。</summary>
/// <param name="Path">ファイルのフルパス。</param>
/// <param name="Length">ファイルサイズ(バイト)。不明なら負値。</param>
public readonly record struct SequenceFile(string Path, long Length);

/// <summary>
/// 同一フォルダ内の連番ファイル群(仮想スタック)を判定する。
/// </summary>
/// <remarks>
/// <para>
/// 再生とバッチ書き出しで別々に実装されていると、片方だけ直したときに
/// 対象ファイルが食い違うため、判定はここへ一本化する。
/// </para>
/// <para>
/// 候補のうち基準ファイルと同じフォルダにあるものだけを使い、基準ファイル自身が連番に入らなければ
/// 空を返す(呼び出し側の一覧が別のフォルダのもの、基準ファイルが一覧に無いなど)。以前は候補のフォルダを
/// 見なかったので、一覧が別のフォルダだとそのフォルダの同じサイズ・同じ命名のファイルを連番とし、
/// 基準ファイルの位置も先頭(0)扱いになっていた。
/// </para>
/// </remarks>
public static class SequenceScanner
{
    /// <summary>
    /// 基準ファイルと同一拡張子・同一サイズのファイル群を自然順で返す。
    /// </summary>
    /// <param name="referencePath">基準ファイルのパス。</param>
    /// <param name="referenceLength">基準ファイルのサイズ。0以下なら判定不能として空を返す。</param>
    /// <param name="candidates">候補ファイル(基準ファイルと別のフォルダのものは使わない)。</param>
    /// <returns>
    /// スタックを構成するファイルパス(自然順)。基準ファイル自身が含まれない(候補に無い・サイズが違う)なら空。
    /// </returns>
    public static IReadOnlyList<string> FindStack(
        string referencePath, long referenceLength, IEnumerable<SequenceFile> candidates)
    {
        if (referenceLength <= 0)
        {
            return Array.Empty<string>();
        }

        string extension = Path.GetExtension(referencePath);
        return ContainingReference(
            referencePath,
            InSameFolder(referencePath, candidates)
                .Where(c => c.Length == referenceLength && string.Equals(
                    Path.GetExtension(c.Path), extension, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Path)
                .OrderBy(p => p, NaturalOrderComparer.Instance)
                .ToArray());
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
    /// <param name="candidates">候補ファイル(基準ファイルと別のフォルダのものは使わない)。</param>
    /// <returns>
    /// スタックを構成するファイルパス(自然順)。連番でない・基準ファイル自身が候補に無いなら空。
    /// </returns>
    public static IReadOnlyList<string> FindNumberedStack(
        string referencePath, IEnumerable<SequenceFile> candidates)
    {
        if (!TrySplitNumbering(referencePath, out string prefix, out string suffix))
        {
            return Array.Empty<string>();
        }

        string extension = Path.GetExtension(referencePath);
        return ContainingReference(
            referencePath,
            InSameFolder(referencePath, candidates)
                .Where(c => string.Equals(
                        Path.GetExtension(c.Path), extension, StringComparison.OrdinalIgnoreCase)
                    && TrySplitNumbering(c.Path, out string p, out string s)
                    && string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s, suffix, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Path)
                .OrderBy(p => p, NaturalOrderComparer.Instance)
                .ToArray());
    }

    /// <summary>候補のうち、基準ファイルと同じフォルダにあるもの。</summary>
    private static IEnumerable<SequenceFile> InSameFolder(
        string referencePath, IEnumerable<SequenceFile> candidates)
    {
        string? folder = DirectoryOf(referencePath);
        return folder is null
            ? Enumerable.Empty<SequenceFile>()
            : candidates.Where(c => string.Equals(
                DirectoryOf(c.Path), folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>連番に基準ファイル自身が含まれていればそのまま、含まれていなければ空を返す。</summary>
    private static IReadOnlyList<string> ContainingReference(string referencePath, string[] stack)
    {
        return stack.Any(p => SamePath(p, referencePath)) ? stack : Array.Empty<string>();
    }

    private static bool SamePath(string a, string b)
    {
        return string.Equals(FullPath(a), FullPath(b), StringComparison.OrdinalIgnoreCase);
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
    /// ファイルが指定のフォルダの直下にあるかを判定する(大文字小文字を区別せず、パスを正規化して比べる)。
    /// </summary>
    /// <param name="path">ファイルのパス。</param>
    /// <param name="folder">フォルダのパス(末尾の区切りの有無は問わない)。null なら false。</param>
    /// <returns>直下にあれば true。</returns>
    public static bool IsInFolder(string path, string? folder)
    {
        return folder is not null && DirectoryOf(path) is { } directory
            && string.Equals(directory, NormalizeFolder(folder), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ファイルのあるフォルダ(正規化済み)。求められなければ null。</summary>
    private static string? DirectoryOf(string path)
    {
        return Path.GetDirectoryName(FullPath(path)) is { } directory ? NormalizeFolder(directory) : null;
    }

    private static string NormalizeFolder(string folder)
    {
        return Path.TrimEndingDirectorySeparator(FullPath(folder));
    }

    private static string FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// スタック内での基準ファイルの位置を返す(大文字小文字を区別せず、パスを正規化して比べる)。見つからない場合は0。
    /// </summary>
    /// <remarks>
    /// <see cref="FindStack"/>・<see cref="FindNumberedStack"/> の結果は基準ファイルを必ず含む(含まなければ空)ので、
    /// その基準ファイルの位置は必ず見つかる。
    /// </remarks>
    /// <param name="stack">スタックを構成するファイルパス。</param>
    /// <param name="referencePath">基準ファイルのパス。</param>
    /// <returns>0起点のインデックス。</returns>
    public static int IndexOf(IReadOnlyList<string> stack, string referencePath)
    {
        for (int i = 0; i < stack.Count; i++)
        {
            if (SamePath(stack[i], referencePath))
            {
                return i;
            }
        }

        return 0;
    }
}
