using BluetoothSwitcher.Config;

namespace BluetoothSwitcher.Audio;

internal enum Availability
{
    /// <summary>Endpoint aktiv – sofort nutzbar.</summary>
    Connected,

    /// <summary>BT-Gerät gepairt, aber nicht verbunden – kann verbunden werden.</summary>
    Disconnected,

    /// <summary>Nicht nutzbar (nicht gefunden, deaktiviert oder Kabelgerät ausgesteckt).</summary>
    Unavailable,
}

/// <summary>Momentaufnahme eines konfigurierten Geräts für die Anzeige.</summary>
internal sealed record DeviceState(DeviceConfig Config, EndpointInfo? Endpoint, bool IsDefault, byte? Battery)
{
    public Availability Availability => Endpoint?.State switch
    {
        EndpointState.Active => Availability.Connected,
        EndpointState.Unplugged when Endpoint.IsBluetooth => Availability.Disconnected,
        _ => Availability.Unavailable,
    };
}

internal static class DeviceResolver
{
    /// <summary>
    /// Findet den Endpoint zu einem konfigurierten Gerät: zuerst über die ID, sonst über
    /// <see cref="DeviceConfig.NameMatch"/> (bevorzugt Stereo- vor Hands-Free-Endpoint, aktiv vor getrennt).
    /// </summary>
    public static EndpointInfo? Resolve(DeviceConfig device, IReadOnlyList<EndpointInfo> endpoints)
    {
        if (!string.IsNullOrEmpty(device.EndpointId))
        {
            var byId = endpoints.FirstOrDefault(e => string.Equals(e.Id, device.EndpointId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId;
        }

        if (string.IsNullOrWhiteSpace(device.NameMatch))
            return null;

        return endpoints
            .Where(e => e.FriendlyName.Contains(device.NameMatch, StringComparison.OrdinalIgnoreCase))
            .Where(e => e.State != EndpointState.NotPresent)
            .OrderBy(e => e.IsHandsFree)
            .ThenBy(e => e.State == EndpointState.Active ? 0 : 1)
            .FirstOrDefault();
    }
}
