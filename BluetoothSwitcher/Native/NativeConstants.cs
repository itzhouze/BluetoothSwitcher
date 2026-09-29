using Windows.Win32.Foundation;

namespace BluetoothSwitcher.Native;

/// <summary>Konstanten, die nicht (vollständig) in den Win32-Metadaten enthalten sind.</summary>
internal static class NativeConstants
{
    /// <summary>KSPROPSETID_BtAudio (bthhfpddi.h / ksmedia.h).</summary>
    public static readonly Guid KSPROPSETID_BtAudio = new("7fa06c40-b8f6-4c7e-8556-e8c33a12e54d");

    public const uint KSPROPERTY_ONESHOT_RECONNECT = 0;
    public const uint KSPROPERTY_ONESHOT_DISCONNECT = 1;
    public const uint KSPROPERTY_TYPE_GET = 0x00000001;

    /// <summary>Akkustand eines Bluetooth-Geräts in Prozent (DEVPROP_TYPE_BYTE).</summary>
    public static readonly DEVPROPKEY DEVPKEY_Bluetooth_Battery = new()
    {
        fmtid = new Guid("104ea319-6ee2-4701-bd47-8ddbf425bbe5"),
        pid = 2,
    };

    /// <summary>PKEY_Device_ContainerId (VT_CLSID) auf dem Endpoint-Property-Store.</summary>
    public static readonly PROPERTYKEY PKEY_Device_ContainerId = new()
    {
        fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
        pid = 2,
    };

    public static readonly Guid IID_IDeviceTopology = typeof(Windows.Win32.Media.Audio.IDeviceTopology).GUID;
    public static readonly Guid IID_IKsControl = typeof(Windows.Win32.Media.KernelStreaming.IKsControl).GUID;
}
