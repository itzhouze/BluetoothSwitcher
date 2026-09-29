using System.Drawing.Drawing2D;
using BluetoothSwitcher.Audio;

namespace BluetoothSwitcher.UI;

internal enum RowStatus
{
    None,
    Busy,
    Success,
    Failed,
}

internal sealed class OverlayRow(DeviceState state)
{
    public DeviceState State { get; set; } = state;
    public RowStatus Status { get; set; }
    public string? Message { get; set; }
}

internal sealed class CardModel
{
    public List<OverlayRow> Rows { get; } = [];

    /// <summary>Animierte Position der Auswahl (Zeilenindex, fließend).</summary>
    public float Selection { get; set; }

    public int SelectedIndex { get; set; }
    public float SpinnerAngle { get; set; }
}

/// <summary>Maße der Card – alles relativ zur Monitorhöhe, damit 1080p und 4K gleich aussehen.</summary>
internal sealed class CardLayout
{
    public float S { get; private init; }
    public float Margin { get; private init; }
    public float Padding { get; private init; }
    public float RowHeight { get; private init; }
    public float RowGap { get; private init; }
    public float FooterHeight { get; private init; }
    public float Radius { get; private init; }
    public RectangleF Card { get; private init; }
    public float RowsTop { get; private init; }
    public Size SurfaceSize { get; private init; }

    /// <summary>Identifiziert gleiche Layouts (für den Chrome-Cache).</summary>
    public string Key => $"{SurfaceSize.Width}x{SurfaceSize.Height}|{S:0.###}|{RowHeight:0.#}";

    public static CardLayout Compute(Size monitor, double scale, int rowCount)
    {
        var s = (float)(monitor.Height / 1080.0 * scale);
        var n = Math.Max(1, rowCount);

        var margin = 48 * s;           // Platz für den Schatten
        var padding = 36 * s;
        var rowsInset = 16 * s;        // gleicher Abstand oben wie links/rechts
        var rowGap = 8 * s;
        var footerGap = 22 * s;
        var footer = 64 * s;
        var fixedHeight = rowsInset + footerGap + footer + padding * 0.7f;

        var rowHeight = 104 * s;
        var maxHeight = monitor.Height * 0.85f;
        if (fixedHeight + n * rowHeight + (n - 1) * rowGap > maxHeight)
            rowHeight = Math.Max(56 * s, (maxHeight - fixedHeight - (n - 1) * rowGap) / n);

        var width = Math.Clamp(monitor.Width * 0.40f, 780 * s, 1100 * s);
        width = Math.Min(width, monitor.Width * 0.92f);
        var height = fixedHeight + n * rowHeight + (n - 1) * rowGap;

        var card = new RectangleF(margin, margin, width, height);
        return new CardLayout
        {
            S = s,
            Margin = margin,
            Padding = padding,
            RowHeight = rowHeight,
            RowGap = rowGap,
            FooterHeight = footer,
            Radius = 26 * s,
            Card = card,
            RowsTop = card.Top + rowsInset,
            SurfaceSize = new Size((int)Math.Ceiling(width + margin * 2), (int)Math.Ceiling(height + margin * 2)),
        };
    }

    public RectangleF RowRect(float index) => new(
        Card.Left + 16 * S,
        RowsTop + index * (RowHeight + RowGap),
        Card.Width - 32 * S,
        RowHeight);
}

internal static class OverlayRenderer
{
    /// <summary>Komplette Card (Chrome + Inhalt) – für Vorschau/Warmup.</summary>
    public static void Render(Graphics g, CardLayout l, CardModel m)
    {
        RenderChrome(g, l, m);
        RenderContent(g, l, m);
    }

    /// <summary>
    /// Statischer Teil: Schatten, Hintergrund, Rahmen, Footer – hängt nur vom Layout ab.
    /// Wird pro Layout einmal gerendert und danach nur noch geblittet.
    /// </summary>
    public static void RenderChrome(Graphics g, CardLayout l, CardModel m)
    {
        var s = l.S;
        var card = l.Card;
        Theme.DrawShadow(g, card, l.Radius, 34 * s, 10 * s);
        Theme.FillRounded(g, card, l.Radius, Theme.CardBackground);
        Theme.StrokeRounded(g, card, l.Radius, Theme.CardBorder, Math.Max(1f, 1.2f * s));
        DrawFooter(g, l);
    }

