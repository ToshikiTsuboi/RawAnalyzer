using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// HDR分割・合成の計算結果で、表示中の画像を派生ビューへ差し替えてよいか判定する。
/// </summary>
/// <remarks>
/// <para>
/// 分割・合成は数秒かかり(モーダルではない)、その間も表示モードは操作できる。元画像の差し替えだけでなく
/// モード変更も見ないと、利用者が選び直した表示を計算結果が後から上書きしてしまう。
/// </para>
/// <para>
/// 計算を始めたときのフォーマット(右パネルの Bayer と、Bayer で決まる行交互の既定の行単位など、派生ビューに
/// 効く値)とも照合する。計算中は右パネルの Bayer を選べなくしている(<see cref="BayerEditAvailability"/>)ので
/// 通常は替わらないが、替わっていたら結果は右パネルのフォーマットと食い違う(以前は照合せず、開始時のパターンで
/// 作った派生ビューをそのまま表示した)。
/// </para>
/// </remarks>
internal static class HdrViewReplacement
{
    /// <summary>計算結果で派生ビューへ差し替えない理由。</summary>
    internal enum Refusal
    {
        /// <summary>差し替えてよい。</summary>
        None,

        /// <summary>
        /// 表示モードを選び直した、または元画像(フレーム)が替わった。選び直した側・差し替えた側が表示しているので、
        /// 結果は黙って捨てる。
        /// </summary>
        Superseded,

        /// <summary>
        /// 計算を始めたときとフォーマットが替わった。結果を捨て、理由を知らせて表示モードを Raw 表示へ戻す
        /// (表示モードの選択は分割・合成のまま、表示は計算前のままになっているため)。
        /// </summary>
        FormatChanged,
    }

    /// <summary>計算結果で派生ビューへ差し替えてよいか判定する。</summary>
    /// <remarks>
    /// 選び直し・元画像の差し替えを先に見る。フォーマットの違いを優先して Raw 表示へ戻すと、利用者が選び直した
    /// 表示を上書きしてしまう。
    /// </remarks>
    /// <param name="modeReselected">計算中に表示モードを選び直したか(選択が開始時の分割・合成でなくなった)。</param>
    /// <param name="sourceReplaced">計算元の元画像・フレームが、いまの元画像・フレームでなくなったか。</param>
    /// <param name="sourceFormat">計算に使ったフォーマット(計算を始めたときの元画像のフォーマット)。</param>
    /// <param name="currentFormat">いまの元画像のフォーマット(右パネルの Bayer を含む)。</param>
    /// <returns>差し替えない理由。差し替えてよければ <see cref="Refusal.None"/>。</returns>
    internal static Refusal Check(
        bool modeReselected, bool sourceReplaced, RawFormat sourceFormat, RawFormat? currentFormat)
    {
        if (modeReselected || sourceReplaced)
        {
            return Refusal.Superseded;
        }

        // 値で比べる(替えて同じ値へ戻したなら、結果は右パネルのフォーマットと一致する)
        return sourceFormat == currentFormat ? Refusal.None : Refusal.FormatChanged;
    }

    /// <summary>差し替えなかった理由を、利用者に知らせる文にする。</summary>
    /// <param name="refusal">差し替えない理由。</param>
    /// <param name="operation">操作名(「HDR分割」「HDR合成」)。</param>
    /// <returns>メッセージボックスに出す文。知らせない理由(<see cref="Refusal.Superseded"/> など)は空文字列。</returns>
    internal static string Explain(Refusal refusal, string operation) => refusal switch
    {
        Refusal.FormatChanged =>
            $"{operation}の計算中に Bayer パターンなどのフォーマットが変わったため、計算結果を表示しませんでした。\n" +
            $"表示モードを Raw表示に戻しました。もう一度{operation}を選んでください。",
        _ => "",
    };
}
