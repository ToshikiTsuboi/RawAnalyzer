namespace RawAnalyzer.App.Services;

/// <summary>
/// HDR派生ビュー(分割・合成)の元にした元画像のフレーム番号を控え、
/// Raw表示へ戻るときに表示し直すフレームを決める。
/// </summary>
/// <remarks>
/// 派生ビューの表示中はビューポートのフレーム番号が派生画像(常に0)を指すため、
/// 元画像のどのフレームから作ったかを別に持つ。
/// 行交互HDRは1フレームが全露光を含む1回の撮影なので、表示中のフレームを分割し、
/// Raw表示へ戻るときもそのフレームを表示し直す(先頭へ戻すと別時刻の撮影を見せてしまう)。
/// フレーム連結HDRはフレームそのものが各露光で、分割・合成には全フレームを使う。
/// 戻り先は行交互と同じく「HDR表示に入る直前に表示していたフレーム」とし、
/// HDR表示を挟んでも元の表示とシーケンスUIの位置が変わらないようにする。
/// </remarks>
internal sealed class HdrSourceFrame
{
    /// <summary>控えている元画像のフレーム番号。</summary>
    internal int Frame { get; private set; }

    /// <summary>
    /// 分割・合成を始めるときに、元にする元画像のフレームを決めて控える。
    /// </summary>
    /// <param name="viewportFrame">ビューポートの表示フレーム。</param>
    /// <param name="derivedViewShown">
    /// 派生ビューを表示中か。表示中(分割⇔合成の切替)はビューポートが派生画像を指すので、
    /// 控えているフレームを引き継ぐ。
    /// </param>
    /// <returns>元画像のフレーム番号。</returns>
    internal int Capture(int viewportFrame, bool derivedViewShown)
    {
        if (!derivedViewShown)
        {
            Frame = viewportFrame;
        }

        return Frame;
    }

    /// <summary>
    /// 分割・合成の結果を適用する直前に、計算の元にしたフレームがいまも元画像のフレームかを確かめる。
    /// </summary>
    /// <remarks>
    /// 表示中の元画像のフレーム(派生ビューの表示中は控えているフレーム)であり、かつRaw表示へ戻るときの
    /// 戻り先(控えているフレーム)でもあること。どちらかが替わっていたら、結果は表示中の撮影のものではないか、
    /// Raw表示へ戻ったときに別のフレームを見せることになる。
    /// </remarks>
    /// <param name="sourceFrame">計算を始めたときに <see cref="Capture"/> が返したフレーム。</param>
    /// <param name="viewportFrame">ビューポートの表示フレーム。</param>
    /// <param name="derivedViewShown">
    /// 派生ビューを表示中か。表示中はビューポートが派生画像を指すので、控えているフレームだけで判定する。
    /// </param>
    /// <returns>いまも元画像のフレームならtrue。</returns>
    internal bool IsCurrent(int sourceFrame, int viewportFrame, bool derivedViewShown)
    {
        return Frame == sourceFrame && (derivedViewShown || viewportFrame == sourceFrame);
    }

    /// <summary>
    /// Raw表示へ戻るときに表示する元画像のフレーム番号を返す。
    /// </summary>
    /// <param name="frameCount">元画像のフレーム数。</param>
    /// <returns>控えたフレーム。元画像の範囲外なら先頭フレーム(0)。</returns>
    internal int ResolveRestoreFrame(int frameCount)
    {
        return (uint)Frame < (uint)frameCount ? Frame : 0;
    }
}
