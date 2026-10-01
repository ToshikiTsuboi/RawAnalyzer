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
}
