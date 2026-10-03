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

    /// <summary>
    /// 派生ビューへ差し替える直前(旧画像を読んでいる描画の停止を待った後)に、計算結果をどうするか。
    /// </summary>
    internal enum Adoption
    {
        /// <summary>派生ビューへ差し替える。</summary>
        Adopt,

        /// <summary>
        /// 結果を捨てる。表示は元画像を差し替えた側・選び直した表示へ戻す側(Raw表示への復帰)が行う。
        /// </summary>
        Discard,

        /// <summary>
        /// 結果を捨て、描画の停止のためにビューポートから外した元画像を、いま選ばれている表示モードで表示し直す。
        /// </summary>
        DiscardAndShowSource,

        /// <summary>
        /// 結果を捨て、描画の停止のためにビューポートから外した表示中の合成ビューを、いま選ばれている表示モードで
        /// 表示し直す。
        /// </summary>
        DiscardAndShowMergedView,
    }

    /// <summary>
    /// 派生ビューへ差し替える直前(旧画像を読んでいる描画の停止を待った後)に、計算結果をどうするか判定する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 差し替えの前に旧画像(元画像・前の派生ビュー)をビューポートから外して描画の停止を待つ。その間も表示モードは
    /// 選び直せるので、元画像の差し替えに加えて選び直しも見る。以前は停止を待った後に元画像・フレームしか照合せず、
    /// 待つ間に Raw表示を選び直しても、選択は Raw表示のまま派生ビューを表示した(保存・解析の対象にもなった)。
    /// </para>
    /// <para>
    /// 選び直したら結果は捨て、選び直した表示を誰が表示するかで分ける。元画像が替わっていれば差し替えた側が、
    /// ビューポートに画像が入っていれば(Raw表示への復帰が済んでいる)その側が表示している。派生ビューがなければ
    /// 選び直した側はビューポートの表示モードだけを替える(外された元画像を表示し直さない)ので、元画像を表示し直す。
    /// 派生ビューの表示中は、合成ビューの表示(Bayer系・合成ビューの Raw表示)を選び直したなら合成ビューのまま
    /// 表示モードだけを替えるので合成ビューを表示し直し、それ以外の選び直しは Raw表示への復帰が派生ビューを捨てて
    /// 元画像を表示し直す(待っている復帰より先に表示し直すと、復帰が捨てる派生ビューをビューポートへ入れてしまう)。
    /// </para>
    /// </remarks>
    /// <param name="sourceReplaced">計算元の元画像・フレームが、いまの元画像・フレームでなくなったか。</param>
    /// <param name="selectedIndex">
    /// いまの表示モードの選択(0=Raw表示, 1=Bayerカラー, 2=カラー現像, 3=チャネル分割, 4=HDR分割, 5=HDR合成)。
    /// </param>
    /// <param name="computedIndex">計算した表示モード(分割=4、合成=5)。</param>
    /// <param name="viewportEmpty">ビューポートが画像を表示していないか(描画の停止のために外したままか)。</param>
    /// <param name="derivedViewShown">差し替える前の派生ビュー(分割・合成ビュー)を表示しているか。</param>
    /// <param name="mergedViewShown">差し替える前の派生ビューが合成ビューか。</param>
    /// <returns>計算結果をどうするか。</returns>
    internal static Adoption CheckAdoption(
        bool sourceReplaced, int selectedIndex, int computedIndex,
        bool viewportEmpty, bool derivedViewShown, bool mergedViewShown)
    {
        if (sourceReplaced)
        {
            return Adoption.Discard;
        }

        if (selectedIndex == computedIndex)
        {
            return Adoption.Adopt;
        }

        if (!viewportEmpty)
        {
            return Adoption.Discard;
        }

        if (!derivedViewShown)
        {
            return Adoption.DiscardAndShowSource;
        }

        return mergedViewShown && selectedIndex is 1 or 2 or 3 or 5
            ? Adoption.DiscardAndShowMergedView
            : Adoption.Discard;
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
