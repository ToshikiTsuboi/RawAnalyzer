using System.Windows;
using System.Windows.Input;

namespace RawAnalyzer.App.Services;

/// <summary>ウィンドウの Esc で、ビューポートの操作やフルスクリーンを解除する。</summary>
/// <remarks>
/// ドロップダウンなどフォーカス中のコントロールが使えるよう、バブルの KeyDown で処理する。
/// ダイアログ内のキーイベントは所有ウィンドウへ伝播しない。
/// </remarks>
internal static class FullscreenEscape
{
    /// <summary>ウィンドウに Esc での解除操作を付ける。</summary>
    /// <param name="window">ウィンドウ(キー入力のルート)。</param>
    /// <param name="isFullscreen">フルスクリーン中か。</param>
    /// <param name="exitFullscreen">フルスクリーンを解除する処理。</param>
    /// <param name="cancelViewportInteraction">カーソル・ツールの順に解除し、解除したら true を返す処理。</param>
    internal static void Attach(UIElement window, Func<bool> isFullscreen, Action exitFullscreen,
        Func<bool> cancelViewportInteraction)
    {
        window.KeyDown += (_, e) =>
        {
            if (e.Handled || e.Key != Key.Escape)
            {
                return;
            }

            if (cancelViewportInteraction())
            {
                e.Handled = true;
            }
            else if (isFullscreen())
            {
                exitFullscreen();
                e.Handled = true;
            }
        };
    }
}
