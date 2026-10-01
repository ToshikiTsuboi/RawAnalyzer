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
/// 右パネルで指定を変えたときも、表示中の画像へは同じ規則で付ける(raw は常にグレーなので指定どおり)。
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

    /// <summary>
    /// 一括書き出しでグレーのページ・ファイルに付ける Bayer を決める。
    /// </summary>
    /// <remarks>
    /// raw は常にグレーで、表示中のフォーマット(右パネルの指定を含む)で全ファイルを読む。画像ファイルは送りと
    /// 同じく利用者の指定を引き継ぐ。表示中の画像のフォーマットはカラーのページ・ファイルでは Bayer=None に
    /// なるので、それを使うとカラーの画像の表示中に始めた書き出しだけが、グレーの画像を指定を無視して
    /// モノクロで焼く。ページ送りできる TIFF はスタックの指定、それ以外(ファイル連番。連番で到達して
    /// ページ送りしない TIFF を含む)は連番の指定を使う。カラーの画像は書き出しでも現像しない。
    /// </remarks>
    /// <param name="rawTargets">対象が raw ファイルか。</param>
    /// <param name="shownBayer">表示中の画像のフォーマットの Bayer。</param>
    /// <param name="stack">開いている TIFF のページ。なければ null。</param>
    /// <param name="sequenceOverride">ファイル連番の送りで引き継いでいる Bayer 指定。</param>
    /// <returns>書き出す各グレー画像に付ける Bayer。</returns>
    internal static BayerPattern ForBatch(
        bool rawTargets, BayerPattern shownBayer, TiffStackSource? stack, BayerPattern sequenceOverride)
    {
        if (rawTargets)
        {
            return shownBayer;
        }

        return stack is { PageNavigationEnabled: true } ? stack.BayerOverride : sequenceOverride;
    }
}
