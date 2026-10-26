using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MicBoost.App.Services;

internal static class NativeWindow
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;

    /// <summary>Gives the standard title bar a dark look on Windows 10 20H1+ / Windows 11.</summary>
    public static void UseDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            int on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            int color = 0x00121110; // COLORREF 0x00BBGGRR: #101112
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref color, sizeof(int));
        }
        catch (Exception) { /* older Windows: keep the default title bar */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
