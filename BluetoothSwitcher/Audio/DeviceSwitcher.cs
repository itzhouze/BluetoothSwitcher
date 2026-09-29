using System.Diagnostics;
using BluetoothSwitcher.Config;
using BluetoothSwitcher.Logging;

namespace BluetoothSwitcher.Audio;

internal enum SwitchPhase
{
    Connecting,
    SettingDefault,
    Done,
    Failed,
}

/// <param name="Reason">Kurzer Grund ohne Gerätenamen, z. B. "nicht erreichbar".</param>
internal sealed record SwitchResult(DeviceConfig Device, bool Success, string Reason)
{
    /// <summary>Vollständige Meldung für den Toast.</summary>
    public string Message => Success ? $"Ton läuft jetzt über {Device.Name}" : $"{Device.Name} {Reason}";

    /// <summary>Kurze Meldung für die Overlay-Zeile.</summary>
    public string RowMessage => Success ? "Ton läuft jetzt hier" : char.ToUpper(Reason[0]) + Reason[1..];
}

/// <summary>
/// Führt einen Gerätewechsel durch: (BT verbinden →) als Standard setzen → andere BT-Geräte trennen.
/// Läuft komplett im Hintergrund; es ist immer höchstens ein Wechsel gleichzeitig aktiv.
/// </summary>
internal sealed class DeviceSwitcher
{
    private const int PollIntervalMs = 200;
    private const int ResumeDelayMs = 500;
    private int _busy;

    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    /// <summary>Gerät, auf das gerade gewechselt wird (oder null).</summary>
    public DeviceConfig? Target { get; private set; }

    /// <returns>null, wenn bereits ein Wechsel läuft.</returns>
    public Task<SwitchResult>? TrySwitch(DeviceConfig device, AppConfig config, IProgress<SwitchPhase> progress)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return null;

        Target = device;
        var timeout = TimeSpan.FromSeconds(config.ConnectTimeoutSec);
        var others = config.Devices.Where(d => d != device && d.Bluetooth && d.DisconnectWhenInactive).ToList();
        var pauseWhileConnecting = config.PauseWhileConnecting;

        return Task.Run(() =>
        {
            try
            {
                return Switch(device, others, timeout, pauseWhileConnecting, progress);
            }
            finally
            {
                Target = null;
                Volatile.Write(ref _busy, 0);
            }
        });
    }

    private static SwitchResult Switch(DeviceConfig device, List<DeviceConfig> others, TimeSpan timeout,
        bool pauseWhileConnecting, IProgress<SwitchPhase> progress)
    {
        var sw = Stopwatch.StartNew();
        var paused = false;
        Log.Info($"Wechsel auf '{device.Name}' gestartet");
        try
        {
            var endpoints = AudioEndpointService.GetRenderEndpoints();
            var endpoint = DeviceResolver.Resolve(device, endpoints);
            if (endpoint is null)
                return Fail(device, "wurde nicht gefunden", progress);

            if (endpoint.State != EndpointState.Active)
            {
                if (endpoint.State == EndpointState.Disabled)
                    return Fail(device, "ist in Windows ausgeschaltet", progress);

                if (!endpoint.IsBluetooth)
                    return Fail(device, "ist nicht angeschlossen", progress);

                progress.Report(SwitchPhase.Connecting);

                // Nur pausieren, wenn wirklich etwas läuft – sonst würde die Taste die Wiedergabe starten.
                if (pauseWhileConnecting && PlaybackControl.IsAudioPlaying())
                {
                    PlaybackControl.SendPlayPause();
                    paused = true;
                    Log.Info("Wiedergabe für den Verbindungsaufbau pausiert");
                }

                if (!ConnectAndWait(endpoint, timeout))
                    return Fail(device, "nicht erreichbar – eingeschaltet und in der Nähe?", progress);
            }

            progress.Report(SwitchPhase.SettingDefault);
            AudioEndpointService.SetDefault(endpoint.Id, device.SetCommunicationsRole);
            Log.Info($"'{device.Name}' als Standardgerät gesetzt ({endpoint.FriendlyName})");

            DisconnectOthers(endpoint, others, endpoints);

            Log.Info($"Wechsel auf '{device.Name}' erfolgreich nach {sw.ElapsedMilliseconds} ms");
            progress.Report(SwitchPhase.Done);
            return new SwitchResult(device, true, "");
        }
        catch (Exception ex)
        {
            Log.Error($"Wechsel auf '{device.Name}' fehlgeschlagen", ex);
            return Fail(device, "ließ sich nicht aktivieren", progress);
        }
        finally
        {
            if (paused)
            {
                Thread.Sleep(ResumeDelayMs); // Player hat Zeit, auf das neue Standardgerät umzuziehen
                PlaybackControl.SendPlayPause();
                Log.Info("Wiedergabe fortgesetzt");
            }
        }
    }

    private static bool ConnectAndWait(EndpointInfo endpoint, TimeSpan timeout)
    {
        BluetoothAudioConnector.Connect(endpoint.Id);

        var sw = Stopwatch.StartNew();
        var retried = false;
        while (sw.Elapsed < timeout)
        {
            Thread.Sleep(PollIntervalMs);
            if (AudioEndpointService.GetState(endpoint.Id) == EndpointState.Active)
            {
                Log.Info($"'{endpoint.FriendlyName}' verbunden nach {sw.ElapsedMilliseconds} ms");
                return true;
            }

            // Manche Geräte reagieren erst auf den zweiten Versuch.
            if (!retried && sw.Elapsed > timeout / 2)
            {
                retried = true;
                Log.Info("Noch nicht verbunden – zweiter Verbindungsversuch");
                try
                {
                    BluetoothAudioConnector.Connect(endpoint.Id);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Zweiter Verbindungsversuch: {ex.Message}");
                }
            }
        }

        Log.Warn($"Timeout: '{endpoint.FriendlyName}' nach {timeout.TotalSeconds:0} s nicht verbunden");
        return false;
    }

    private static void DisconnectOthers(EndpointInfo target, List<DeviceConfig> others, List<EndpointInfo> endpoints)
    {
        foreach (var other in others)
        {
            var ep = DeviceResolver.Resolve(other, endpoints);
            if (ep is null || !ep.IsBluetooth || AudioEndpointService.GetState(ep.Id) != EndpointState.Active)
                continue;
            if (ep.ContainerId is not null && ep.ContainerId == target.ContainerId)
                continue; // gleiches physisches Gerät

            try
            {
                BluetoothAudioConnector.Disconnect(ep.Id);
                Log.Info($"'{other.Name}' getrennt");
            }
            catch (Exception ex)
            {
                Log.Warn($"'{other.Name}' konnte nicht getrennt werden: {ex.Message}");
            }
        }
    }

    private static SwitchResult Fail(DeviceConfig device, string reason, IProgress<SwitchPhase> progress)
    {
        Log.Warn($"'{device.Name}' {reason}");
        progress.Report(SwitchPhase.Failed);
        return new SwitchResult(device, false, reason);
    }
}
