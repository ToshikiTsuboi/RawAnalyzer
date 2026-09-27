using System.IO;

namespace RawAnalyzer.App.Services;

/// <summary>
/// ノイズ測定の2枚目(B)が、表示中の対象(A)と同じデータかどうかの判定。
/// </summary>
/// <remarks>
/// 2枚目はファイルの先頭フレーム・先頭ページを読む。同一データ同士の差分は常に0で、
/// σ_temporal=0 という誤った値が無警告で出るため、同一なら測定を断る。
/// ただしAが同じファイルでも、表示中のフレームやTIFFページが先頭でなければ別データである。
/// TIFFの各ページはフレーム数1の画像として開くため表示フレームは常に0で、
/// フレーム番号だけではページの違いを判別できない。
/// </remarks>
internal static class NoiseReference
{
    /// <summary>
    /// 2枚目として読むデータ(ファイルの先頭フレーム・先頭ページ)が、
    /// 対象Aとして表示中のデータと同一か判定する。
    /// </summary>
    /// <param name="referencePath">2枚目のパス。</param>
    /// <param name="currentPath">対象Aのファイル(なければnull)。</param>
    /// <param name="isDerivedView">対象AがHDR分割/合成の派生ビューか(ファイルとは別データ)。</param>
    /// <param name="frame">対象Aの表示フレーム。</param>
    /// <param name="tiffPageIndex">対象Aの表示TIFFページ(0起点。複数ページでなければ0)。</param>
    /// <returns>同一データならtrue。</returns>
    internal static bool ReadsSameDataAsTarget(
        string referencePath, string? currentPath, bool isDerivedView, int frame,
        int tiffPageIndex)
    {
        return currentPath is not null && !isDerivedView && frame == 0 && tiffPageIndex == 0
            && IsSameFile(referencePath, currentPath);
    }

    private static bool IsSameFile(string first, string second)
    {
        try
        {
            // 書き方の違い(大文字小文字・相対パス・..)で同一ファイルを見逃さない
            return string.Equals(
                Path.GetFullPath(first), Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    }
}
