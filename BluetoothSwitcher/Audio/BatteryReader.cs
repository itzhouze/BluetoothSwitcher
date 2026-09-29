using System.Runtime.InteropServices;
using BluetoothSwitcher.Logging;
using BluetoothSwitcher.Native;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Devices.Properties;

namespace BluetoothSwitcher.Audio;

/// <summary>
/// Liest den Bluetooth-Akkustand (DEVPKEY_Bluetooth_Battery), den Windows auch in den
/// Einstellungen anzeigt. Zuordnung zum Audio-Endpoint über die ContainerId.
/// Nicht jedes Gerät liefert einen Wert (AirPods z. B. meist nicht).
/// </summary>
internal static class BatteryReader
{
    private static readonly string[] Enumerators = ["BTHENUM", "BTHLE", "BTHLEDEVICE"];

    /// <summary>ContainerId → Akkustand in Prozent.</summary>
    public static Dictionary<Guid, byte> ReadAll()
    {
        var result = new Dictionary<Guid, byte>();
        foreach (var enumerator in Enumerators)
        {
            try
            {
                ReadFrom(enumerator, result);
            }
            catch (Exception ex)
            {
                Log.Warn($"Akkustand ({enumerator}) nicht lesbar: {ex.Message}");
            }
        }
        return result;
    }

    private static unsafe void ReadFrom(string enumerator, Dictionary<Guid, byte> result)
    {
        using var set = PInvoke.SetupDiGetClassDevs(null, enumerator, default,
            SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_ALLCLASSES | SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT);
        if (set.IsInvalid)
            return;

        var info = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
        Span<byte> buffer = stackalloc byte[16];

        for (uint i = 0; PInvoke.SetupDiEnumDeviceInfo(set, i, ref info); i++)
        {
            if (!PInvoke.SetupDiGetDeviceProperty(set, info, NativeConstants.DEVPKEY_Bluetooth_Battery,
                    out var type, buffer, out _, 0) || type != DEVPROPTYPE.DEVPROP_TYPE_BYTE)
                continue;

            var battery = buffer[0];

            if (!PInvoke.SetupDiGetDeviceProperty(set, info, PInvoke.DEVPKEY_Device_ContainerId,
                    out type, buffer, out _, 0) || type != DEVPROPTYPE.DEVPROP_TYPE_GUID)
                continue;

            var container = MemoryMarshal.Read<Guid>(buffer);
            if (battery <= 100)
                result[container] = battery;
        }
    }
}
