using BluetoothSwitcher.Audio;
using BluetoothSwitcher.Config;
using BluetoothSwitcher.Logging;

namespace BluetoothSwitcher.UI;

/// <summary>Auswahl eines Windows-Audio-Ausgabegeräts (inkl. gepairter, aber getrennter BT-Geräte).</summary>
internal sealed class EndpointPickerDialog : Form
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, BorderStyle = BorderStyle.None };
    private readonly CheckBox _showAll = new() { Text = "&Alle Geräte anzeigen (auch virtuelle / deaktivierte)", AutoSize = true };
    private readonly HashSet<string> _alreadyConfigured;
    private List<EndpointInfo> _all = [];
    private Font? _boldFont;

    public EndpointInfo? Selected => _list.SelectedItem as EndpointInfo;

    public EndpointPickerDialog(HashSet<string?> alreadyConfigured)
    {
        _alreadyConfigured = alreadyConfigured.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        Text = "Audiogerät auswählen";
        Font = new Font(Theme.TextFontSmall, 10.5f);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 480);
        MinimizeBox = MaximizeBox = false;
        ShowIcon = ShowInTaskbar = false;
        BackColor = Theme.WindowBackground;
        ForeColor = Theme.TextPrimary;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(20) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        _list.ItemHeight = 54;
        _list.BackColor = Theme.PanelBackground;
        _list.DrawItem += DrawItem;
        _list.DoubleClick += (_, _) => Accept();
        root.Controls.Add(_list, 0, 0);

        _showAll.Margin = new Padding(0, 12, 0, 0);
        _showAll.CheckedChanged += (_, _) => Fill();
        root.Controls.Add(_showAll, 0, 1);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var ok = new Button { Text = "Übernehmen", Size = new Size(140, 40), FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White };
        var cancel = new Button { Text = "Abbrechen", Size = new Size(140, 40), FlatStyle = FlatStyle.Flat, BackColor = Theme.InputBackground, ForeColor = Color.White, DialogResult = DialogResult.Cancel };
        ok.FlatAppearance.BorderColor = Theme.Accent;
        cancel.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 82);
        ok.Click += (_, _) => Accept();
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 2);
        AcceptButton = ok;
        CancelButton = cancel;

        try
        {
            _all = AudioEndpointService.GetRenderEndpoints();
        }
        catch (Exception ex)
        {
            Log.Error("Audiogeräte konnten nicht gelesen werden", ex);
        }
        Fill();
    }

    private void Fill()
    {
        var items = _showAll.Checked ? _all : ConfigStore.SuggestEndpoints(_all).ToList();
        _list.Items.Clear();
        foreach (var endpoint in items
                     .OrderBy(e => _alreadyConfigured.Contains(e.Id))
                     .ThenBy(e => e.State)
                     .ThenBy(e => e.FriendlyName))
            _list.Items.Add(endpoint);
        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;
    }

    private void Accept()
    {
        if (Selected is null)
            return;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void DrawItem(object? sender, DrawItemEventArgs e)
    {
        var g = e.Graphics;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Theme.Accent : Theme.PanelBackground))
            g.FillRectangle(back, e.Bounds);
        if (e.Index < 0 || _list.Items[e.Index] is not EndpointInfo endpoint)
            return;

        var state = endpoint.State switch
        {
            EndpointState.Active => "aktiv",
            EndpointState.Unplugged => endpoint.IsBluetooth ? "gepairt, getrennt" : "ausgesteckt",
            EndpointState.Disabled => "deaktiviert",
            _ => "nicht vorhanden",
        };
        var details = $"{(endpoint.IsBluetooth ? "Bluetooth" : "Kabel/HDMI/USB")} · {state}";
        if (_alreadyConfigured.Contains(endpoint.Id))
            details += " · bereits in der Liste";

        var half = e.Bounds.Height / 2;
        TextRenderer.DrawText(g, endpoint.FriendlyName, _boldFont ??= new Font(Font, FontStyle.Bold),
            new Rectangle(e.Bounds.Left + 14, e.Bounds.Top + 4, e.Bounds.Width - 20, half),
            Theme.TextPrimary, TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, details, Font,
            new Rectangle(e.Bounds.Left + 14, e.Bounds.Top + half, e.Bounds.Width - 20, half - 4),
            selected ? Color.White : Theme.TextSecondary, TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle);
    }
}
