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

        /// <summary>
        /// HDR 方式の指定がないため、HDR 分割・合成が成立しない(raw は「フォーマット変更…」で指定できる)。
        /// </summary>
        NoHdr,

        /// <summary>画像ファイル(TIFF 等)には HDR 方式を指定できないため、HDR 分割・合成を使えない。</summary>
        HdrOnImageFile,

        /// <summary>デコード済みのカラー画像には HDR 分割・合成を使わない(RGB のまま表示する)。</summary>
        HdrOnColorImage,
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
    /// HDR 分割・合成(4, 5)は派生ビューを作るので、呼び出し側が先に <see cref="ForHdrMode"/> で扱う
    /// (ここでは Raw 表示と同じ扱い)。
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

    /// <summary>
    /// 表示モード選択(メニュー・ショートカットを含む)で HDR 分割・合成(4, 5)が選ばれたとき、
    /// 表示中の画像に使えるか判定する。
    /// </summary>
    /// <remarks>
    /// HDR 方式は raw の読み込みダイアログ(「フォーマット変更…」で開き直すときも同じ)でしか指定できない。
    /// 画像ファイル(TIFF 等)は HDR 方式を持たず、「フォーマット変更…」も raw 以外では何もしないので、
    /// そこから設定するよう案内しない。デコード済みのカラー画像は RGB のまま表示する
    /// (<see cref="ForSelectedMode"/> と同じ規約)。
    /// </remarks>
    /// <param name="hdr">表示中の画像のフォーマットの HDR 方式。</param>
    /// <param name="isColor">表示中の画像がデコード済みのカラー画像か。</param>
    /// <param name="isRawFile">表示中の画像のファイルが raw(.raw/.bin)か。</param>
    /// <returns>使えない理由。使えるときは <see cref="Refusal.None"/>。</returns>
    internal static Refusal ForHdrMode(HdrMode hdr, bool isColor, bool isRawFile)
    {
        if (isColor)
        {
            return Refusal.HdrOnColorImage;
        }

        if (hdr != HdrMode.None)
        {
            return Refusal.None;
        }

        return isRawFile ? Refusal.NoHdr : Refusal.HdrOnImageFile;
    }

    /// <summary>
    /// 選ばれた表示モードを断った理由を、利用者に知らせる文にする。
    /// </summary>
    /// <param name="refusal">断った理由。</param>
    /// <returns>メッセージボックスに出す文。<see cref="Refusal.None"/> は空文字列。</returns>
    internal static string Explain(Refusal refusal) => refusal switch
    {
        Refusal.ColorImage =>
            "カラー画像(RGB)はカラーのまま表示します。\n" +
            "Bayerカラー・カラー現像・チャネル分割は、Bayer配列のRaw画像で使える表示です。",
        Refusal.NoBayer =>
            "この表示モードにはBayerパターンの指定が必要です。\n" +
            "右パネルの「フォーマット」→「Bayer」でパターン(RGGB等)を選択してください。",
        Refusal.NoHdr => "この表示モードにはHDR方式の指定が必要です(フォーマット変更…から設定)。",
        Refusal.HdrOnImageFile =>
            "画像ファイル(TIFF等)にはHDR方式を指定できません。\n" +
            "HDR分割・合成は、raw(.raw/.bin)を開くときにHDR方式を指定すると使えます" +
            "(この画像もrawで保存してから開き直せば指定できます)。",
        Refusal.HdrOnColorImage =>
            "カラー画像(RGB)はカラーのまま表示します。\n" +
            "HDR分割・合成は、HDR方式を指定して開いたraw(.raw/.bin)で使える表示です。",
        _ => "",
    };

    /// <summary>グレー画像での項目番号に対応するビューポートの表示モード。</summary>
    private static ViewportDisplayMode ModeOf(int index) => index switch
    {
        1 => ViewportDisplayMode.BayerColor,
        2 => ViewportDisplayMode.ColorDevelop,
        3 => ViewportDisplayMode.ChannelSplit,
        _ => ViewportDisplayMode.Raw,
    };
}
