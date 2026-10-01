using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 画像演算の参照画像(B)・ノイズ測定の2枚目を、表示中の対象(A)と組み合わせて読む規約。
/// </summary>
internal static class ReferenceImage
{
    /// <summary>raw の参照を読むフォーマット(先頭の1フレーム)。</summary>
    /// <remarks>
    /// 参照の raw はディスク上のファイルなので、表示中の raw ファイルを読んだフォーマット(ビット深度・詰め・
    /// エンディアン・ヘッダ・寸法)で読む。ビニング・フィルタの結果は 16bit・下詰め・リトルエンディアン・
    /// ヘッダ0(寸法もビニングで縮む)の形式に置き換わるので、その形式で読むと下詰めNbitのファイルは正規化
    /// (&lt;&lt;(16−N))されずに 1/2^(16−N) の値になり、ビッグエンディアンではバイトが入れ替わり、ビニング後は
    /// ファイルの先頭だけを縮んだ寸法として読んでしまう。画素は内部では常に正規化した16bitなので、ファイルの
    /// 形式で読めば処理結果と同じ値域で演算でき、寸法の違い(ビニング後)は演算の寸法検査で明示的に断られる。
    /// Bayer は右パネルで変えた表示中のものを使う(読み込む画素値には影響しない)。
    /// raw 以外(TIFF 等)を表示中なら、従来どおり表示中の形式で読む。
    /// </remarks>
    /// <param name="current">表示中の画像のフォーマット(処理結果ならその結果のもの)。</param>
    /// <param name="openedRaw">表示中の raw ファイルを読んだフォーマット(処理で変わる前)。raw 以外ならnull。</param>
    /// <returns>参照ファイルの解釈に使うフォーマット。</returns>
    internal static RawFormat RawReadFormat(RawFormat current, RawFormat? openedRaw)
    {
        return (openedRaw ?? current) with { FrameCount = 1, Bayer = current.Bayer };
    }

    /// <summary>raw の参照ファイルに期待するバイト数(ヘッダ+読むフレーム)。</summary>
    /// <param name="readFormat"><see cref="RawReadFormat"/> が返したフォーマット。</param>
    /// <returns>期待するバイト数。</returns>
    internal static long ExpectedRawSize(RawFormat readFormat)
    {
        return readFormat.HeaderOffset + readFormat.FrameSizeInBytes;
    }
}
