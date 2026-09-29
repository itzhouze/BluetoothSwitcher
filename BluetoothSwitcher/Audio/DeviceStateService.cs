using BluetoothSwitcher.Config;
using BluetoothSwitcher.Logging;

namespace BluetoothSwitcher.Audio;

/// <summary>
/// Hält einen stets aktuellen Cache der Gerätezustände, damit das Overlay ohne einen einzigen
/// COM-Aufruf sofort gezeichnet werden kann. Aktualisiert sich selbst bei Endpoint-Änderungen.
/// Alle Events werden auf dem UI-Thread ausgelöst.
/// </summary>
internal sealed class DeviceStateService : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly AudioEndpointService _audio;
    private readonly System.Threading.Timer _debounce;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AppConfig _config;

    /// <summary>AirPods-Akku per BLE – läuft nur, wenn ein Apple-Gerät konfiguriert ist.</summary>
    private AppleBatteryWatcher? _apple;
    private static readonly TimeSpan AppleBatteryMaxAge = TimeSpan.FromMinutes(3);

    public IReadOnlyList<DeviceState> Current { get; private set; } = [];
    public IReadOnlyList<EndpointInfo> Endpoints { get; private set; } = [];
    public DeviceState? DefaultDevice => Current.FirstOrDefault(d => d.IsDefault);
    public EndpointInfo? DefaultEndpoint { get; private set; }

    /// <summary>Der Cache hat sich geändert.</summary>
    public event Action? Changed;

    /// <summary>Endpoint-IDs wurden per NameMatch-Fallback aktualisiert → Config speichern.</summary>
    public event Action? ConfigIdsChanged;

    public DeviceStateService(AudioEndpointService audio, AppConfig config)
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Muss auf dem UI-Thread erzeugt werden.");
        _audio = audio;
        _config = config;
        _debounce = new System.Threading.Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _audio.EndpointsChanged += OnEndpointsChanged;
        UpdateAppleWatcher(config);
    }

    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        UpdateAppleWatcher(config);
        _ = RefreshAsync();
    }

    /// <summary>Apple sendet den Akku nicht über Standard-Bluetooth – erkennbar am Namen.</summary>
    internal static bool IsAppleDevice(DeviceConfig device, EndpointInfo? endpoint = null)
    {
        static bool Match(string? s) => s is not null &&
            (s.Contains("AirPods", StringComparison.OrdinalIgnoreCase) || s.Contains("Beats", StringComparison.OrdinalIgnoreCase));
        return Match(device.Name) || Match(device.NameMatch) || Match(endpoint?.FriendlyName);
    }

    private void UpdateAppleWatcher(AppConfig config)
    {
        var needed = config.Devices.Any(d => d.Bluetooth && IsAppleDevice(d));
        if (needed && _apple is null)
        {
            _apple = new AppleBatteryWatcher();
            _apple.Changed += OnEndpointsChanged;
            var watcher = _apple;
            Task.Run(watcher.Start); // MTA-Thread; Events kommen ohnehin auf Thread-Pool-Threads
        }
        else if (!needed && _apple is not null)
        {
            _apple.Changed -= OnEndpointsChanged;
            _apple.Dispose();
            _apple = null;
        }
    }

    private void OnEndpointsChanged() => _debounce.Change(250, Timeout.Infinite);

    public async Task RefreshAsync()
    {
        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var config = _config;
            var (endpoints, defaultId, batteries) = await Task.Run(() =>
                (AudioEndpointService.GetRenderEndpoints(), AudioEndpointService.GetDefaultRenderId(), BatteryReader.ReadAll()))
                .ConfigureAwait(false);

            var apple = _apple?.GetLatest(AppleBatteryMaxAge);
            var states = new List<DeviceState>(config.Devices.Count);
            var idUpdates = new List<(DeviceConfig Device, string NewId)>();

            foreach (var device in config.Devices)
            {
                var endpoint = DeviceResolver.Resolve(device, endpoints);
                if (endpoint is not null && !string.Equals(endpoint.Id, device.EndpointId, StringComparison.OrdinalIgnoreCase))
                    idUpdates.Add((device, endpoint.Id));

                IReadOnlyList<BatteryLevel> battery = [];
                int? signal = null;
                if (endpoint is { State: EndpointState.Active, IsBluetooth: true, ContainerId: { } container } &&
                    batteries.TryGetValue(container, out var b))
                    battery = [new BatteryLevel(null, b)];
                else if (apple is not null && endpoint is { State: EndpointState.Active, IsBluetooth: true } &&
                         IsAppleDevice(device, endpoint))
                {
                    battery = apple.ToLevels();
                    signal = apple.SignalBars;
                }

                var isDefault = endpoint is not null && string.Equals(endpoint.Id, defaultId, StringComparison.OrdinalIgnoreCase);
                states.Add(new DeviceState(device, endpoint, isDefault, battery, signal));
            }

            var defaultEndpoint = endpoints.FirstOrDefault(e => string.Equals(e.Id, defaultId, StringComparison.OrdinalIgnoreCase));

            _ui.Post(_ =>
            {
                if (!ReferenceEquals(config, _config))
                    return; // Config wurde zwischenzeitlich getauscht → neuer Refresh läuft bereits

                foreach (var (device, newId) in idUpdates)
                {
                    Log.Info($"Endpoint-ID für '{device.Name}' aktualisiert (NameMatch): {newId}");
                    device.EndpointId = newId;
                }

                Current = states;
                Endpoints = endpoints;
                DefaultEndpoint = defaultEndpoint;
                if (idUpdates.Count > 0)
                    ConfigIdsChanged?.Invoke();
                Changed?.Invoke();
            }, null);
        }
        catch (Exception ex)
        {
            Log.Error("Gerätestatus konnte nicht aktualisiert werden", ex);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        _audio.EndpointsChanged -= OnEndpointsChanged;
        _apple?.Dispose();
        _debounce.Dispose();
    }
}
