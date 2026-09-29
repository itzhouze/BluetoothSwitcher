using BluetoothSwitcher.Logging;
using Microsoft.Win32;

namespace BluetoothSwitcher.Startup;

/// <summary>Autostart über HKCU\...\Run (keine Admin-Rechte nötig).</summary>
internal static class AutostartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BTSwitcher";

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value &&
                   value.Trim('"').Equals(ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{ExePath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Info($"Autostart {(enabled ? "aktiviert" : "deaktiviert")}: {ExePath}");
    }
}
