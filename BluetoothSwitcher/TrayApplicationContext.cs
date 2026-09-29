using System.Diagnostics;
using BluetoothSwitcher.Audio;
using BluetoothSwitcher.Config;
using BluetoothSwitcher.Hotkeys;
using BluetoothSwitcher.Logging;
using BluetoothSwitcher.Startup;
using BluetoothSwitcher.UI;

namespace BluetoothSwitcher;

/// <summary>Verdrahtet Tray-Icon, Hotkey, Gerätezustand, Overlay, Switcher und Toast.</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AudioEndpointService _audio;
    private readonly DeviceStateService _states;
    private readonly DeviceSwitcher _switcher = new();
    private readonly HotkeyManager _hotkey;
    private readonly OverlayForm _overlay;
    private readonly ToastForm _toast;
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _autostartItem;

    private AppConfig _config;
    private SettingsForm? _settings;

    public TrayApplicationContext()
    {
        string? startupError = null;
        try
        {
            _config = ConfigStore.LoadOrCreate();
        }
        catch (ConfigException ex)
        {
            Log.Error(ex.Message, ex.InnerException);
            startupError = "config.json fehlerhaft – Standardwerte aktiv";
            _config = new AppConfig();
        }

        _audio = new AudioEndpointService();
        _states = new DeviceStateService(_audio, _config);
        _states.Changed += OnStatesChanged;
        _states.ConfigIdsChanged += () => TrySave(_config);

        _overlay = new OverlayForm();
        _overlay.DeviceChosen += OnDeviceChosen;
        _toast = new ToastForm();

        _hotkey = new HotkeyManager();
        _hotkey.Pressed += OnHotkey;

        _icon = TrayIconFactory.Create();
        _openItem = new ToolStripMenuItem("Audiogerät wechseln …", null, (_, _) => ShowOverlay(Stopwatch.GetTimestamp()))
        {
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),
        };
        _autostartItem = new ToolStripMenuItem("Mit Windows starten", null, (_, _) => ToggleAutostart())
        {
            Checked = AutostartManager.IsEnabled,
        };

        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.AddRange(
        [
            _openItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Einstellungen …", null, (_, _) => ShowSettings()),
            new ToolStripMenuItem("Konfiguration neu laden", null, (_, _) => ReloadConfig()),
            new ToolStripMenuItem("Config-Datei öffnen", null, (_, _) => OpenPath(ConfigStore.FilePath)),
            new ToolStripMenuItem("Log-Ordner öffnen", null, (_, _) => OpenPath(Log.Directory)),
            new ToolStripSeparator(),
            _autostartItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Beenden", null, (_, _) => ExitThread()),
        ]);
        menu.Opening += (_, _) => _autostartItem.Checked = AutostartManager.IsEnabled;

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "BT-Switcher",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowOverlay(Stopwatch.GetTimestamp());
        };

        RegisterHotkey();
        _ = _states.RefreshAsync();
        _overlay.WarmUp(_config.Overlay, _config.Devices.Count);

        if (startupError is not null)
            _toast.ShowToast(ToastForm.Kind.Error, startupError, null, _config.Overlay.Scale);

        Log.Info("BT-Switcher gestartet");
    }

    // ---------------------------------------------------------------- Overlay / Hotkey

    private void OnHotkey()
    {
        var timestamp = Stopwatch.GetTimestamp();
        if (_overlay.IsOpen)
            _overlay.CloseOverlay(); // Toggle – ein laufender Wechsel läuft im Hintergrund weiter
        else
            ShowOverlay(timestamp);
    }

    private void ShowOverlay(long timestamp)
    {
        try
        {
            _overlay.ShowOverlay(_states.Current, _config.Overlay, _switcher.Target, timestamp);
        }
        catch (Exception ex)
        {
            Log.Error("Overlay konnte nicht angezeigt werden", ex);
            return;
        }
        // Cache im Hintergrund auffrischen (Akku, Status) – Overlay aktualisiert sich dann live.
        _ = _states.RefreshAsync();
    }

    private string? CurrentOutputName() =>
        _states.DefaultDevice?.Config.Name ??
        (_states.DefaultEndpoint is { } e ? ConfigStore.ShortName(e.FriendlyName) : null);

    private void OnStatesChanged()
    {
        var current = CurrentOutputName();
        var tooltip = current is null ? "BT-Switcher" : $"BT-Switcher – {current}";
        _tray.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _overlay.UpdateStates(_states.Current);
    }

    private async void OnDeviceChosen(DeviceConfig device)
    {
        if (_switcher.IsBusy)
            return;

        var state = _states.Current.FirstOrDefault(s => s.Config == device);
        if (state is { IsDefault: true, Availability: Availability.Connected })
        {
            _overlay.CloseOverlay(); // ist bereits aktiv
            return;
        }

        var progress = new Progress<SwitchPhase>(phase =>
        {
            switch (phase)
            {
                case SwitchPhase.Connecting:
                    _overlay.SetRowStatus(device, RowStatus.Busy, "Verbinde …");
                    break;
                case SwitchPhase.SettingDefault:
                    _overlay.SetRowStatus(device, RowStatus.Busy, "Schalte um …");
                    break;
            }
        });

        var task = _switcher.TrySwitch(device, _config, progress);
        if (task is null)
            return;

        _overlay.SetRowStatus(device, RowStatus.Busy,
            state?.Availability == Availability.Disconnected ? "Verbinde …" : "Schalte um …");

        SwitchResult result;
        try
        {
            result = await task;
        }
        catch (Exception ex)
        {
            Log.Error("Unerwarteter Fehler beim Wechsel", ex);
            result = new SwitchResult(device, false, "ließ sich nicht aktivieren");
        }

        await _states.RefreshAsync();

        if (_overlay.IsOpen)
        {
            _overlay.SetRowStatus(device, result.Success ? RowStatus.Success : RowStatus.Failed, result.RowMessage);
            if (result.Success)
                _overlay.CloseAfter(_config.Overlay.CloseDelayAfterSuccessMs);
        }
        else
        {
            _toast.ShowToast(result.Success ? ToastForm.Kind.Success : ToastForm.Kind.Error,
                result.Message, device.Kind, _config.Overlay.Scale);
        }
    }

    // ---------------------------------------------------------------- Config

    private void RegisterHotkey()
    {
        if (!HotkeyParser.TryParse(_config.Hotkey, out var hotkey))
        {
            Log.Warn($"Ungültiger Hotkey '{_config.Hotkey}' – verwende Ctrl+Alt+B");
            HotkeyParser.TryParse("Ctrl+Alt+B", out hotkey);
        }

        var ok = _hotkey.Register(hotkey!.Value);
        _openItem.ShortcutKeyDisplayString = hotkey.Value.ToString();
        if (!ok)
            _toast.ShowToast(ToastForm.Kind.Error, $"Hotkey {hotkey.Value} ist bereits belegt", null, _config.Overlay.Scale);
    }

    private void ReloadConfig()
    {
        try
        {
            ApplyConfig(ConfigStore.LoadOrCreate());
            _toast.ShowToast(ToastForm.Kind.Info, "Konfiguration neu geladen", null, _config.Overlay.Scale);
        }
        catch (ConfigException ex)
        {
            Log.Error(ex.Message, ex.InnerException);
            _toast.ShowToast(ToastForm.Kind.Error, "config.json fehlerhaft – alte Konfiguration bleibt aktiv", null, _config.Overlay.Scale);
        }
    }

    private void ApplyConfig(AppConfig config)
    {
        _config = config;
        _states.UpdateConfig(config);
        _overlay.WarmUp(config.Overlay, config.Devices.Count);
        RegisterHotkey();
    }

    private static void TrySave(AppConfig config)
    {
        try
        {
            ConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            Log.Error("Config konnte nicht gespeichert werden", ex);
        }
    }

    private void ShowSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }

        // Hotkey während der Aufnahme im Settings-Fenster deaktivieren, sonst schnappt er ihn weg.
        _settings = new SettingsForm(_config.Clone(), () => _states.Endpoints);
        _settings.HotkeyCaptureStarted += () => _hotkey.Unregister();
        _settings.HotkeyCaptureEnded += RegisterHotkey;
        _settings.Saved += config =>
        {
            TrySave(config);
            ApplyConfig(config);
            Log.Info("Einstellungen gespeichert");
        };
        _settings.FormClosed += (_, _) =>
        {
            _settings = null;
            if (_hotkey.Current is null)
                RegisterHotkey();
        };
        _settings.Show();
        _settings.Activate();
    }

    private void ToggleAutostart()
    {
        try
        {
            AutostartManager.SetEnabled(!AutostartManager.IsEnabled);
        }
        catch (Exception ex)
        {
            Log.Error("Autostart konnte nicht geändert werden", ex);
        }
        _autostartItem.Checked = AutostartManager.IsEnabled;
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"'{path}' konnte nicht geöffnet werden", ex);
        }
    }

    // ---------------------------------------------------------------- Exit

    protected override void ExitThreadCore()
    {
        Log.Info("BT-Switcher wird beendet");
        _tray.Visible = false;
        _settings?.Close();
        _hotkey.Dispose();
        _states.Dispose();
        _audio.Dispose();
        _overlay.Dispose();
        _toast.Dispose();
        _tray.Dispose();
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
