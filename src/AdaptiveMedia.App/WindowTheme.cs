using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AdaptiveMedia;

/// <summary>Gives each window a dark native title bar that matches the app bar, so
/// the frame does not flash a light caption around a dark interface. Windows 10
/// honours the dark-mode attribute; Windows 11 also takes the exact caption colour.
/// Both calls are best-effort: an unsupported attribute leaves the default frame.</summary>
internal static class WindowTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int CaptionColorRef = 0x001E1A15; // #151A1E as COLORREF (0x00BBGGRR)

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void UseDarkFrame(Window window) => window.SourceInitialized += (_, _) =>
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int enabled = 1, caption = CaptionColorRef;
        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
    };
}
