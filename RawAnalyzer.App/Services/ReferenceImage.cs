using System.Globalization;
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

    /// <summary>raw の参照ファイルの大きさが、対象の形式で読んでよい大きさか。</summary>
    /// <param name="actual">参照ファイルのバイト数。</param>
    /// <param name="expected">期待するバイト数(<see cref="ExpectedRawSize"/>)。</param>
    /// <param name="targetFileSize">対象(A)の raw ファイルのバイト数。raw でなければ0。</param>
    /// <returns>警告せずに読んでよければtrue。</returns>
    /// <remarks>
    /// 参照は先頭の1フレームだけを読む。1フレーム分のほか、対象の raw ファイルと同じ大きさ(同じファイル、
    /// 同じ形の連写ファイル)なら同じ形式で読める。1フレームの倍数を一律に許すと、幅2倍・同じ高さのような
    /// 別フォーマットのファイルまで無警告で誤読するので許さない(不足も超過も警告する)。
    /// </remarks>
    internal static bool IsExpectedRawSize(long actual, long expected, long targetFileSize)
    {
        return actual == expected || (targetFileSize > 0 && actual == targetFileSize);
    }

    /// <summary>サイズ不一致の案内に添える、期待するバイト数の説明。</summary>
    /// <param name="expected">期待するバイト数(<see cref="ExpectedRawSize"/>)。</param>
    /// <param name="targetFileSize">対象(A)の raw ファイルのバイト数。raw でなければ0。</param>
    /// <returns>「N バイト」または「N バイト(1フレーム)または M バイト(対象と同じ)」。</returns>
    internal static string DescribeExpectedRawSize(long expected, long targetFileSize)
    {
        return targetFileSize > 0 && targetFileSize != expected
            ? $"{expected:N0} バイト(1フレーム)または {targetFileSize:N0} バイト(対象Aのファイルと同じ)"
            : $"{expected:N0} バイト";
    }

    /// <summary>
    /// 参照画像を読み込む(raw は <paramref name="rawReadFormat"/> の先頭フレーム、画像ファイルは先頭ページの輝度)。
    /// </summary>
    /// <remarks>
    /// 32bit実数・32bit整数・符号あり・24/64bit・半精度の TIFF は、1ファイル(1ページ)ごとに自分の値域から
    /// 16bitコードへ写す。対象と参照の係数が違うと、同じ16bitのコードが別の値を表すので、差分・減算・比が
    /// 黙って誤る(ビット深度は同じ16なので寸法・ビット深度の検査では見分けられない)。係数が違えば読み込んだ
    /// 参照を破棄して理由を示して断る。raw と16bit以下の整数の画像ファイルは等倍(係数なし)として扱う。
    /// </remarks>
    /// <param name="path">参照画像のパス。</param>
    /// <param name="isRaw">raw ファイルとして読むか。</param>
    /// <param name="rawReadFormat">raw の参照を読むフォーマット(<see cref="RawReadFormat"/>)。</param>
    /// <param name="targetScaling">対象(A)の値の対応(32bit TIFF などを16bitへ写した係数)。等倍ならnull。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>参照画像(呼び出し側が破棄する)。</returns>
    /// <exception cref="InvalidOperationException">対象と参照の値の対応が違う場合。</exception>
    internal static RawImage Load(
        string path, bool isRaw, RawFormat rawReadFormat, SampleScaling? targetScaling,
        CancellationToken cancellationToken)
    {
        RawImage image;
        SampleScaling? scaling = null;
        if (isRaw)
        {
            image = RawLoader.Load(path, rawReadFormat, cancellationToken);
        }
        else
        {
            DecodedImage decoded = ImageFileLoader.Load(path, cancellationToken);
            image = decoded.Luminance;
            scaling = decoded.Scaling;
        }

        string? mismatch = ScalingMismatch(targetScaling, scaling);
        if (mismatch is not null)
        {
            image.Dispose();
            throw new InvalidOperationException(mismatch);
        }

        return image;
    }

    /// <summary>
    /// 対象と参照の値の対応(16bitコードが表す元の値)が違うなら、組み合わせられない理由を返す。
    /// </summary>
    /// <param name="target">対象(A)の係数。等倍ならnull。</param>
    /// <param name="reference">参照(B)の係数。等倍ならnull。</param>
    /// <returns>違うなら理由。同じならnull。</returns>
    internal static string? ScalingMismatch(SampleScaling? target, SampleScaling? reference)
    {
        // 係数は両端の値(コード0とコード65535が表す値)で決まる。値域の内訳(最小・非数の数)は問わない
        bool same = target is null
            ? reference is null
            : reference is not null && target.Offset == reference.Offset && target.Upper == reference.Upper;
        if (same)
        {
            return null;
        }

        return $"参照画像の値の対応({Describe(reference)})が対象({Describe(target)})と違うため、" +
            "同じ16bitのコードが別の値を表し、2枚を組み合わせた結果が正しくなりません。\n" +
            "32bit実数などのTIFFは1枚(1ページ)ごとの値域で16bitへ写します。値域の上限・下限が同じ2枚" +
            "(またはraw・16bit以下の整数の画像同士)を指定してください。";
    }

    private static string Describe(SampleScaling? scaling)
    {
        if (scaling is null)
        {
            return "等倍のコード";
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        return $"値 {scaling.Offset.ToString("G6", culture)}〜{scaling.Upper.ToString("G6", culture)} → 16bit";
    }
}
