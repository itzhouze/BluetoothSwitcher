using BluetoothSwitcher.Audio;
using BluetoothSwitcher.Config;
using BluetoothSwitcher.Hotkeys;
using BluetoothSwitcher.Logging;
using BluetoothSwitcher.Startup;

namespace BluetoothSwitcher.UI;

/// <summary>Einstellungen: Allgemein (Hotkey, Overlay, Autostart) und Geräte-Editor. Komplett per Tastatur bedienbar.</summary>
internal sealed class SettingsForm : Form
{
    private static readonly (DeviceKind Kind, string Label)[] KindLabels =
    [
        (DeviceKind.Headphones, "Kopfhörer"),
        (DeviceKind.Earbuds, "In-Ear (AirPods & Co.)"),
        (DeviceKind.Speaker, "Lautsprecher"),
        (DeviceKind.Soundbar, "Soundbar"),
        (DeviceKind.Tv, "TV"),
    ];

    private readonly AppConfig _config;
    private readonly Func<IReadOnlyList<EndpointInfo>> _endpoints;

    // Allgemein
    private readonly HotkeyCaptureBox _hotkeyBox = new();
    private readonly CheckBox _autostart = new() { Text = "Mit &Windows starten" };
    private readonly TrackBar _opacity = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10 };
    private readonly Label _opacityValue = new();
    private readonly TrackBar _scale = new() { Minimum = 50, Maximum = 250, TickFrequency = 25, SmallChange = 5, LargeChange = 25 };
    private readonly Label _scaleValue = new();
    private readonly CheckBox _activeOnTop = new() { Text = "&Aktives Gerät oben anzeigen" };
    private readonly NumericUpDown _fadeMs = new() { Minimum = 0, Maximum = 1000, Increment = 25 };
    private readonly NumericUpDown _closeDelay = new() { Minimum = 0, Maximum = 5000, Increment = 100 };
    private readonly NumericUpDown _timeout = new() { Minimum = 3, Maximum = 60 };
    private readonly NumericUpDown _autoClose = new() { Minimum = 0, Maximum = 300 };
    private readonly CheckBox _pauseWhileConnecting = new() { Text = "Film &pausieren, während ein Bluetooth-Gerät verbindet" };

    // Geräte
    private readonly ListBox _deviceList = new() { IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly TextBox _name = new();
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _nameMatch = new();
    private readonly Label _endpointLabel = new() { AutoSize = false, AutoEllipsis = true };
    private readonly CheckBox _bluetooth = new() { Text = "&Bluetooth-Gerät (wird bei Bedarf verbunden)" };
    private readonly CheckBox _disconnect = new() { Text = "Beim Wechsel auf ein anderes Gerät &trennen" };
    private readonly CheckBox _communications = new() { Text = "Auch als &Kommunikationsgerät setzen" };
    private readonly Panel _editor = new();

    private readonly Panel _generalPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Panel _devicesPage = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly RadioButton _generalTab = new() { Text = "Allgemein" };
    private readonly RadioButton _devicesTab = new() { Text = "Geräte" };

    private bool _loading;

    public event Action<AppConfig>? Saved;
    public event Action? HotkeyCaptureStarted;
    public event Action? HotkeyCaptureEnded;

    public SettingsForm(AppConfig workingCopy, Func<IReadOnlyList<EndpointInfo>> endpoints)
    {
        _config = workingCopy;
        _endpoints = endpoints;

        Text = "BT-Switcher – Einstellungen";
        Font = new Font(Theme.TextFontSmall, 10.5f);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1080, 800);
        MinimumSize = new Size(980, 700);
        BackColor = Theme.WindowBackground;
        ForeColor = Theme.TextPrimary;
        KeyPreview = true;
        ShowIcon = false;

        BuildLayout();
        LoadValues();
        ApplyDarkTheme(this);
    }

    // ---------------------------------------------------------------- Layout

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(24, 18, 24, 18) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        // Segment-Umschalter statt TabControl (lässt sich sauber dunkel darstellen)
        var tabs = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 14), WrapContents = false };
        foreach (var tab in new[] { _generalTab, _devicesTab })
        {
            tab.Appearance = Appearance.Button;
            tab.AutoSize = false;
            tab.Size = new Size(150, 40);
            tab.TextAlign = ContentAlignment.MiddleCenter;
            tab.FlatStyle = FlatStyle.Flat;
            tab.FlatAppearance.BorderSize = 0;
            tab.FlatAppearance.CheckedBackColor = Theme.Accent;
            tab.Font = new Font(Font, FontStyle.Bold);
            tab.Margin = new Padding(0, 0, 8, 0);
            tab.CheckedChanged += (_, _) => UpdatePages();
            tabs.Controls.Add(tab);
        }
        _generalTab.Checked = true;
        root.Controls.Add(tabs, 0, 0);

        var pages = new Panel { Dock = DockStyle.Fill };
        pages.Controls.Add(_generalPage);
        pages.Controls.Add(_devicesPage);
        root.Controls.Add(pages, 0, 1);
        BuildGeneralPage();
        BuildDevicesPage();

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        var save = MakeButton("&Speichern", primary: true);
        var cancel = MakeButton("Abbrechen");
        save.Click += (_, _) => SaveAndClose();
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 2);
        CancelButton = cancel;
    }

    private void BuildGeneralPage()
    {
        var grid = NewGrid(2);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _hotkeyBox.Width = 320;
        _hotkeyBox.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        _hotkeyBox.CaptureStarted += () => HotkeyCaptureStarted?.Invoke();
        _hotkeyBox.CaptureEnded += () => HotkeyCaptureEnded?.Invoke();

        AddRow(grid, "&Hotkey", _hotkeyBox, "Feld auswählen und die gewünschte Kombination drücken.");
        AddRow(grid, "", _autostart);
        AddRow(grid, "Hintergrund-&Deckkraft", WithValue(_opacity, _opacityValue));
        AddRow(grid, "&Größe im Overlay", WithValue(_scale, _scaleValue), "100 % ist für 3 m Abstand ausgelegt.");
        AddRow(grid, "", _activeOnTop);
        AddRow(grid, "&Animationsdauer (ms)", _fadeMs);
        AddRow(grid, "Schließen nach Erfolg (&ms)", _closeDelay);
        AddRow(grid, "&Verbindungs-Timeout (s)", _timeout);
        AddRow(grid, "Automatisch s&chließen (s)", _autoClose, "Schließt das Overlay nach so vielen Sekunden ohne Tastendruck. 0 = nie.");
        AddRow(grid, "", _pauseWhileConnecting);

        _opacity.ValueChanged += (_, _) => _opacityValue.Text = $"{_opacity.Value} %";
        _scale.ValueChanged += (_, _) => _scaleValue.Text = $"{_scale.Value} %";

        var info = new Label
        {
            AutoSize = true,
            ForeColor = Theme.TextSecondary,
            Margin = new Padding(0, 18, 0, 0),
            Text = $"Config: {ConfigStore.FilePath}\nLogs:   {Log.Directory}",
        };
        grid.Controls.Add(info, 0, grid.RowCount);
        grid.SetColumnSpan(info, 2);

        _generalPage.Controls.Add(grid);
    }

    private void BuildDevicesPage()
    {
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _devicesPage.Controls.Add(split);

        // Linke Spalte: Liste + Buttons
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 18, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _deviceList.Dock = DockStyle.Fill;
        _deviceList.ItemHeight = 46;
        _deviceList.BorderStyle = BorderStyle.None;
        _deviceList.DrawItem += DrawDeviceItem;
        _deviceList.SelectedIndexChanged += (_, _) => LoadEditor();
        _deviceList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) RemoveDevice();
            else if (e.Alt && e.KeyCode == Keys.Up) { MoveDevice(-1); e.Handled = true; }
            else if (e.Alt && e.KeyCode == Keys.Down) { MoveDevice(+1); e.Handled = true; }
        };
        left.Controls.Add(_deviceList, 0, 0);

        var listButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        var add = MakeButton("&Hinzufügen …", width: 150);
        var remove = MakeButton("&Entfernen", width: 110);
        var up = MakeButton("▲", width: 44);
        var down = MakeButton("▼", width: 44);
        add.Click += (_, _) => AddDevice();
        remove.Click += (_, _) => RemoveDevice();
        up.Click += (_, _) => MoveDevice(-1);
        down.Click += (_, _) => MoveDevice(+1);
        var tips = new ToolTip();
        tips.SetToolTip(up, "Nach oben (Alt+↑)");
        tips.SetToolTip(down, "Nach unten (Alt+↓)");
        listButtons.Controls.AddRange([add, remove, up, down]);
        left.Controls.Add(listButtons, 0, 1);
        split.Controls.Add(left, 0, 0);

        // Rechte Spalte: Editor
        _editor.Dock = DockStyle.Fill;
        _editor.BackColor = Theme.PanelBackground;
        _editor.Padding = new Padding(20, 16, 20, 16);
        var grid = NewGrid(2);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var (_, label) in KindLabels)
            _kind.Items.Add(label);
        _name.Dock = _kind.Dock = DockStyle.Fill;
        _nameMatch.Width = 410;
        _endpointLabel.AutoSize = true;
        _endpointLabel.MaximumSize = new Size(460, 0);
        _endpointLabel.ForeColor = Theme.TextSecondary;
        _endpointLabel.Margin = new Padding(0, 4, 0, 8);

        var reassign = MakeButton("Audiogerät &zuordnen …", width: 230);
        reassign.MinimumSize = reassign.Size;
        reassign.Margin = Padding.Empty;
        reassign.Click += (_, _) => ReassignEndpoint();

        var endpointStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        endpointStack.Controls.Add(_endpointLabel);
        endpointStack.Controls.Add(reassign);

        AddRow(grid, "&Name", _name);
        AddRow(grid, "&Typ", _kind);
        AddRow(grid, "Audiogerät", endpointStack);
        AddRow(grid, "Name-&Match", _nameMatch, "Fallback, falls sich die Geräte-ID ändert (z. B. nach Neu-Pairing).");
        AddRow(grid, "", _bluetooth);
        AddRow(grid, "", _disconnect);
        AddRow(grid, "", _communications);
        _editor.Controls.Add(grid);
        split.Controls.Add(_editor, 1, 0);

        _name.TextChanged += (_, _) => EditSelected(d => d.Name = _name.Text, refreshList: true);
        _kind.SelectedIndexChanged += (_, _) => EditSelected(d => d.Kind = KindLabels[Math.Max(0, _kind.SelectedIndex)].Kind, refreshList: true);
        _nameMatch.TextChanged += (_, _) => EditSelected(d => d.NameMatch = string.IsNullOrWhiteSpace(_nameMatch.Text) ? null : _nameMatch.Text.Trim());
        _bluetooth.CheckedChanged += (_, _) => EditSelected(d => d.Bluetooth = _bluetooth.Checked, refreshList: true);
        _disconnect.CheckedChanged += (_, _) => EditSelected(d => d.DisconnectWhenInactive = _disconnect.Checked);
        _communications.CheckedChanged += (_, _) => EditSelected(d => d.SetCommunicationsRole = _communications.Checked);
    }

    private void UpdatePages()
    {
        _generalPage.Visible = _generalTab.Checked;
        _devicesPage.Visible = _devicesTab.Checked;
        foreach (var tab in new[] { _generalTab, _devicesTab })
            tab.BackColor = tab.Checked ? Theme.Accent : Theme.PanelBackground;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+Tab / Ctrl+1/2 wechseln die Seite
        if (keyData is (Keys.Control | Keys.Tab) or (Keys.Control | Keys.Shift | Keys.Tab))
        {
            (_generalTab.Checked ? _devicesTab : _generalTab).Checked = true;
            return true;
        }
        if (keyData == (Keys.Control | Keys.D1)) { _generalTab.Checked = true; return true; }
        if (keyData == (Keys.Control | Keys.D2)) { _devicesTab.Checked = true; return true; }
        if (keyData == (Keys.Control | Keys.S)) { SaveAndClose(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------------------------------------------------------------- Werte

    private void LoadValues()
    {
        _loading = true;
        _hotkeyBox.Hotkey = HotkeyParser.TryParse(_config.Hotkey, out var hk) ? hk : null;
        _autostart.Checked = AutostartManager.IsEnabled;
        _opacity.Value = (int)Math.Round(_config.Overlay.BackgroundOpacity * 100);
        _scale.Value = Math.Clamp((int)Math.Round(_config.Overlay.Scale * 100), _scale.Minimum, _scale.Maximum);
        _opacityValue.Text = $"{_opacity.Value} %";
        _scaleValue.Text = $"{_scale.Value} %";
        _activeOnTop.Checked = _config.Overlay.ActiveDeviceOnTop;
        _fadeMs.Value = Math.Clamp(_config.Overlay.FadeMs, 0, 1000);
        _closeDelay.Value = Math.Clamp(_config.Overlay.CloseDelayAfterSuccessMs, 0, 5000);
        _timeout.Value = Math.Clamp(_config.ConnectTimeoutSec, 3, 60);
        _autoClose.Value = Math.Clamp(_config.Overlay.AutoCloseSec, 0, 300);
        _pauseWhileConnecting.Checked = _config.PauseWhileConnecting;

        _deviceList.Items.Clear();
        foreach (var device in _config.Devices)
            _deviceList.Items.Add(device);
        _loading = false;

        if (_deviceList.Items.Count > 0)
            _deviceList.SelectedIndex = 0;
        LoadEditor();
        UpdatePages();
    }

    private void SaveAndClose()
    {
        if (_hotkeyBox.Hotkey is not { } hotkey)
        {
            ShowError("Bitte einen Hotkey festlegen.");
            _generalTab.Checked = true;
            _hotkeyBox.Focus();
            return;
        }
        var unnamed = _config.Devices.FirstOrDefault(d => string.IsNullOrWhiteSpace(d.Name));
        if (unnamed is not null)
        {
            ShowError("Jedes Gerät braucht einen Namen.");
            _devicesTab.Checked = true;
            _deviceList.SelectedItem = unnamed;
            _name.Focus();
            return;
        }

        _config.Hotkey = hotkey.ToString();
        _config.Overlay.BackgroundOpacity = _opacity.Value / 100.0;
        _config.Overlay.Scale = _scale.Value / 100.0;
        _config.Overlay.ActiveDeviceOnTop = _activeOnTop.Checked;
        _config.Overlay.FadeMs = (int)_fadeMs.Value;
        _config.Overlay.CloseDelayAfterSuccessMs = (int)_closeDelay.Value;
        _config.ConnectTimeoutSec = (int)_timeout.Value;
        _config.Overlay.AutoCloseSec = (int)_autoClose.Value;
        _config.PauseWhileConnecting = _pauseWhileConnecting.Checked;

        try
        {
            if (_autostart.Checked != AutostartManager.IsEnabled)
                AutostartManager.SetEnabled(_autostart.Checked);
        }
        catch (Exception ex)
        {
            Log.Error("Autostart konnte nicht geändert werden", ex);
        }

        Saved?.Invoke(_config);
        Close();
    }

    // ---------------------------------------------------------------- Geräte-Editor

    private DeviceConfig? Selected => _deviceList.SelectedItem as DeviceConfig;

    private void LoadEditor()
    {
        _loading = true;
        var device = Selected;
        _editor.Enabled = device is not null;
        _name.Text = device?.Name ?? "";
        _kind.SelectedIndex = device is null ? -1 : Array.FindIndex(KindLabels, k => k.Kind == device.Kind);
        _nameMatch.Text = device?.NameMatch ?? "";
        _bluetooth.Checked = device?.Bluetooth ?? false;
        _disconnect.Checked = device?.DisconnectWhenInactive ?? false;
        _communications.Checked = device?.SetCommunicationsRole ?? false;
        _endpointLabel.Text = device is null ? "" : DescribeEndpoint(device);
        _loading = false;
    }

    private string DescribeEndpoint(DeviceConfig device)
    {
        var endpoint = DeviceResolver.Resolve(device, _endpoints());
        if (endpoint is null)
            return "⚠ nicht gefunden";
        var state = endpoint.State switch
        {
            EndpointState.Active => "aktiv",
            EndpointState.Unplugged => endpoint.IsBluetooth ? "getrennt" : "ausgesteckt",
            EndpointState.Disabled => "deaktiviert",
            _ => "nicht vorhanden",
        };
        return $"{endpoint.FriendlyName}  ·  {state}";
    }

    private void EditSelected(Action<DeviceConfig> edit, bool refreshList = false)
    {
        if (_loading || Selected is not { } device)
            return;
        edit(device);
        if (refreshList)
            _deviceList.Invalidate();
    }

    private void AddDevice()
    {
        var known = _config.Devices.Select(d => d.EndpointId).Where(id => id is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var picker = new EndpointPickerDialog(known);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is not { } endpoint)
            return;

        var device = ConfigStore.FromEndpoint(endpoint);
        _config.Devices.Add(device);
        _deviceList.Items.Add(device);
        _deviceList.SelectedItem = device;
        _name.Focus();
        _name.SelectAll();
    }

    private void ReassignEndpoint()
    {
        if (Selected is not { } device)
            return;
        using var picker = new EndpointPickerDialog([]);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is not { } endpoint)
            return;
        device.EndpointId = endpoint.Id;
        device.Bluetooth = endpoint.IsBluetooth;
        device.NameMatch = ConfigStore.ShortName(endpoint.FriendlyName);
        LoadEditor();
        _endpointLabel.Text = $"{endpoint.FriendlyName}";
        _deviceList.Invalidate();
    }

    private void RemoveDevice()
    {
        if (Selected is not { } device)
            return;
        var index = _deviceList.SelectedIndex;
        _config.Devices.Remove(device);
        _deviceList.Items.RemoveAt(index);
        if (_deviceList.Items.Count > 0)
            _deviceList.SelectedIndex = Math.Min(index, _deviceList.Items.Count - 1);
        else
            LoadEditor();
    }

    private void MoveDevice(int delta)
    {
        var index = _deviceList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _config.Devices.Count)
            return;

        (_config.Devices[index], _config.Devices[target]) = (_config.Devices[target], _config.Devices[index]);
        _loading = true;
        (_deviceList.Items[index], _deviceList.Items[target]) = (_deviceList.Items[target], _deviceList.Items[index]);
        _loading = false;
        _deviceList.SelectedIndex = target;
    }

    private void DrawDeviceItem(object? sender, DrawItemEventArgs e)
    {
        var g = e.Graphics;
        Theme.Prepare(g);
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Theme.WithAlpha(Theme.Accent, 255) : Theme.PanelBackground))
            g.FillRectangle(back, e.Bounds);
        if (e.Index < 0 || _deviceList.Items[e.Index] is not DeviceConfig device)
            return;

        var iconRect = new RectangleF(e.Bounds.Left + 10, e.Bounds.Top, 34, e.Bounds.Height);
        using (var iconFont = Theme.PixelFont(Theme.IconFont, 18 * DeviceDpi / 96f))
            Theme.DrawTextCentered(g, Theme.GlyphFor(device.Kind), iconFont, Theme.TextPrimary, iconRect);

        var text = string.IsNullOrWhiteSpace(device.Name) ? "(ohne Namen)" : device.Name;
        TextRenderer.DrawText(g, text, Font, new Rectangle(e.Bounds.Left + 52, e.Bounds.Top, e.Bounds.Width - 90, e.Bounds.Height),
            Theme.TextPrimary, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (device.Bluetooth)
        {
            var btRect = new RectangleF(e.Bounds.Right - 34, e.Bounds.Top, 26, e.Bounds.Height);
            using var btFont = Theme.PixelFont(Theme.IconFont, 14 * DeviceDpi / 96f);
            Theme.DrawTextCentered(g, Theme.GlyphBluetooth, btFont, selected ? Color.White : Theme.TextSecondary, btRect);
        }
    }

    // ---------------------------------------------------------------- Helfer

    private static TableLayoutPanel NewGrid(int columns) => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        ColumnCount = columns,
        RowCount = 0,
    };

    private static void AddRow(TableLayoutPanel grid, string label, Control control, string? hint = null)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var labelControl = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 10, 12, 10),
            UseMnemonic = true,
        };
        grid.Controls.Add(labelControl, 0, row);

        control.Margin = new Padding(0, 7, 0, 7);
        if (control is CheckBox cb)
            cb.AutoSize = true;
        if (hint is null)
        {
            grid.Controls.Add(control, 1, row);
            return;
        }

        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty, Dock = DockStyle.Fill };
        stack.Controls.Add(control);
        stack.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Theme.TextSecondary, Margin = new Padding(0, 0, 0, 6) });
        grid.Controls.Add(stack, 1, row);
    }

    private static Control WithValue(TrackBar bar, Label value)
    {
        bar.Width = 320;
        bar.AutoSize = false;
        bar.Height = 36;
        value.AutoSize = true;
        value.Anchor = AnchorStyles.Left;
        value.Margin = new Padding(8, 8, 0, 0);
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        flow.Controls.Add(bar);
        flow.Controls.Add(value);
        return flow;
    }

    private Button MakeButton(string text, bool primary = false, int width = 140)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(width, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Theme.Accent : Theme.InputBackground,
            ForeColor = Color.White,
            Margin = new Padding(8, 0, 0, 0),
            UseMnemonic = true,
        };
        button.FlatAppearance.BorderColor = primary ? Theme.Accent : Color.FromArgb(70, 70, 82);
        if (primary)
            button.Font = new Font(Font, FontStyle.Bold);
        return button;
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "BT-Switcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    internal static void ApplyDarkTheme(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox or ComboBox or NumericUpDown or ListBox:
                    c.BackColor = c is ListBox ? Theme.PanelBackground : Theme.InputBackground;
                    c.ForeColor = Theme.TextPrimary;
                    if (c is TextBox tb) tb.BorderStyle = BorderStyle.FixedSingle;
                    if (c is ComboBox combo) combo.FlatStyle = FlatStyle.Flat;
                    if (c is NumericUpDown num) num.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case TrackBar:
                    c.BackColor = c.Parent?.BackColor ?? Theme.WindowBackground;
                    break;
                case CheckBox or RadioButton:
                    c.ForeColor = Theme.TextPrimary;
                    break;
            }
            ApplyDarkTheme(c);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle);
    }
}
