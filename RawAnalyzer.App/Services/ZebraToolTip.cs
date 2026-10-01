using RawAnalyzer.App.Rendering;

namespace RawAnalyzer.App.Services;

/// <summary>
/// ツールバーのゼブラのトグルのツールチップ。
/// </summary>
/// <remarks>
/// ゼブラはグレーの描画(Raw 表示・チャネル分割。HDR の分割・合成のグレー表示を含む)とカラー画像の RGB 表示で
/// だけ描き、Bayer カラー・カラー現像の表示では描かない(<see cref="ViewportRenderer"/>)。トグルは無効にせず
/// (ON のまま表示モードを戻せば描く)、描かない表示ではツールチップの先頭にその理由を出す。
/// </remarks>
internal static class ZebraToolTip
{
    private const string Description = "飽和(≥98%)を赤 / 黒潰れ(≤2%)を青のストライプで警告";
    private const string Scope = "Raw・チャネル分割・カラー画像の表示のみ";

    /// <summary>その表示モードでゼブラを描くか。</summary>
    /// <param name="mode">ビューポートの表示モード。</param>
    /// <returns>描くならtrue。Bayer カラー・カラー現像ではfalse。</returns>
    internal static bool IsDrawn(ViewportDisplayMode mode)
    {
        return mode is not (ViewportDisplayMode.BayerColor or ViewportDisplayMode.ColorDevelop);
    }

    /// <summary>その表示モードでのトグルのツールチップ。</summary>
    /// <param name="mode">ビューポートの表示モード。</param>
    /// <returns>ツールチップの文。描かない表示では先頭に理由を置く。</returns>
    internal static string For(ViewportDisplayMode mode)
    {
        if (IsDrawn(mode))
        {
            return $"{Description}({Scope})";
        }

        string name = mode == ViewportDisplayMode.BayerColor ? "Bayerカラー" : "カラー現像";
        return $"{name}の表示ではゼブラを描きません({Scope})。\n{Description}";
    }
}
