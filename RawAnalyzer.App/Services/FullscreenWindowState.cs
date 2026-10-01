using System.Windows;

namespace RawAnalyzer.App.Services;

/// <summary>
/// フルスクリーンとウィンドウの状態(最大化)の対応。フルスクリーンは枠を消したウィンドウを最大化して作る。
/// </summary>
internal static class FullscreenWindowState
{
    /// <summary>終了時にセッションへ保存する「最大化」を決める。</summary>
    /// <remarks>
    /// フルスクリーン中はウィンドウが最大化されているが、それはフルスクリーンのための最大化なので、フルスクリーンに
    /// 入る前の状態を保存する(次回はフルスクリーンにせずに起動する)。以前は今の状態をそのまま保存し、
    /// 通常のウィンドウから F11 を押してそのまま終了すると、次回は最大化で起動した。
    /// </remarks>
    /// <param name="current">今のウィンドウの状態。</param>
    /// <param name="fullscreen">フルスクリーン中か。</param>
    /// <param name="beforeFullscreen">フルスクリーンに入る前のウィンドウの状態。</param>
    /// <returns>最大化として保存するなら true。</returns>
    internal static bool IsMaximizedToSave(WindowState current, bool fullscreen, WindowState beforeFullscreen) =>
        (fullscreen ? beforeFullscreen : current) == WindowState.Maximized;
}
