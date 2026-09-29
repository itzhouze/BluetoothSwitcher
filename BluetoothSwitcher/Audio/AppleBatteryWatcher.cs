using System.Runtime.InteropServices;
using BluetoothSwitcher.Logging;
using Windows.Win32;

namespace BluetoothSwitcher.Audio;

/// <summary>Akkustand von AirPods/Beats aus dem Apple-„Proximity Pairing“-Paket (in 10 %-Schritten).</summary>
/// <param name="Left">Linker Ohrhörer in % oder null (unbekannt/im Case ohne Wert).</param>
/// <param name="Right">Rechter Ohrhörer in % oder null.</param>
/// <param name="Case">Ladecase in % oder null.</param>
/// <param name="Model">Apple-Modellkennung (z. B. 0x201B), um die eigenen AirPods wiederzuerkennen.</param>
/// <param name="Rssi">Geglättete Signalstärke in dBm.</param>
/// <param name="SignalBars">Signal als 0–4 Balken.</param>
internal sealed record AppleBattery(byte? Left, byte? Right, byte? Case, bool LeftCharging, bool RightCharging,
    ushort Model, DateTime Received, short Rssi = 0, int SignalBars = 0)
{
    /// <summary>Werte für die Anzeige: je Ohrhörer, der einen Akkustand meldet (also nicht im geschlossenen Case liegt).</summary>
    public IReadOnlyList<BatteryLevel> ToLevels()
    {
        var levels = new List<BatteryLevel>(2);
        if (Left is { } l)
            levels.Add(new BatteryLevel("L", l));
        if (Right is { } r)
            levels.Add(new BatteryLevel("R", r));
        return levels;
    }
}

/// <summary>
/// Windows liefert für AirPods keinen Akkustand – Apple sendet ihn nur per Bluetooth-LE-Advertisement
/// (Hersteller-ID 0x004C, Typ 0x07), das auch iPhones für das Akku-Popup auswerten. Dieser Watcher
/// hört passiv mit.
///
/// Umgesetzt mit minimaler, handgeschriebener WinRT-Interop (BluetoothLEAdvertisementWatcher), um die
/// ~25 MB große CsWinRT-Projektion zu vermeiden. Vtable-Slots entsprechen den Windows-SDK-Headern
/// (windows.devices.bluetooth.advertisement.h); 0–5 sind IUnknown + IInspectable.
/// </summary>
internal sealed unsafe class AppleBatteryWatcher : IDisposable
{
    private const ushort AppleCompanyId = 0x004C;
    private const byte ProximityPairingType = 0x07;

    /// <summary>
    /// Nur Pakete ab diesem Pegel gelten sicher als „unsere“ AirPods (fremde in der Nähe sind schwächer).
    /// Deren Modell wird gemerkt; danach zählen auch schwächere Pakete desselben Modells – sonst könnte
    /// die Signalanzeige nie „schwach“ zeigen.
    /// </summary>
    private const short TrustedSignalDbm = -70;

    /// <summary>Glättung der stark schwankenden RSSI-Werte (Anteil des neuen Werts).</summary>
    private const double RssiSmoothing = 0.3;

    private static readonly Guid IID_IBluetoothLEAdvertisementWatcher = new("a6ac336f-f3d3-4297-8d6c-c81ea6623f40");
    private static readonly Guid IID_IBluetoothLEAdvertisementReceivedEventArgs = new("27987ddf-e596-41be-8d43-9e6731d4a913");
    private static readonly Guid IID_IBluetoothLEAdvertisement = new("066fb2b7-33d1-4e7d-8367-cf81d0f79653");
    private static readonly Guid IID_IBluetoothLEManufacturerData = new("912dba18-6963-4533-b061-4694dafb34e5");
    private static readonly Guid IID_IBuffer = new("905a0fe0-bc53-11df-8c49-001e4fc686da");
    private static readonly Guid IID_IBufferByteAccess = new("905a0fef-bc53-11df-8c49-001e4fc686da");

