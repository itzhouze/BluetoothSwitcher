using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace BluetoothSwitcher.UI;

internal static class DarkTitleBar
{
    /// <summary>Dunkle Titelleiste (Windows 10 20H1+ / Windows 11).</summary>
    public static unsafe void Apply(IntPtr hwnd)
    {
        BOOL dark = true;
        PInvoke.DwmSetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, (uint)sizeof(BOOL));
    }
}
