using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RawViewer.App.Native;

/// <summary>
/// DWM(Desktop Window Manager)によるウィンドウタイトルバーのダークテーマ適用。
/// </summary>
internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    // COLORREF (0x00BBGGRR)
    private const int CaptionColor = 0x00322E2E;   // #2E2E32
    private const int CaptionTextColor = 0x00D4D8D8; // #D8D8D4

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// ウィンドウのタイトルバーへダークテーマ(アプリのパネル色)を適用する。
    /// 非対応OSでは何もしない。
    /// </summary>
    /// <param name="window">対象ウィンドウ。</param>
    public static void Apply(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int dark = 1;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        int caption = CaptionColor;
        _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
        int text = CaptionTextColor;
        _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
    }
}
