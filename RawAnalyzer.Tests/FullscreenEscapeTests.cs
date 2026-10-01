using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RawAnalyzer.App.Services;
using Xunit;

namespace RawAnalyzer.Tests;

/// <summary>フルスクリーン中の Esc(FullscreenEscape)の検証。</summary>
[Collection("WPF UI")]
public class FullscreenEscapeTests
{
    [Fact]
    public Task Escape_ClearsThePixelCursorBeforeLeavingFullscreen() => WpfTestHost.Run(() =>
    {
        // ビューポートは Esc で画素カーソルを消す(KeyDown で処理済みにする)。以前はウィンドウの PreviewKeyDown
        // (ルートが最初)でフルスクリーンを解除していたため、画素カーソルを消す前にフルスクリーンが解除され、
        // カーソルは残った(ショートカット一覧の「Esc: 画素カーソルを消す」と食い違った)
        var viewport = new EscapeConsumer { HasCursor = true };
        var window = new Border { Child = viewport };
        bool fullscreen = true;
        FullscreenEscape.Attach(window, () => fullscreen, () => fullscreen = false);

        PressEscape(viewport);
        Assert.False(viewport.HasCursor);
        Assert.True(fullscreen);

        // もう一度押すと(消すカーソルがないので)フルスクリーンを解除する
        PressEscape(viewport);
        Assert.False(fullscreen);
    });

    [Fact]
    public Task Escape_DoesNothingOutsideFullscreen() => WpfTestHost.Run(() =>
    {
        var viewport = new EscapeConsumer();
        var window = new Border { Child = viewport };
        int exits = 0;
        FullscreenEscape.Attach(window, () => false, () => exits++);

        KeyEventArgs args = PressEscape(viewport);
        Assert.Equal(0, exits);
        Assert.False(args.Handled); // ほかの Esc の使い道を妨げない
    });

    /// <summary>
    /// 入力システムと同じく、PreviewKeyDown(トンネル)を流してから、その処理済みを引き継いだ KeyDown(バブル)を流す。
    /// </summary>
    private static KeyEventArgs PressEscape(UIElement target)
    {
        var source = new TestInputSource();
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        target.RaiseEvent(preview);

        var keyDown = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
            Handled = preview.Handled,
        };
        target.RaiseEvent(keyDown);
        return keyDown;
    }

    /// <summary>ImageViewport と同じく、表示中の画素カーソルがあれば Esc(KeyDown)で消して処理済みにする要素。</summary>
    private sealed class EscapeConsumer : FrameworkElement
    {
        public bool HasCursor { get; set; }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled)
            {
                return;
            }

            if (e.Key == Key.Escape && HasCursor)
            {
                HasCursor = false;
                e.Handled = true;
            }
        }
    }

    /// <summary>ウィンドウを表示せずにキー入力イベントを作るための入力元。</summary>
    private sealed class TestInputSource : PresentationSource
    {
        private Visual? _root;

        public override Visual RootVisual
        {
            get => _root!;
            set => _root = value;
        }

        public override bool IsDisposed => false;

        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
