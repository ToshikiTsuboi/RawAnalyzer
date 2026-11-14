using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using RawAnalyzer.App.Themes;

namespace RawAnalyzer.App.Native;

/// <summary>
/// DWM(Desktop Window Manager)によるウィンドウタイトルバーのダークテーマ適用。
/// </summary>
internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    // COLORREF (0x00BBGGRR)
    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

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
        int caption = ToColorRef(ThemeColors.Panel);
        _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
        int text = ToColorRef(ThemeColors.Text);
        _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
    }
}
