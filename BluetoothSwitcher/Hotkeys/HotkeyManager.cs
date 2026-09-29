using System.Runtime.InteropServices;
using BluetoothSwitcher.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace BluetoothSwitcher.Hotkeys;

/// <summary>Registriert einen systemweiten Hotkey über RegisterHotKey (funktioniert auch über Fullscreen-Apps).</summary>
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int HotkeyId = 0xB710;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private bool _registered;

    public event Action? Pressed;

    public Hotkey? Current { get; private set; }

    public HotkeyManager()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE, Caption = "BTSwitcher.Hotkey" });
    }

    /// <summary>Registriert den Hotkey (ersetzt einen vorherigen).</summary>
    /// <returns>false, wenn die Kombination bereits von einem anderen Programm belegt ist.</returns>
    public bool Register(Hotkey hotkey)
    {
        Unregister();

        var mods = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(Keys.Control)) mods |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        if (hotkey.Modifiers.HasFlag(Keys.Alt)) mods |= HOT_KEY_MODIFIERS.MOD_ALT;
        if (hotkey.Modifiers.HasFlag(Keys.Shift)) mods |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        if (hotkey.Modifiers.HasFlag(Keys.LWin)) mods |= HOT_KEY_MODIFIERS.MOD_WIN;

        if (!PInvoke.RegisterHotKey((HWND)Handle, HotkeyId, mods, (uint)hotkey.Key))
        {
            Log.Warn($"Hotkey {hotkey} konnte nicht registriert werden (Win32-Fehler {Marshal.GetLastPInvokeError()}) – vermutlich belegt.");
            return false;
        }

        _registered = true;
        Current = hotkey;
        Log.Info($"Hotkey {hotkey} registriert");
        return true;
    }

    public void Unregister()
    {
        if (!_registered)
            return;
        PInvoke.UnregisterHotKey((HWND)Handle, HotkeyId);
        _registered = false;
        Current = null;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)PInvoke.WM_HOTKEY && m.WParam == HotkeyId)
        {
            Pressed?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
