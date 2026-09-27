using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// 処理結果(画像演算・欠陥補正・ビニング・フィルタ)で表示中の画像を差し替えてよいか判定する。
/// </summary>
/// <remarks>
/// <para>
/// 処理は開始時の元画像(Raw 表示の画像)から結果を作り、差し替えは元画像を破棄して結果に置き換える。
/// 処理は実行中の操作(HDR分割・合成の計算など)の間と HDR 表示の間は始めないが、結果を適用する時点で
/// 前提が崩れていたら差し替えない。
/// </para>
/// <para>
/// HDR分割・合成の派生ビューを表示している間に差し替えると、元画像だけが替わって派生ビュー
/// (ActiveImage。解析・保存の対象)が残り、表示と食い違う。元画像が別の画像へ替わっていたら、
/// 結果は表示中の画像を処理したものではない。
/// </para>
/// </remarks>
internal static class ProcessedImageReplacement
{
    /// <summary>処理結果で差し替えない理由。</summary>
    internal enum Refusal
    {
        /// <summary>差し替えてよい。</summary>
        None,

        /// <summary>HDR分割・合成の派生ビューを表示している。</summary>
        DerivedViewShown,

        /// <summary>元画像が処理の元にした画像ではなくなった。</summary>
        SourceReplaced,
    }

    /// <summary>処理結果で表示中の画像を差し替えてよいか判定する。</summary>
    /// <remarks>
    /// 派生ビューは元画像を差し替えないので、元画像が処理の元のままでも派生ビューの表示中は断る
    /// (元画像の一致だけでは見分けられない)。
    /// </remarks>
    /// <param name="source">処理の元にした画像(処理の開始時の元画像)。</param>
    /// <param name="current">いまの元画像。</param>
    /// <param name="derivedViewShown">HDR分割・合成の派生ビューを表示しているか。</param>
    /// <returns>差し替えない理由。差し替えてよければ <see cref="Refusal.None"/>。</returns>
    internal static Refusal Check(RawImage source, RawImage? current, bool derivedViewShown)
    {
        if (derivedViewShown)
        {
            return Refusal.DerivedViewShown;
        }

        return ReferenceEquals(source, current) ? Refusal.None : Refusal.SourceReplaced;
    }

    /// <summary>差し替えなかった理由を、利用者に知らせる文にする。</summary>
    /// <param name="refusal">差し替えない理由。</param>
    /// <param name="label">処理ラベル(「欠陥補正 3px (メディアン)」など)。</param>
    /// <returns>メッセージボックスに出す文。<see cref="Refusal.None"/> は空文字列。</returns>
    internal static string Explain(Refusal refusal, string label) => refusal switch
    {
        Refusal.DerivedViewShown =>
            $"処理中にHDR表示へ切り替わったため、処理結果({label})を適用しませんでした。\n" +
            "Raw表示に戻してから、もう一度実行してください。",
        Refusal.SourceReplaced =>
            $"処理中に表示中の画像が替わったため、処理結果({label})を適用しませんでした。\n" +
            "表示中の画像で、もう一度実行してください。",
        _ => "",
    };
}
