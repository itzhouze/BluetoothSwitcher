#if DEBUG
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using BluetoothSwitcher.Audio;
using BluetoothSwitcher.Config;

namespace BluetoothSwitcher.UI;

/// <summary>Nur Debug: rendert das Overlay mit Beispieldaten als PNG (BTSwitcher.exe --preview &lt;ordner&gt;).</summary>
internal static class PreviewRenderer
{
    public static void Run(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var (w, h) in new[] { (1920, 1080), (3840, 2160) })
        {
            Render(Path.Combine(folder, $"overlay-{h}p.png"), w, h, SampleRows(), 1);
            Render(Path.Combine(folder, $"overlay-{h}p-1dev.png"), w, h, SampleRows().Take(1).ToList(), 0);
        }
        Render(Path.Combine(folder, "overlay-1080p-10dev.png"), 1920, 1080,
            Enumerable.Range(0, 10).Select(i => SampleRows()[i % 4]).ToList(), 2);
        RenderToasts(Path.Combine(folder, "toasts-1080p.png"));
    }

    private static void RenderToasts(string path)
    {
        const int width = 1920, height = 520;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, width, height), Color.FromArgb(30, 60, 110), Color.FromArgb(120, 40, 60), 35f))
            g.FillRectangle(bg, 0, 0, width, height);

        (ToastForm.Kind Kind, string Text, DeviceKind? Device)[] toasts =
        [
            (ToastForm.Kind.Success, "Ton läuft jetzt über Sony WH-1000XM4", DeviceKind.Headphones),
            (ToastForm.Kind.Error, "AirPods Pro nicht erreichbar – eingeschaltet und in der Nähe?", null),
            (ToastForm.Kind.Info, "Konfiguration neu geladen", null),
        ];
        var y = 10;
        foreach (var (kind, text, device) in toasts)
        {
            using var surface = ToastForm.RenderSurface(kind, text, device, new Size(1920, 1080), 1f, out _);
            g.DrawImage(surface.Bitmap, (width - surface.Width) / 2, y);
            y += surface.Height - 20;
        }
        bitmap.Save(path, ImageFormat.Png);
    }

    /// <summary>Lauscht 15 s auf Apple-BLE-Pakete und protokolliert Rohdaten + ausgewerteten Akku.</summary>
    public static void ScanAirPods(string path)
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var thread = new Thread(() =>
        {
            using var watcher = new AppleBatteryWatcher();
            watcher.RawApplePacket += (rssi, data) =>
            {
                var parsed = AppleBatteryWatcher.TryParseProximityPairing(data, out var b)
                    ? $"  → L {b.Left} R {b.Right} Case {b.Case} (lädt L:{b.LeftCharging} R:{b.RightCharging})"
                    : "";
                lines.Enqueue($"{DateTime.Now:HH:mm:ss.fff} RSSI {rssi,4} type 0x{(data.Length > 0 ? data[0] : 0):X2} {Convert.ToHexString(data)}{parsed}");
            };
            watcher.Start();
            lines.Enqueue($"läuft: {watcher.IsRunning}");
            Thread.Sleep(15000);
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        File.WriteAllLines(path, lines);
    }

    /// <summary>Prüft IPolicyConfig ohne Änderung: setzt das aktuelle Standardgerät erneut als Standard.</summary>
    public static void SelfTest(string path)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var before = AudioEndpointService.GetDefaultRenderId();
            sb.AppendLine($"Default vorher: {before}");
            if (before is not null)
            {
                AudioEndpointService.SetDefault(before, includeCommunications: false);
                sb.AppendLine("SetDefault (No-op) OK");
            }
            sb.AppendLine($"Default nachher: {AudioEndpointService.GetDefaultRenderId()}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            sb.AppendLine($"Ton läuft gerade: {PlaybackControl.IsAudioPlaying()} ({sw.ElapsedMilliseconds} ms)");
            foreach (var e in AudioEndpointService.GetRenderEndpoints().Where(e => e.IsBluetooth))
                sb.AppendLine($"BT: {e.FriendlyName} [{e.State}]");
        }
        catch (Exception ex)
        {
            sb.AppendLine("FEHLER: " + ex);
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void Render(string path, int width, int height, List<OverlayRow> rows, int selected)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        Theme.Prepare(g);

        // "Harbor" im Hintergrund simulieren
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, width, height), Color.FromArgb(30, 60, 110), Color.FromArgb(120, 40, 60), 35f))
            g.FillRectangle(bg, 0, 0, width, height);
        using (var font = Theme.PixelFont(Theme.TextFont, height * 0.08f, FontStyle.Bold))
            g.DrawString("Harbor – Film läuft", font, Brushes.White, width * 0.06f, height * 0.12f);
        using (var dim = new SolidBrush(Color.FromArgb((int)(0.78 * 255), 0, 0, 0)))
            g.FillRectangle(dim, 0, 0, width, height);

        var model = new CardModel { SelectedIndex = selected, Selection = selected, SpinnerAngle = 40 };
        model.Rows.AddRange(rows);
        var layout = CardLayout.Compute(new Size(width, height), 1.0, rows.Count);
        var x = (width - layout.SurfaceSize.Width) / 2;
        var y = (height - layout.SurfaceSize.Height) / 2;
        g.TranslateTransform(x, y);
        OverlayRenderer.Render(g, layout, model);
        g.ResetTransform();

        bitmap.Save(path, ImageFormat.Png);
    }

    private static List<OverlayRow> SampleRows()
    {
        EndpointInfo Ep(string name, EndpointState state, bool bt) => new(Guid.NewGuid().ToString(), name, state, Guid.NewGuid(), bt);
        DeviceConfig Dev(string name, DeviceKind kind, bool bt) => new() { Name = name, Kind = kind, Bluetooth = bt };

        return
        [
            new(new DeviceState(Dev("AirPods 4", DeviceKind.Earbuds, true), Ep("Kopfhörer (AirPods)", EndpointState.Active, true), true, [new BatteryLevel("L", 80), new BatteryLevel("R", 100)], SignalBars: 3)),
            new(new DeviceState(Dev("Sony WH-1000XM4", DeviceKind.Headphones, true), Ep("Kopfhörer (WH-1000XM4)", EndpointState.Active, true), false, [new BatteryLevel(null, 30)])),
            new(new DeviceState(Dev("LG Soundbar", DeviceKind.Soundbar, true), Ep("Lautsprecher (LG SN5)", EndpointState.Unplugged, true), false, []))
            {
                Status = RowStatus.Busy, Message = "Verbinde …",
            },
            new(new DeviceState(Dev("TV-Lautsprecher", DeviceKind.Tv, false), Ep("LG TV (HDMI)", EndpointState.Unplugged, false), false, [])),
        ];
    }
}
#endif
