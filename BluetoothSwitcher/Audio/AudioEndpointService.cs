using System.Runtime.InteropServices;
using BluetoothSwitcher.Logging;
using BluetoothSwitcher.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace BluetoothSwitcher.Audio;

internal enum EndpointState
{
    Active,
    Unplugged,
    Disabled,
    NotPresent,
}

/// <param name="Id">Core-Audio-Endpoint-ID.</param>
/// <param name="FriendlyName">z. B. "Kopfhörer (AirPods von Max)".</param>
/// <param name="ContainerId">Physisches Gerät (gleich für A2DP- und Hands-Free-Endpoint).</param>
/// <param name="IsBluetooth">KS-Filter hängt am Bluetooth-Enumerator.</param>
internal sealed record EndpointInfo(string Id, string FriendlyName, EndpointState State, Guid? ContainerId, bool IsBluetooth)
{
    public bool IsHandsFree =>
        FriendlyName.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ||
        FriendlyName.Contains("Freisprech", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Zugriff auf Windows Core Audio (Render-Endpoints). Alle Methoden sind threadunabhängig:
/// Jeder Aufruf erzeugt seinen eigenen Enumerator (kostet &lt; 1 ms) und kann daher gefahrlos
/// auf Thread-Pool-Threads laufen.
/// </summary>
internal sealed class AudioEndpointService : IDisposable
{
    private readonly IMMDeviceEnumerator _notifyEnumerator;
    private readonly NotificationClient _client;

    /// <summary>Irgendetwas an den Endpoints hat sich geändert (Status, Default, hinzugefügt, entfernt). Beliebiger Thread.</summary>
    public event Action? EndpointsChanged;

    public AudioEndpointService()
    {
        // Notifications kommen auf MTA-Threads des Audio-Dienstes. Enumerator auf MTA erzeugen,
        // damit der STA-UI-Thread nicht als COM-Apartment für die Callbacks herhalten muss.
        (_notifyEnumerator, _client) = Task.Run(() =>
        {
            var enumerator = CreateEnumerator();
            var client = new NotificationClient(() => EndpointsChanged?.Invoke());
            enumerator.RegisterEndpointNotificationCallback(client);
            return (enumerator, client);
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            _notifyEnumerator.UnregisterEndpointNotificationCallback(_client);
            Marshal.ReleaseComObject(_notifyEnumerator);
        }
        catch (Exception ex)
        {
            Log.Warn($"UnregisterEndpointNotificationCallback: {ex.Message}");
        }
    }

    public static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumerator();

    /// <summary>Alle Render-Endpoints außer "nicht vorhanden" (also auch getrennte BT-Geräte).</summary>
    public static List<EndpointInfo> GetRenderEndpoints()
    {
        var result = new List<EndpointInfo>();
        var enumerator = CreateEnumerator();
        try
        {
            enumerator.EnumAudioEndpoints(EDataFlow.eRender,
                DEVICE_STATE.DEVICE_STATE_ACTIVE | DEVICE_STATE.DEVICE_STATE_UNPLUGGED | DEVICE_STATE.DEVICE_STATE_DISABLED,
                out var collection);
            collection.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out var device);
                try
                {
                    result.Add(ReadEndpoint(device));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Endpoint {i} konnte nicht gelesen werden: {ex.Message}");
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            Marshal.ReleaseComObject(collection);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }

    public static string? GetDefaultRenderId()
    {
        var enumerator = CreateEnumerator();
        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            try
            {
                return GetId(device);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch (COMException)
        {
            return null; // kein Ausgabegerät vorhanden
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public static EndpointState? GetState(string endpointId)
    {
        var enumerator = CreateEnumerator();
        try
        {
            enumerator.GetDevice(endpointId, out var device);
            try
            {
                device.GetState(out var state);
                return MapState(state);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Setzt den Endpoint als Standard für Console + Multimedia (+ optional Communications).</summary>
    public static void SetDefault(string endpointId, bool includeCommunications)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            Check(policy.SetDefaultEndpoint(endpointId, ERole.eConsole), "eConsole");
            Check(policy.SetDefaultEndpoint(endpointId, ERole.eMultimedia), "eMultimedia");
            if (includeCommunications)
                Check(policy.SetDefaultEndpoint(endpointId, ERole.eCommunications), "eCommunications");
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }

        static void Check(int hr, string role)
        {
            if (hr < 0)
                throw new COMException($"SetDefaultEndpoint({role}) fehlgeschlagen", hr);
        }
    }

    private static EndpointInfo ReadEndpoint(IMMDevice device)
    {
        var id = GetId(device);
        device.GetState(out var state);

        string name = id;
        Guid? container = null;
        device.OpenPropertyStore(STGM.STGM_READ, out var store);
        try
        {
            name = GetStringProperty(store, PInvoke.PKEY_Device_FriendlyName) ?? id;
            container = GetGuidProperty(store, NativeConstants.PKEY_Device_ContainerId);
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }

        var filterId = BluetoothAudioConnector.TryGetKsFilterId(device);
        var isBluetooth = filterId is not null && BluetoothAudioConnector.IsBluetoothFilter(filterId);

        return new EndpointInfo(id, name, MapState(state), container, isBluetooth);
    }

    internal static unsafe string GetId(IMMDevice device)
    {
        device.GetId(out PWSTR pwstr);
        try
        {
            return pwstr.ToString();
        }
        finally
        {
            Marshal.FreeCoTaskMem((IntPtr)pwstr.Value);
        }
    }

    private static EndpointState MapState(DEVICE_STATE state) => state switch
    {
        DEVICE_STATE.DEVICE_STATE_ACTIVE => EndpointState.Active,
        DEVICE_STATE.DEVICE_STATE_UNPLUGGED => EndpointState.Unplugged,
        DEVICE_STATE.DEVICE_STATE_DISABLED => EndpointState.Disabled,
        _ => EndpointState.NotPresent,
    };

    private static unsafe string? GetStringProperty(IPropertyStore store, in PROPERTYKEY key)
    {
        store.GetValue(key, out PROPVARIANT pv);
        try
        {
            return pv.vt == VARENUM.VT_LPWSTR ? pv.pwszVal.ToString() : null;
        }
        finally
        {
            PInvoke.PropVariantClear(ref pv);
        }
    }

    private static unsafe Guid? GetGuidProperty(IPropertyStore store, in PROPERTYKEY key)
    {
        store.GetValue(key, out PROPVARIANT pv);
        try
        {
            return pv.vt == VARENUM.VT_CLSID && pv.puuid != null ? *pv.puuid : null;
        }
        finally
        {
            PInvoke.PropVariantClear(ref pv);
        }
    }

    /// <summary>COM-Callback für Endpoint-Änderungen. Wird von Windows auf MTA-Threads aufgerufen.</summary>
    private sealed class NotificationClient(Action onChange) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(PCWSTR pwstrDeviceId, DEVICE_STATE dwNewState) => onChange();
        public void OnDeviceAdded(PCWSTR pwstrDeviceId) => onChange();
        public void OnDeviceRemoved(PCWSTR pwstrDeviceId) => onChange();

        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, PCWSTR pwstrDefaultDeviceId)
        {
            if (flow == EDataFlow.eRender && role == ERole.eMultimedia)
                onChange();
        }

        public void OnPropertyValueChanged(PCWSTR pwstrDeviceId, PROPERTYKEY key)
        {
            // Sehr häufig (Lautstärke etc.) – ignorieren.
        }
    }
}
