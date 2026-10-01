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

    /// <summary>2枚目に raw を指定したときに、測定を断る理由。</summary>
    /// <remarks>
    /// 2枚目の raw はディスク上の未処理のファイル。処理結果(ビニング・フィルタ・欠陥補正・画像演算)の対象Aとの
    /// 差分には、ノイズではなく処理の違い(平滑化・補正した欠陥・引いたダーク)が乗り、時間ノイズにならない。
    /// ビニング・フィルタの結果は 16bit・ヘッダ0(ビニングは寸法も)の形式に替わるので、以前はその形式のまま
    /// 2枚目を読み、下詰めNbitのファイルでは正規化されない値との差分を、サイズ警告も出さずに測っていた。
    /// 同じ処理をして保存した画像ファイルは2枚目に使える(画像ファイルはファイル自身の形式で読む)。
    /// HDR合成ビューの対象Aは、元画像を分割して黒レベルを引き、露光比で合成して16bitへ量子化したもの。
    /// 2枚目の raw に同じ合成を掛けなければ同じ画素・同じ値の対応で比べられない(合成ビューの形式のまま
    /// 読むとサイズも合わない)。分割ビューは2枚目も同じ並びに分割して比べる(<see cref="HdrSplitComposite"/>)。
    /// </remarks>
    /// <param name="processed">対象Aが処理結果(ビニング・フィルタ・欠陥補正・画像演算)か。</param>
    /// <param name="mergedView">対象AがHDR合成ビューか。</param>
    /// <returns>断る理由。測定できるならnull。</returns>
    internal static string? RawReferenceRefusal(bool processed, bool mergedView)
    {
        if (processed)
        {
            return "処理結果(ビニング・フィルタ・欠陥補正・画像演算)の画像では、2枚目にrawを指定した" +
                "2枚差分の測定はできません(2枚目のrawは処理されていないため、差分にノイズではなく処理の違いが乗ります)。\n" +
                "元のファイルを開き直して測定するか、2枚目にも同じ処理をして保存した画像ファイルを指定してください。";
        }

        return mergedView
            ? "HDR合成ビューでは、2枚目にrawを指定した2枚差分の測定はできません" +
              "(2枚目のrawは合成ビューと同じ黒レベル・露光比で合成されないため、Aと別の値同士の差分になります)。\n" +
              "HDR分割ビューに切り替え、測る段の中にROIを置いて測定してください(2枚目も同じく分割して比べます)。"
            : null;
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
