using System.Windows;
using System.Windows.Input;

namespace RawAnalyzer.App.Services;

/// <summary>フルスクリーンを Esc で解除する。</summary>
/// <remarks>
/// ウィンドウの KeyDown(バブル。フォーカス中のコントロールの後)で、そのコントロールが Esc を使わなかったときだけ
/// 解除する。ビューポートは Esc で画素カーソルを消して処理済みにし、開いたドロップダウンは Esc で閉じる。
/// 以前はウィンドウの PreviewKeyDown(トンネル。ルートが最初)で解除していたため、これらより先にフルスクリーンが
/// 解除され、画素カーソルは残った(「Esc は画素カーソルの解除を優先する」という意図・ショートカット一覧と逆)。
/// </remarks>
internal static class FullscreenEscape
{
    /// <summary>ウィンドウに Esc でのフルスクリーン解除を付ける。</summary>
    /// <param name="window">ウィンドウ(キー入力のルート)。</param>
    /// <param name="isFullscreen">フルスクリーン中か。</param>
    /// <param name="exitFullscreen">フルスクリーンを解除する処理。</param>
    internal static void Attach(UIElement window, Func<bool> isFullscreen, Action exitFullscreen)
    {
        window.KeyDown += (_, e) =>
        {
            if (!e.Handled && e.Key == Key.Escape && isFullscreen())
            {
                exitFullscreen();
                e.Handled = true;
            }
        };
    }
}
