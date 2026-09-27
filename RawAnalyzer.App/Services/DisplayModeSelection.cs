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
    /// ファイル連番の送りで表示する新しい画像に対し、選択中の表示モードを保てるなら保ち、
    /// 成立しないモードだけを戻した結果を返す。
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
        return new Choice(index, ComboEnabled: true, index switch
        {
            1 => ViewportDisplayMode.BayerColor,
            2 => ViewportDisplayMode.ColorDevelop,
            3 => ViewportDisplayMode.ChannelSplit,
            _ => ViewportDisplayMode.Raw,
        });
    }
}
