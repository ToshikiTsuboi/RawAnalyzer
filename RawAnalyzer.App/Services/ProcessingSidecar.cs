using RawAnalyzer.App.Views;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 保存の付随テキスト(保存した画像に何が適用されたかの記録。MainWindow の WriteProcessingSidecar が書く)の判断。
/// </summary>
internal static class ProcessingSidecar
{
    /// <summary>
    /// マルチフレームの画像の1フレームだけを書き出したときの「元画像のフレーム: k/N」の行。
    /// </summary>
    /// <remarks>
    /// TIFF/PNG/JPEG・float raw は表示中の1フレームだけを書き出す。どのフレームかを書かないと、同じ raw から別の
    /// フレームを保存したファイル同士を後から区別できない。raw 形式は全フレームを書き出すので書かない。TIFF スタックは
    /// 「元TIFFのページ」を書く(各ページが1枚の画像)。HDR派生ビューは派生画像が1フレームで、元にしたフレームは
    /// [HDR派生ビュー]に書く(<see cref="HdrViewSidecar"/>)。
    /// </remarks>
    /// <param name="frameCount">保存した画像のフレーム数。</param>
    /// <param name="frame">保存したフレーム番号(0始まり)。</param>
    /// <param name="tiffStack">TIFFスタックのページか。</param>
    /// <param name="format">保存形式。</param>
    /// <returns>行(改行なし)。書かないときは null。</returns>
    internal static string? SourceFrameLine(int frameCount, int frame, bool tiffStack, SaveFormat format)
    {
        return frameCount > 1 && !tiffStack && format != SaveFormat.Raw
            ? $"元画像のフレーム: {frame + 1}/{frameCount}"
            : null;
    }
}
