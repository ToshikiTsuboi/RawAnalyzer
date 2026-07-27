namespace RawViewer.Core;

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