    // IBluetoothLEAdvertisementWatcher
    private const int Watcher_put_ScanningMode = 12;
    private const int Watcher_Start = 17;
    private const int Watcher_Stop = 18;
    private const int Watcher_add_Received = 19;
    private const int Watcher_remove_Received = 20;

    // IBluetoothLEAdvertisementReceivedEventArgs
    private const int Args_get_RawSignalStrengthInDBm = 6;
    private const int Args_get_Advertisement = 10;

    // IBluetoothLEAdvertisement
    private const int Adv_GetManufacturerDataByCompanyId = 13;

    // IVectorView<T>
    private const int VectorView_GetAt = 6;
    private const int VectorView_get_Size = 7;

    // IBluetoothLEManufacturerData
    private const int ManufacturerData_get_Data = 8;

    // IBuffer / IBufferByteAccess
    private const int Buffer_get_Length = 7;
    private const int BufferByteAccess_Buffer = 3;

    private const int ScanningModePassive = 0;

    private readonly ReceivedHandler _handler;
    private IntPtr _watcher;
    private IntPtr _handlerPtr;
    private long _token;
    private AppleBattery? _latest;
    private readonly Lock _gate = new(); // Received-Events können parallel auf mehreren Threads eintreffen
    private ushort? _ownModel;
    private double? _smoothedRssi;

    /// <summary>Neuer Akkuwert empfangen (beliebiger Thread, nur bei Änderung).</summary>
    public event Action? Changed;

    /// <summary>Jedes Apple-Paket roh (Diagnose): Signalstärke, Daten nach der Hersteller-ID.</summary>
    public event Action<short, byte[]>? RawApplePacket;

    public AppleBatteryWatcher()
    {
        _handler = new ReceivedHandler(this);
    }

    /// <summary>Letzter empfangener Wert, sofern nicht älter als <paramref name="maxAge"/>.</summary>
    public AppleBattery? GetLatest(TimeSpan maxAge)
    {
        var latest = _latest;
        return latest is not null && DateTime.UtcNow - latest.Received <= maxAge ? latest : null;
    }

    public bool IsRunning => _watcher != IntPtr.Zero;

    public void Start()
    {
        if (IsRunning)
            return;
        try
        {
            using var className = CreateHString("Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher");
#pragma warning disable CA1416 // WinRT ab Windows 8 – die App setzt ohnehin Windows 10/11 voraus
            PInvoke.RoActivateInstance(className, out var inspectable).ThrowOnFailure();
#pragma warning restore CA1416
            var unknown = Marshal.GetIUnknownForObject(inspectable);
            try
            {
                _watcher = QueryInterface(unknown, IID_IBluetoothLEAdvertisementWatcher);
            }
            finally
            {
                Marshal.Release(unknown);
                Marshal.ReleaseComObject(inspectable);
            }

            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(_watcher, Watcher_put_ScanningMode))(_watcher, ScanningModePassive));

