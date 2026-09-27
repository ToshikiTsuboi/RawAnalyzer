using RawAnalyzer.App.Rendering;
using RawAnalyzer.Core;

namespace RawAnalyzer.App.Services;

/// <summary>
/// ツールバーの表示モード選択(DisplayModeCombo)とビューポートの表示モードの対応。
/// </summary>
/// <remarks>
/// 選択肢の並びは MainWindow.xaml と同じ
/// (0=Raw表示, 1=Bayerカラー, 2=カラー現像, 3=チャネル分割, 4=HDR分割, 5=HDR合成)。
/// </remarks>
internal static class DisplayModeSelection
{
    /// <summary>
    /// 新しい画像に適用する表示モード。
    /// </summary>
    /// <param name="ComboIndex">表示モード選択の項目番号。</param>
    /// <param name="ComboEnabled">表示モード選択を操作できるか(カラー画像では選ばせない)。</param>
    /// <param name="ViewportMode">ビューポートの表示モード。</param>
    internal readonly record struct Choice(
        int ComboIndex, bool ComboEnabled, ViewportDisplayMode ViewportMode);

    /// <summary>
    /// 表示モード選択で選ばれたモードを、表示中の画像に使えない理由。
    /// </summary>
    internal enum Refusal
    {
        /// <summary>使える。</summary>
        None,

        /// <summary>デコード済みのカラー画像には Bayer 系の表示を使わない(RGB のまま表示する)。</summary>
        ColorImage,

        /// <summary>Bayer パターンがないため、Bayer カラー・カラー現像・チャネル分割が成立しない。</summary>
        NoBayer,
    }

    /// <summary>
    /// 表示モード選択で選ばれたモードの判定結果。
    /// </summary>
    /// <param name="ViewportMode">
    /// ビューポートの表示モード。使えないときは、選択を Raw 表示へ戻したときの表示。
    /// </param>
    /// <param name="Refusal">使えない理由。使えるときは <see cref="Refusal.None"/>。</param>
    internal readonly record struct Selected(ViewportDisplayMode ViewportMode, Refusal Refusal);

    /// <summary>
    /// 表示する画像を差し替えるとき(ファイル連番の送り・TIFF のページ送り・ビニングなどの処理結果)、
    /// 選択中の表示モードを新しい画像でも保てるなら保ち、成立しないモードだけを戻した結果を返す。
    /// </summary>
    /// <remarks>
    /// デコード済みのカラー画像は RGB のまま表示し、選択は Raw 表示で操作不可にする
    /// (ファイルを開いたときと同じ)。Bayer カラー・カラー現像・チャネル分割は Bayer パターンが
    /// なければ成立しない。HDR 分割・合成は元画像から作る派生ビューなので持ち越さない。
    /// 選択とビューポートを同じ結果にそろえ、表示と選択が食い違わないようにする。
    /// </remarks>
    /// <param name="selectedIndex">現在の表示モード選択の項目番号。</param>
    /// <param name="isColor">新しい画像がデコード済みのカラー画像か。</param>
    /// <param name="bayer">新しい画像のBayerパターン。</param>
    /// <returns>適用する表示モード。</returns>
    internal static Choice ForSequenceImage(int selectedIndex, bool isColor, BayerPattern bayer)
    {
        if (isColor)
        {
            return new Choice(0, ComboEnabled: false, ViewportDisplayMode.TrueColor);
        }

        int index = selectedIndex is >= 1 and <= 3 && bayer != BayerPattern.None
            ? selectedIndex
            : 0;
        return new Choice(index, ComboEnabled: true, ModeOf(index));
    }

    /// <summary>
    /// 表示モード選択(メニュー・ショートカットを含む)で Raw 表示・Bayer 系の表示が選ばれたとき、
    /// 表示中の画像に使えるか判定する。
    /// </summary>
    /// <remarks>
    /// カラー画像の Raw 表示(選択 0)は RGB のままの表示で、Bayer 系の表示は右パネルで Bayer を
    /// 指定していても使わない(<see cref="ForSequenceImage"/> と同じ規約。選択を戻した先もカラー表示)。
    /// HDR 分割・合成(4, 5)は派生ビューを作るので呼び出し側が先に扱う(ここでは Raw 表示と同じ扱い)。
    /// </remarks>
    /// <param name="selectedIndex">選ばれた表示モード選択の項目番号。</param>
    /// <param name="isColor">表示中の画像がデコード済みのカラー画像か。</param>
    /// <param name="bayer">表示中の画像のBayerパターン。</param>
    /// <returns>表示するモードと、使えないときはその理由。</returns>
    internal static Selected ForSelectedMode(int selectedIndex, bool isColor, BayerPattern bayer)
    {
        bool bayerMode = selectedIndex is >= 1 and <= 3;
        if (isColor)
        {
            return new Selected(
                ViewportDisplayMode.TrueColor, bayerMode ? Refusal.ColorImage : Refusal.None);
        }

        return bayerMode && bayer == BayerPattern.None
            ? new Selected(ViewportDisplayMode.Raw, Refusal.NoBayer)
            : new Selected(ModeOf(selectedIndex), Refusal.None);
    }

    /// <summary>グレー画像での項目番号に対応するビューポートの表示モード。</summary>
    private static ViewportDisplayMode ModeOf(int index) => index switch
    {
        1 => ViewportDisplayMode.BayerColor,
        2 => ViewportDisplayMode.ColorDevelop,
        3 => ViewportDisplayMode.ChannelSplit,
        _ => ViewportDisplayMode.Raw,
    };
}
