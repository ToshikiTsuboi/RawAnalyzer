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

    /// <summary>フルスクリーンを抜けるときに(枠を戻した後で)順に設定する WindowState を決める。</summary>
    /// <remarks>
    /// 枠のない状態で最大化した矩形は画面全体(タスクバーの領域を含む)になる。元が最大化なら WindowState は
    /// 最大化のまま変わらないので、そのまま枠を戻しても最大化の矩形が作業領域に戻らず、ウィンドウの下端
    /// (ステータスバー)がタスクバーと重なったままになる。入るときと同じく一度通常へ戻してから最大化し直す。
    /// </remarks>
    /// <param name="beforeFullscreen">フルスクリーンに入る前のウィンドウの状態。</param>
    /// <returns>順に設定する状態(最後が戻した後の状態)。</returns>
    internal static IReadOnlyList<WindowState> StatesOnLeaving(WindowState beforeFullscreen) =>
        beforeFullscreen == WindowState.Maximized
            ? new[] { WindowState.Normal, WindowState.Maximized }
            : new[] { beforeFullscreen };
}
