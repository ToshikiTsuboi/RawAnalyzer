using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 画像ファイル(TIFF 等)を送るときに、利用者の Bayer 指定(右パネルの「Bayer」)を引き継ぐ規則。
/// </summary>
/// <remarks>
/// TIFF のページ送り(<see cref="TiffStackSource.BayerOverride"/>)とファイル連番の送りで共通。
/// 指定はグレーの画像にだけ付け、ファイル自身の CFAPattern より優先する(指定の初期値は開いた画像の
/// 配列なので、利用者が変えなければ開いたファイルの CFAPattern が続く)。デコード済みのカラー画像は
/// RGB のまま表示するので Bayer を付けない。指定そのものは、カラー画像を挟んでも呼び出し側が保持する。
/// </remarks>
internal static class ImageFileBayer
{
    /// <summary>
    /// 送った先の画像のフォーマットへ、引き継いだ Bayer 指定を付ける。
    /// </summary>
    /// <param name="fileFormat">画像ファイルから読んだフォーマット。</param>
    /// <param name="isColor">デコード済みのカラー画像か。</param>
    /// <param name="bayerOverride">引き継ぐ利用者の Bayer 指定。</param>
    /// <returns>表示・解析に使うフォーマット(Bayer 以外はファイルのまま)。</returns>
    internal static RawFormat Apply(RawFormat fileFormat, bool isColor, BayerPattern bayerOverride)
    {
        return fileFormat with { Bayer = isColor ? BayerPattern.None : bayerOverride };
    }
}
