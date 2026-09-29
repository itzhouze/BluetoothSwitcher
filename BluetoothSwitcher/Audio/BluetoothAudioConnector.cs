using System.Runtime.InteropServices;
using BluetoothSwitcher.Logging;
using BluetoothSwitcher.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.KernelStreaming;
using Windows.Win32.System.Com;

namespace BluetoothSwitcher.Audio;

/// <summary>
/// Verbindet/trennt gepairte Bluetooth-Audiogeräte über KSPROPSETID_BtAudio
/// (KSPROPERTY_ONESHOT_RECONNECT / _DISCONNECT) auf dem Kernel-Streaming-Filter des Endpoints.
/// Das ist derselbe Mechanismus wie „Verbinden“ in der Windows-Soundsteuerung.
/// </summary>
internal static class BluetoothAudioConnector
{
    public static void Connect(string endpointId) =>
        SendOneShot(endpointId, NativeConstants.KSPROPERTY_ONESHOT_RECONNECT, "RECONNECT");

    public static void Disconnect(string endpointId) =>
        SendOneShot(endpointId, NativeConstants.KSPROPERTY_ONESHOT_DISCONNECT, "DISCONNECT");

    /// <summary>
    /// Liefert die Device-ID des KS-Filters, an dem der Endpoint hängt
    /// (z. B. "{2}.\\?\bthenum#{0000110b-...}..."), oder null.
    /// </summary>
    internal static unsafe string? TryGetKsFilterId(IMMDevice endpoint)
    {
        IDeviceTopology? topology = null;
        IConnector? connector = null;
        try
        {
            endpoint.Activate(NativeConstants.IID_IDeviceTopology, CLSCTX.CLSCTX_ALL, null, out var obj);
            topology = (IDeviceTopology)obj;
            topology.GetConnector(0, out connector);
            connector.GetDeviceIdConnectedTo(out PWSTR pwstr);
            try
            {
                return pwstr.ToString();
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)pwstr.Value);
            }
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Release(connector);
            Release(topology);
        }
    }

    internal static bool IsBluetoothFilter(string filterId) =>
        filterId.Contains(@"\bthenum", StringComparison.OrdinalIgnoreCase) ||
        filterId.Contains(@"\bthhfenum", StringComparison.OrdinalIgnoreCase) ||
        filterId.Contains(@"\bthleenum", StringComparison.OrdinalIgnoreCase);

    private static unsafe void SendOneShot(string endpointId, uint propertyId, string name)
    {
        var enumerator = AudioEndpointService.CreateEnumerator();
        IMMDevice? endpoint = null;
        IMMDevice? filter = null;
        IKsControl? ks = null;
        try
        {
            enumerator.GetDevice(endpointId, out var ep);
            endpoint = ep;
            var filterId = TryGetKsFilterId(ep)
                ?? throw new InvalidOperationException("Kein KS-Filter für den Endpoint gefunden.");

            if (!IsBluetoothFilter(filterId))
                throw new InvalidOperationException("Endpoint ist kein Bluetooth-Gerät.");

            enumerator.GetDevice(filterId, out filter);
            filter.Activate(NativeConstants.IID_IKsControl, CLSCTX.CLSCTX_ALL, null, out var obj);
            ks = (IKsControl)obj;

            var property = new KSIDENTIFIER();
            property.Set = NativeConstants.KSPROPSETID_BtAudio;
            property.Id = propertyId;
            property.Flags = NativeConstants.KSPROPERTY_TYPE_GET;

            ks.KsProperty(property, (uint)sizeof(KSIDENTIFIER), null, 0, out _);
            Log.Info($"KSPROPERTY_ONESHOT_{name} gesendet an {filterId}");
        }
        finally
        {
            Release(ks);
            Release(filter);
            Release(endpoint);
            Release(enumerator);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
            Marshal.ReleaseComObject(com);
    }
}