    /// <summary>Dynamischer Teil: Auswahl und Gerätezeilen (pro Frame).</summary>
    public static void RenderContent(Graphics g, CardLayout l, CardModel m)
    {
        if (m.Rows.Count == 0)
        {
            DrawEmpty(g, l);
            return;
        }

        DrawSelection(g, l, m.Selection);
        for (var i = 0; i < m.Rows.Count; i++)
            DrawRow(g, l, m.Rows[i], i, i == m.SelectedIndex, m.SpinnerAngle);
    }

    private static void DrawSelection(Graphics g, CardLayout l, float selection)
    {
        var s = l.S;
        var r = l.RowRect(selection);
        var radius = 18 * s;

        using (var brush = new LinearGradientBrush(r, Theme.WithAlpha(Theme.Accent, 70), Theme.WithAlpha(Theme.Accent, 28), LinearGradientMode.Horizontal))
        using (var path = Theme.RoundedRect(r, radius))
            g.FillPath(brush, path);
        Theme.StrokeRounded(g, r, radius, Theme.WithAlpha(Theme.Accent, 110), Math.Max(1f, 1.5f * s));

        var bar = new RectangleF(r.Left + 8 * s, r.Top + r.Height * 0.24f, 6 * s, r.Height * 0.52f);
        Theme.FillRounded(g, bar, 3 * s, Theme.Accent);
    }

    private static void DrawRow(Graphics g, CardLayout l, OverlayRow row, int index, bool selected, float spinnerAngle)
    {
        var s = l.S;
        var r = l.RowRect(index);
        var state = row.State;
        var availability = state.Availability;
        var unavailable = availability == Availability.Unavailable && row.Status == RowStatus.None;
        var compact = l.RowHeight < 80 * s;

        // Icon-Badge
        var badgeSize = Math.Min(60 * s, r.Height - 24 * s);
        var badge = new RectangleF(r.Left + 30 * s, r.Top + (r.Height - badgeSize) / 2, badgeSize, badgeSize);
        var badgeColor = state.IsDefault
            ? Theme.WithAlpha(Theme.Success, 46)
            : selected ? Theme.WithAlpha(Theme.Accent, 80) : Color.FromArgb(22, 255, 255, 255);
        using (var brush = new SolidBrush(badgeColor))
            g.FillEllipse(brush, badge);
        using (var icon = Theme.PixelFont(Theme.IconFont, badgeSize * 0.46f))
        {
            var iconColor = unavailable ? Theme.TextDisabled : state.IsDefault ? Theme.Success : Theme.TextPrimary;
            Theme.DrawTextCentered(g, Theme.GlyphFor(state.Config.Kind), icon, iconColor, badge);
        }

        // Rechte Seite: Pills / Spinner (von rechts nach links)
        var right = r.Right - 24 * s;
        var center = r.Top + r.Height / 2;
        right = DrawRightSide(g, l, row, right, center, spinnerAngle);

        // Name + Status
        var textLeft = badge.Right + 24 * s;
        var textWidth = Math.Max(10, right - 16 * s - textLeft);
        var nameColor = unavailable ? Theme.TextDisabled : selected ? Color.White : Theme.TextPrimary;
        var (statusText, statusColor) = StatusLine(row);

        using var nameFont = Theme.PixelFont(Theme.TextFont, (compact ? 28 : 33) * s, FontStyle.Bold);
        if (compact)
        {
            Theme.DrawTextLeft(g, state.Config.Name, nameFont, nameColor, textLeft, center, textWidth);
            return;
        }

        using var statusFont = Theme.PixelFont(Theme.TextFontSmall, 20 * s);
        Theme.DrawTextLeft(g, state.Config.Name, nameFont, nameColor, textLeft, center - 14 * s, textWidth);
        Theme.DrawTextLeft(g, statusText, statusFont, statusColor, textLeft, center + 22 * s, textWidth);
    }

