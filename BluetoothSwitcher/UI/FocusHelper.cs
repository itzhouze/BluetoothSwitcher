using BluetoothSwitcher.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace BluetoothSwitcher.UI;

/// <summary>Merkt sich das Vordergrundfenster (Harbor) und gibt ihm später den Fokus zurück.</summary>
internal static class FocusHelper
{
    public static unsafe IntPtr Capture() => (IntPtr)PInvoke.GetForegroundWindow().Value;

    /// <summary>Holt ein Fenster in den Vordergrund, notfalls über AttachThreadInput.</summary>
    public static unsafe bool Activate(IntPtr hwnd)
    {
        var target = new HWND(hwnd);
        if (hwnd == IntPtr.Zero || !PInvoke.IsWindow(target))
            return false;

        if (PInvoke.SetForegroundWindow(target) && PInvoke.GetForegroundWindow() == target)
            return true;

        var foreground = PInvoke.GetForegroundWindow();
        var foregroundThread = PInvoke.GetWindowThreadProcessId(foreground, null);
        var currentThread = PInvoke.GetCurrentThreadId();
        var attached = foregroundThread != currentThread && PInvoke.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            PInvoke.BringWindowToTop(target);
            PInvoke.SetForegroundWindow(target);
        }
        finally
        {
            if (attached)
                PInvoke.AttachThreadInput(currentThread, foregroundThread, false);
        }

        var ok = PInvoke.GetForegroundWindow() == target;
        if (!ok)
            Log.Warn($"Fenster 0x{hwnd:X} konnte nicht in den Vordergrund geholt werden");
        return ok;
    }
}
