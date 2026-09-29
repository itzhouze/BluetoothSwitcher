using System.Text.Json.Serialization;

namespace BluetoothSwitcher.Config;

[JsonConverter(typeof(JsonStringEnumConverter<DeviceKind>))]
internal enum DeviceKind
{
    Headphones,
    Earbuds,
    Speaker,
    Soundbar,
    Tv,
}

internal sealed class AppConfig
{
    public string Hotkey { get; set; } = "Ctrl+Alt+B";
    public OverlayConfig Overlay { get; set; } = new();
    public int ConnectTimeoutSec { get; set; } = 12;

    /// <summary>Laufende Wiedergabe pausieren, solange ein BT-Gerät verbunden wird (Play/Pause-Medientaste).</summary>
    public bool PauseWhileConnecting { get; set; } = true;

    public List<DeviceConfig> Devices { get; set; } = [];

    public AppConfig Clone() => new()
    {
        Hotkey = Hotkey,
        Overlay = Overlay.Clone(),
        ConnectTimeoutSec = ConnectTimeoutSec,
        PauseWhileConnecting = PauseWhileConnecting,
        Devices = Devices.Select(d => d.Clone()).ToList(),
    };
}

internal sealed class OverlayConfig
{
    /// <summary>Deckkraft des Vollbild-Hintergrunds (0 = unsichtbar, 1 = schwarz).</summary>
    public double BackgroundOpacity { get; set; } = 0.78;

    /// <summary>Skalierung aller Größen (1.0 = Standard).</summary>
    public double Scale { get; set; } = 1.0;

    public int FadeMs { get; set; } = 150;
    public bool ActiveDeviceOnTop { get; set; } = true;
    public int CloseDelayAfterSuccessMs { get; set; } = 600;

    /// <summary>Overlay schließt sich nach so vielen Sekunden ohne Tastendruck (0 = nie).</summary>
    public int AutoCloseSec { get; set; } = 10;

    public OverlayConfig Clone() => (OverlayConfig)MemberwiseClone();
}

internal sealed class DeviceConfig
{
    public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; } = DeviceKind.Headphones;

    /// <summary>Core-Audio-Endpoint-ID ({0.0.0.00000000}.{guid}). Primärer Schlüssel.</summary>
    public string? EndpointId { get; set; }

    /// <summary>Fallback: Teilstring des Endpoint-Namens, falls sich die ID ändert (z. B. nach Neu-Pairing).</summary>
    public string? NameMatch { get; set; }

    public bool Bluetooth { get; set; }

    /// <summary>Beim Wechsel auf ein anderes Gerät dieses BT-Gerät trennen.</summary>
    public bool DisconnectWhenInactive { get; set; } = true;

    /// <summary>Auch als Standard-Kommunikationsgerät setzen.</summary>
    public bool SetCommunicationsRole { get; set; } = true;

    public DeviceConfig Clone() => (DeviceConfig)MemberwiseClone();

    public override string ToString() => Name;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;