            _handlerPtr = Marshal.GetComInterfaceForObject<ReceivedHandler, IAdvertisementReceivedHandler>(_handler);
            long token;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(_watcher, Watcher_add_Received))(_watcher, _handlerPtr, &token));
            _token = token;

            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_watcher, Watcher_Start))(_watcher));
            Log.Info("AirPods-Akku: BLE-Scan gestartet");
        }
        catch (Exception ex)
        {
            Log.Warn($"AirPods-Akku: BLE-Scan nicht möglich ({ex.Message})");
            Stop();
        }
    }

    public void Stop()
    {
        if (_watcher != IntPtr.Zero)
        {
            try
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_watcher, Watcher_Stop))(_watcher);
                if (_token != 0)
                    ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(_watcher, Watcher_remove_Received))(_watcher, _token);
            }
            catch
            {
                // beim Beenden egal
            }
            Marshal.Release(_watcher);
            _watcher = IntPtr.Zero;
            _token = 0;
        }
        if (_handlerPtr != IntPtr.Zero)
        {
            Marshal.Release(_handlerPtr);
            _handlerPtr = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    // ---------------------------------------------------------------- Empfang

    private void OnReceived(IntPtr args)
    {
        IntPtr eventArgs = IntPtr.Zero, advertisement = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            eventArgs = QueryInterface(args, IID_IBluetoothLEAdvertisementReceivedEventArgs);

            short rssi;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, short*, int>)Slot(eventArgs, Args_get_RawSignalStrengthInDBm))(eventArgs, &rssi));
            if (rssi < TrustedSignalDbm && _ownModel is null && RawApplePacket is null)
                return;

            IntPtr adv;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(eventArgs, Args_get_Advertisement))(eventArgs, &adv));
            advertisement = adv;

            IntPtr view;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, ushort, IntPtr*, int>)Slot(advertisement, Adv_GetManufacturerDataByCompanyId))(advertisement, AppleCompanyId, &view));
            list = view;

            uint count;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(list, VectorView_get_Size))(list, &count));
            for (uint i = 0; i < count; i++)
            {
                IntPtr item;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(list, VectorView_GetAt))(list, i, &item));
                try
                {
                    var data = ReadManufacturerData(item);
                    RawApplePacket?.Invoke(rssi, data);
                    if (TryParseProximityPairing(data, out var battery))
                        Accept(battery, rssi);
                }
                finally
                {
                    Marshal.Release(item);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"AirPods-Akku: Paket nicht lesbar ({ex.Message})");
        }
        finally
        {
            if (list != IntPtr.Zero) Marshal.Release(list);
            if (advertisement != IntPtr.Zero) Marshal.Release(advertisement);
            if (eventArgs != IntPtr.Zero) Marshal.Release(eventArgs);
        }
    }

    private void Accept(AppleBattery battery, short rssi)
    {
        lock (_gate)
            AcceptLocked(battery, rssi);
    }

    private void AcceptLocked(AppleBattery battery, short rssi)
    {
        var trusted = rssi >= TrustedSignalDbm;
        if (!trusted && battery.Model != _ownModel)
            return; // schwaches Paket eines anderen Modells → vermutlich fremde AirPods
        if (trusted)
            _ownModel = battery.Model;

        _smoothedRssi = _smoothedRssi is { } s ? s + (rssi - s) * RssiSmoothing : rssi;
        var smoothed = (short)Math.Round(_smoothedRssi.Value);
        var previous = _latest;
        battery = battery with { Rssi = smoothed, SignalBars = ToBars(smoothed, previous?.SignalBars) };

        _latest = battery;

        // Nur bei sichtbarer Änderung melden (Akku oder Balkenzahl), nicht bei jedem RSSI-Zucken.
        if (previous is null || previous with { Received = battery.Received, Rssi = battery.Rssi } != battery)
        {
            Log.Info($"AirPods: L {Fmt(battery.Left)} / R {Fmt(battery.Right)} / Case {Fmt(battery.Case)}, " +
                     $"Signal {battery.SignalBars}/4 ({smoothed} dBm)");
            Changed?.Invoke();
        }

        static string Fmt(byte? v) => v is { } b ? $"{b} %" : "–";
    }

    /// <summary>
    /// Signalstärke → 0–4 Balken mit 3 dB Hysterese: Die Anzeige wechselt erst, wenn der Wert
    /// deutlich über die Grenze geht – sonst flackert sie bei Werten genau an der Schwelle.
    /// </summary>
    internal static int ToBars(short rssi, int? previousBars)
    {
        const int hysteresis = 3;
        var bars = ToBars(rssi);
        if (previousBars is { } prev && bars != prev &&
            ToBars((short)(rssi - hysteresis)) <= prev && prev <= ToBars((short)(rssi + hysteresis)))
            return prev;
        return bars;
    }

    /// <summary>Signalstärke → 0–4 Balken (grobe Einteilung wie am Handy).</summary>
    internal static int ToBars(short rssi) => rssi switch
    {
        >= -65 => 4,
        >= -72 => 3,
        >= -80 => 2,
        >= -88 => 1,
        _ => 0,
    };

    private static byte[] ReadManufacturerData(IntPtr item)
    {
        var manufacturerData = QueryInterface(item, IID_IBluetoothLEManufacturerData);
        IntPtr buffer = IntPtr.Zero, bufferObj = IntPtr.Zero, byteAccess = IntPtr.Zero;
        try
        {
            IntPtr b;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(manufacturerData, ManufacturerData_get_Data))(manufacturerData, &b));
            bufferObj = b;
            buffer = QueryInterface(bufferObj, IID_IBuffer);

            uint length;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(buffer, Buffer_get_Length))(buffer, &length));

            byteAccess = QueryInterface(bufferObj, IID_IBufferByteAccess);
            byte* bytes;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, int>)Slot(byteAccess, BufferByteAccess_Buffer))(byteAccess, &bytes));
            return new ReadOnlySpan<byte>(bytes, (int)length).ToArray();
        }
        finally
        {
            if (byteAccess != IntPtr.Zero) Marshal.Release(byteAccess);
            if (buffer != IntPtr.Zero) Marshal.Release(buffer);
            if (bufferObj != IntPtr.Zero) Marshal.Release(bufferObj);
            Marshal.Release(manufacturerData);
        }
    }

    /// <summary>
    /// Aufbau (Bytes nach der Hersteller-ID): [0]=0x07 Typ, [1]=Länge, [2]=Präfix, [3..4]=Modell,
    /// [5]=Status (Bit 0x20: links/rechts vertauscht), [6]=Akku Ohrhörer (je Nibble, 0–10 = ×10 %, 15 = unbekannt),
    /// [7]=oberes Nibble Ladeflags, unteres Nibble Akku Case.
    /// </summary>
    internal static bool TryParseProximityPairing(ReadOnlySpan<byte> d, out AppleBattery battery)
    {
        battery = null!;
        if (d.Length < 8 || d[0] != ProximityPairingType || d[1] < 6)
            return false;

        var flipped = (d[5] & 0x20) == 0;
        var high = d[6] >> 4;
        var low = d[6] & 0x0F;
        var left = flipped ? high : low;
        var right = flipped ? low : high;
        var caseLevel = d[7] & 0x0F;
        var charging = d[7] >> 4;

        battery = new AppleBattery(
            Level(left),
            Level(right),
            Level(caseLevel),
            LeftCharging: (charging & (flipped ? 0b10 : 0b01)) != 0,
            RightCharging: (charging & (flipped ? 0b01 : 0b10)) != 0,
            Model: (ushort)((d[3] << 8) | d[4]),
            Received: DateTime.UtcNow);
        return battery.Left is not null || battery.Right is not null;

        static byte? Level(int nibble) => nibble <= 10 ? (byte)(nibble * 10) : null;
    }

    // ---------------------------------------------------------------- COM-Helfer

    private static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    private static IntPtr QueryInterface(IntPtr obj, Guid iid)
    {
        Check(Marshal.QueryInterface(obj, in iid, out var result));
        return result;
    }

    private static WindowsDeleteStringSafeHandle CreateHString(string value)
    {
#pragma warning disable CA1416 // siehe oben
        PInvoke.WindowsCreateString(value, (uint)value.Length, out var handle).ThrowOnFailure();
#pragma warning restore CA1416
        return handle;
    }

    /// <summary>TypedEventHandler&lt;BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs&gt;.</summary>
    [ComImport]
    [Guid("90eb4eca-d465-5ea0-a61c-033c8c5ecef2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAdvertisementReceivedHandler
    {
        [PreserveSig]
        int Invoke(IntPtr sender, IntPtr args);
    }

    [ComVisible(true)]
    internal sealed class ReceivedHandler(AppleBatteryWatcher owner) : IAdvertisementReceivedHandler
    {
        public int Invoke(IntPtr sender, IntPtr args)
        {
            owner.OnReceived(args);
            return 0;
        }
    }
}
