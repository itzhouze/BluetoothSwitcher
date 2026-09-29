using System.Text.Json;
using BluetoothSwitcher.Audio;
using BluetoothSwitcher.Logging;

namespace BluetoothSwitcher.Config;

internal static class ConfigStore
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BTSwitcher");

    public static string FilePath { get; } = Path.Combine(Directory, "config.json");

    private static readonly string[] VirtualDeviceMarkers =
        ["VB-Audio", "CABLE Input", "CABLE In ", "Voicemeeter", "Virtual Audio Cable", "Steam Streaming", "Oculus Virtual", "SteelSeries Sonar", "NVIDIA Broadcast"];

    /// <summary>
    /// Lädt die Config. Existiert keine, wird sie aus den gefundenen Audiogeräten erzeugt.
    /// Wirft <see cref="ConfigException"/> bei einer kaputten Datei.
    /// </summary>
    public static AppConfig LoadOrCreate()
    {
        if (!File.Exists(FilePath))
        {
            var seeded = CreateDefault();
            Save(seeded);
            Log.Info($"Neue Config mit {seeded.Devices.Count} Geräten erzeugt: {FilePath}");
            return seeded;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)
                ?? throw new ConfigException("Config ist leer.");
            Normalize(config);
            Log.Info($"Config geladen: {config.Devices.Count} Geräte, Hotkey {config.Hotkey}");
            return config;
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"config.json ist ungültig (Zeile {ex.LineNumber + 1}): {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new ConfigException($"config.json nicht lesbar: {ex.Message}", ex);
        }
    }

    public static void Save(AppConfig config)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static void Normalize(AppConfig config)
    {
        config.Overlay ??= new OverlayConfig();
        config.Devices ??= [];
        config.Devices.RemoveAll(d => d is null);
        config.Overlay.BackgroundOpacity = Math.Clamp(config.Overlay.BackgroundOpacity, 0, 1);
        config.Overlay.Scale = Math.Clamp(config.Overlay.Scale, 0.5, 2.5);
        config.Overlay.FadeMs = Math.Clamp(config.Overlay.FadeMs, 0, 1000);
        config.Overlay.AutoCloseSec = Math.Clamp(config.Overlay.AutoCloseSec, 0, 300);
        config.ConnectTimeoutSec = Math.Clamp(config.ConnectTimeoutSec, 3, 60);
    }

    /// <summary>Erzeugt eine Start-Config aus den vorhandenen Render-Endpoints.</summary>
    public static AppConfig CreateDefault()
    {
        var config = new AppConfig();
        try
        {
            foreach (var endpoint in SuggestEndpoints(AudioEndpointService.GetRenderEndpoints()))
                config.Devices.Add(FromEndpoint(endpoint));
        }
        catch (Exception ex)
        {
            Log.Error("Audiogeräte für die Start-Config konnten nicht gelesen werden", ex);
        }
        return config;
    }

    /// <summary>
    /// Filtert die Endpoints auf sinnvolle Kandidaten: keine virtuellen Geräte, keine deaktivierten
    /// und pro BT-Gerät nur der Stereo-Endpoint (nicht Hands-Free).
    /// </summary>
    public static IEnumerable<EndpointInfo> SuggestEndpoints(IEnumerable<EndpointInfo> endpoints)
    {
        var list = endpoints
            .Where(e => e.State is EndpointState.Active or EndpointState.Unplugged)
            .Where(e => !IsVirtual(e.FriendlyName))
            .ToList();

        return list.Where(e => !(e.IsBluetooth && e.IsHandsFree &&
                                 list.Any(o => o != e && o.IsBluetooth && !o.IsHandsFree && o.ContainerId == e.ContainerId)));
    }

    public static bool IsVirtual(string name) =>
        VirtualDeviceMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    public static DeviceConfig FromEndpoint(EndpointInfo endpoint)
    {
        var shortName = ShortName(endpoint.FriendlyName);
        return new DeviceConfig
        {
            Name = shortName,
            Kind = GuessKind(endpoint),
            EndpointId = endpoint.Id,
            NameMatch = shortName,
            Bluetooth = endpoint.IsBluetooth,
            DisconnectWhenInactive = endpoint.IsBluetooth,
            SetCommunicationsRole = true,
        };
    }

    /// <summary>"Kopfhörer (AirPods von Max)" → "AirPods von Max".</summary>
    public static string ShortName(string friendlyName)
    {
        var open = friendlyName.IndexOf('(');
        var close = friendlyName.LastIndexOf(')');
        if (open >= 0 && close > open + 1)
            return friendlyName[(open + 1)..close].Trim();
        return friendlyName.Trim();
    }

    private static DeviceKind GuessKind(EndpointInfo endpoint)
    {
        var n = endpoint.FriendlyName;
        bool Has(string s) => n.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("AirPods") || Has("Buds") || Has("Earbud") || Has("WF-") || Has("Elite") || Has("In-Ear"))
            return DeviceKind.Earbuds;
        if (Has("Soundbar") || Has(" Bar") || Has("Sonos"))
            return DeviceKind.Soundbar;
        if (Has("HDMI") || Has("TV") || Has("High Definition Audio Device") || Has("NVIDIA") || Has("AMD"))
            return DeviceKind.Tv;
        if (endpoint.IsBluetooth || Has("Kopfhörer") || Has("Headphone") || Has("Headset"))
            return DeviceKind.Headphones;
        return DeviceKind.Speaker;
    }
}

internal sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);
