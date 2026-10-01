using System.Windows;
using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>フルスクリーンとウィンドウの状態(FullscreenWindowState)の検証。</summary>
public class FullscreenWindowStateTests
{
    [Theory]
    [InlineData(WindowState.Normal, false)]
    [InlineData(WindowState.Maximized, true)]
    public void FullscreenSavesTheStateBeforeFullscreen(WindowState before, bool expected)
    {
        // フルスクリーンはウィンドウを最大化して作る。以前はフルスクリーンのまま終了すると最大化として保存し、
        // 元が通常のウィンドウでも次回は最大化で起動した
        Assert.Equal(expected, FullscreenWindowState.IsMaximizedToSave(
            WindowState.Maximized, fullscreen: true, beforeFullscreen: before));
    }

    [Theory]
    [InlineData(WindowState.Normal, false)]
    [InlineData(WindowState.Maximized, true)]
    [InlineData(WindowState.Minimized, false)]
    public void OutsideFullscreen_SavesTheCurrentState(WindowState current, bool expected)
    {
        // フルスクリーンに入る前の状態は、フルスクリーンでなければ使わない(前に入ったフルスクリーンの名残を見ない)
        Assert.Equal(expected, FullscreenWindowState.IsMaximizedToSave(
            current, fullscreen: false, beforeFullscreen: WindowState.Maximized));
    }

    [Fact]
    public void LeavingFullscreen_MaximizesAgainFromNormal()
    {
        // 枠のない状態で最大化した矩形は画面全体(タスクバーの領域を含む)。元が最大化なら WindowState は変わらず、
        // 枠を戻しても最大化の矩形が作業領域に戻らなかった(ステータスバーがタスクバーに隠れる)。入るときと同じく、
        // 一度通常に戻してから最大化し直す
        Assert.Equal(new[] { WindowState.Normal, WindowState.Maximized },
            FullscreenWindowState.StatesOnLeaving(WindowState.Maximized));

        // 元が通常のウィンドウなら通常へ戻すだけ
        Assert.Equal(new[] { WindowState.Normal }, FullscreenWindowState.StatesOnLeaving(WindowState.Normal));
    }
}