    private static float DrawRightSide(Graphics g, CardLayout l, OverlayRow row, float right, float center, float spinnerAngle)
    {
        var s = l.S;
        switch (row.Status)
        {
            case RowStatus.Busy:
            {
                var d = 34 * s;
                var rect = new RectangleF(right - d, center - d / 2, d, d);
                using var track = new Pen(Theme.WithAlpha(Theme.Accent, 50), 4 * s);
                using var pen = new Pen(Theme.Accent, 4 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawEllipse(track, rect);
                g.DrawArc(pen, rect, spinnerAngle, 100);
                return rect.Left - 14 * s;
            }
            case RowStatus.Success:
                return DrawGlyphCircle(g, Theme.GlyphCheck, Theme.Success, right, center, s);
            case RowStatus.Failed:
                return DrawGlyphCircle(g, Theme.GlyphError, Theme.Danger, right, center, s);
        }

        if (row.State.IsDefault)
            right = DrawPill(g, "AKTIV", null, Theme.Success, right, center, s) - 10 * s;
        // Von rechts nach links zeichnen → rückwärts iterieren, damit L links von R steht.
        for (var i = row.State.Batteries.Count - 1; i >= 0; i--)
        {
            var battery = row.State.Batteries[i];
            var percent = battery.Percent;
            var color = percent < 20 ? Theme.Danger : percent < 40 ? Theme.Warning : Theme.TextSecondary;
            right = DrawPill(g, $"{percent} %", Theme.BatteryGlyph(percent), color, right, center, s, battery.Side) - 10 * s;
        }
        if (row.State.SignalBars is { } bars)
            right = DrawSignal(g, bars, right, center, s) - 10 * s;
        return right;
    }

    /// <summary>Funksignal als 4 ansteigende Balken in einer Pill (gefüllt = Stärke).</summary>
    /// <returns>Linke Kante der Pill.</returns>
    private static float DrawSignal(Graphics g, int bars, float right, float center, float s)
    {
        var color = bars >= 3 ? Theme.TextSecondary : bars == 2 ? Theme.Warning : Theme.Danger;
        var fill = bars >= 3 ? Theme.TextPrimary : color; // gefüllte Balken deutlich heller als leere
        var h = 36 * s;
        var barWidth = 4.5f * s;
        var gap = 3f * s;
        var w = 4 * barWidth + 3 * gap + 28 * s;
        var rect = new RectangleF(right - w, center - h / 2, w, h);

        Theme.FillRounded(g, rect, h / 2, Theme.WithAlpha(color, 36));
        Theme.StrokeRounded(g, rect, h / 2, Theme.WithAlpha(color, 80), Math.Max(1f, 1.2f * s));

        var bottom = center + 9 * s;
        var x = rect.Left + 14 * s;
        for (var i = 0; i < 4; i++)
        {
            var barHeight = (6 + i * 4) * s;
            var bar = new RectangleF(x, bottom - barHeight, barWidth, barHeight);
            Theme.FillRounded(g, bar, barWidth / 2, i < bars ? fill : Color.FromArgb(38, 255, 255, 255));
            x += barWidth + gap;
        }
        return rect.Left;
    }

    private static float DrawGlyphCircle(Graphics g, string glyph, Color color, float right, float center, float s)
    {
        var d = 38 * s;
        var rect = new RectangleF(right - d, center - d / 2, d, d);
        using (var brush = new SolidBrush(Theme.WithAlpha(color, 50)))
            g.FillEllipse(brush, rect);
        using var font = Theme.PixelFont(Theme.IconFont, 17 * s);
        Theme.DrawTextCentered(g, glyph, font, color, rect);
        return rect.Left - 14 * s;
    }

    /// <param name="side">Optionales Kürzel ("L"/"R") als runder Badge am Anfang der Pill.</param>
    /// <returns>Linke Kante der Pill.</returns>
    private static float DrawPill(Graphics g, string text, string? glyph, Color color, float right, float center, float s,
        string? side = null)
    {
        using var font = Theme.PixelFont(Theme.TextFontSmall, 18 * s, FontStyle.Bold);
        using var iconFont = Theme.PixelFont(Theme.IconFont, 18 * s);
        var textSize = Theme.Measure(g, text, font);
        var glyphWidth = glyph is null ? 0 : Theme.Measure(g, glyph, iconFont).Width + 8 * s;
        var h = 36 * s;
        var badge = h - 10 * s;
        var badgeWidth = side is null ? 0 : badge + 8 * s;
        var padLeft = side is null ? 15 * s : 5 * s;
        var w = padLeft + badgeWidth + glyphWidth + textSize.Width + 15 * s;
        var rect = new RectangleF(right - w, center - h / 2, w, h);

        Theme.FillRounded(g, rect, h / 2, Theme.WithAlpha(color, 36));
        Theme.StrokeRounded(g, rect, h / 2, Theme.WithAlpha(color, 80), Math.Max(1f, 1.2f * s));

        var x = rect.Left + padLeft;
        if (side is not null)
        {
            var circle = new RectangleF(x, center - badge / 2, badge, badge);
            using (var brush = new SolidBrush(Theme.WithAlpha(color, 90)))
                g.FillEllipse(brush, circle);
            using var sideFont = Theme.PixelFont(Theme.TextFontSmall, 15 * s, FontStyle.Bold);
            Theme.DrawTextCentered(g, side, sideFont, Color.White, circle);
            x += badgeWidth;
        }
        if (glyph is not null)
        {
            Theme.DrawTextLeft(g, glyph, iconFont, color, x, center, glyphWidth + 4 * s);
            x += glyphWidth;
        }
        Theme.DrawTextLeft(g, text, font, color, x, center, textSize.Width + 8 * s);
        return rect.Left;
    }

    private static (string Text, Color Color) StatusLine(OverlayRow row)
    {
        switch (row.Status)
        {
            case RowStatus.Busy:
                return (row.Message ?? "Verbinde …", Theme.Accent);
            case RowStatus.Success:
                return (row.Message ?? "Ton läuft jetzt hier", Theme.Success);
            case RowStatus.Failed:
                return (row.Message ?? "Hat nicht geklappt", Theme.Danger);
        }

        return row.State.Availability switch
        {
            Availability.Connected when row.State.IsDefault => ("Ton läuft hier", Theme.TextSecondary),
            Availability.Connected => ("Bereit", Theme.TextSecondary),
            Availability.Disconnected => ("Nicht verbunden – wird beim Auswählen verbunden", Theme.TextSecondary),
            _ when row.State.Endpoint is null => ("Nicht gefunden", Theme.TextDisabled),
            _ when row.State.Endpoint.State == EndpointState.Disabled => ("In Windows ausgeschaltet", Theme.TextDisabled),
            _ => ("Nicht angeschlossen", Theme.TextDisabled),
        };
    }

    private static void DrawEmpty(Graphics g, CardLayout l)
    {
        var s = l.S;
        var r = l.RowRect(0);
        using var font = Theme.PixelFont(Theme.TextFont, 28 * s, FontStyle.Bold);
        using var small = Theme.PixelFont(Theme.TextFontSmall, 20 * s);
        Theme.DrawTextCentered(g, "Noch keine Geräte eingerichtet", font, Theme.TextPrimary, r with { Height = r.Height * 0.6f });
        Theme.DrawTextCentered(g, "Rechtsklick aufs Tray-Symbol → Einstellungen", small, Theme.TextSecondary,
            r with { Y = r.Y + r.Height * 0.5f, Height = r.Height * 0.5f });
    }

    private static void DrawFooter(Graphics g, CardLayout l)
    {
        var s = l.S;
        var top = l.Card.Bottom - l.Padding * 0.7f - l.FooterHeight;
        using (var pen = new Pen(Color.FromArgb(22, 255, 255, 255), Math.Max(1f, s)))
            g.DrawLine(pen, l.Card.Left + l.Padding, top, l.Card.Right - l.Padding, top);

        (string Key, string Label)[] hints = [("↑ ↓", "Auswählen"), ("Enter", "Umschalten"), ("Esc", "Schließen")];

        using var keyFont = Theme.PixelFont(Theme.TextFontSmall, 17 * s, FontStyle.Bold);
        using var labelFont = Theme.PixelFont(Theme.TextFontSmall, 19 * s);
        var capH = 34 * s;
        var capPad = 13 * s;
        var gapInner = 10 * s;
        var gapOuter = 34 * s;

        var widths = hints.Select(h => (
            Cap: Theme.Measure(g, h.Key, keyFont).Width + capPad * 2,
            Label: Theme.Measure(g, h.Label, labelFont).Width)).ToArray();
        var total = widths.Sum(w => w.Cap + gapInner + w.Label) + gapOuter * (hints.Length - 1);

        var x = l.Card.Left + (l.Card.Width - total) / 2;
        var center = top + l.FooterHeight / 2 + 4 * s;
        for (var i = 0; i < hints.Length; i++)
        {
            var cap = new RectangleF(x, center - capH / 2, widths[i].Cap, capH);
            Theme.FillRounded(g, cap, 8 * s, Color.FromArgb(20, 255, 255, 255));
            Theme.StrokeRounded(g, cap, 8 * s, Color.FromArgb(56, 255, 255, 255), Math.Max(1f, s));
            // "Tastenkante" unten
            using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255), Math.Max(1f, 2 * s)))
                g.DrawLine(pen, cap.Left + 6 * s, cap.Bottom - 1 * s, cap.Right - 6 * s, cap.Bottom - 1 * s);
            Theme.DrawTextCentered(g, hints[i].Key, keyFont, Theme.TextPrimary, cap);
            x = cap.Right + gapInner;
            Theme.DrawTextLeft(g, hints[i].Label, labelFont, Theme.TextSecondary, x, center, widths[i].Label + 10 * s);
            x += widths[i].Label + gapOuter;
        }
    }
}
