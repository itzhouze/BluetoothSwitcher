using System.Diagnostics;
using BluetoothSwitcher.Config;

namespace BluetoothSwitcher.UI;

/// <summary>
/// Eigene Benachrichtigung oben mittig. Bewusst kein Windows-Toast: Der Fokus-Assistent unterdrückt
/// System-Benachrichtigungen, solange eine Fullscreen-App (Harbor) läuft.
/// Klick-durchlässig und nie aktiviert → stiehlt Harbor nie den Fokus.
/// </summary>
internal sealed class ToastForm : LayeredWindow
{
    private const int InMs = 220;
    private const int HoldMs = 3500;
    private const int OutMs = 320;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10 };
    private readonly Stopwatch _clock = new();

    private DibSurface? _surface;
    private Point _target;
    private float _slide;

    public ToastForm() : base(clickThrough: true)
    {
        _timer.Tick += (_, _) => Tick();
        CreateControl();
    }

    public enum Kind { Success, Error, Info }

    public void ShowToast(Kind kind, string text, DeviceKind? device, double scale)
    {
        var monitor = Screen.FromHandle(FocusHelper.Capture()).Bounds;
        var s = (float)(monitor.Height / 1080.0 * scale);

        _surface?.Dispose();
        _surface = RenderSurface(kind, text, device, monitor.Size, s, out var margin);

        _target = new Point(
            monitor.Left + (monitor.Width - _surface.Width) / 2,
            monitor.Top + (int)(monitor.Height * 0.05f) - (int)margin);
        _slide = 24 * s;

        _clock.Restart();
        Render(0);
        if (!Visible)
            Show();
        BringToTopNoActivate();
        _timer.Start();
    }

    /// <summary>Zeichnet den Toast (inkl. Schattenrand) in eine neue Surface.</summary>
    internal static DibSurface RenderSurface(Kind kind, string text, DeviceKind? device, Size monitor, float s, out float margin)
    {
        using var font = Theme.PixelFont(Theme.TextFont, 27 * s, FontStyle.Bold);
        margin = 36 * s;
        var height = 84 * s;
        var iconSize = 48 * s;
        var padLeft = 18 * s;
        var padRight = 32 * s;
        var gap = 18 * s;

        float textWidth;
        using (var measure = Graphics.FromHwnd(IntPtr.Zero))
            textWidth = Math.Min(Theme.Measure(measure, text, font).Width + 4 * s, monitor.Width * 0.6f);

        var width = padLeft + iconSize + gap + textWidth + padRight;
        var surface = new DibSurface((int)Math.Ceiling(width + margin * 2), (int)Math.Ceiling(height + margin * 2));

        var color = kind switch
        {
            Kind.Success => Theme.Success,
            Kind.Error => Theme.Danger,
            _ => Theme.Accent,
        };
        var glyph = kind switch
        {
            Kind.Success => device is { } d ? Theme.GlyphFor(d) : Theme.GlyphCheck,
            Kind.Error => Theme.GlyphError,
            _ => Theme.GlyphHeadphones,
        };

        using var g = surface.CreateGraphics();
        var card = new RectangleF(margin, margin, width, height);
        var radius = height / 2;
        Theme.DrawShadow(g, card, radius, 26 * s, 8 * s, maxAlpha: 120);
        Theme.FillRounded(g, card, radius, Theme.CardBackground);
        Theme.StrokeRounded(g, card, radius, Theme.WithAlpha(color, 110), Math.Max(1f, 1.5f * s));

        var icon = new RectangleF(card.Left + padLeft, card.Top + (height - iconSize) / 2, iconSize, iconSize);
        using (var brush = new SolidBrush(Theme.WithAlpha(color, 52)))
            g.FillEllipse(brush, icon);
        using (var iconFont = Theme.PixelFont(Theme.IconFont, iconSize * 0.46f))
            Theme.DrawTextCentered(g, glyph, iconFont, color, icon);

        Theme.DrawTextLeft(g, text, font, Theme.TextPrimary, icon.Right + gap, card.Top + height / 2, textWidth + 8 * s);
        return surface;
    }

    private void Tick()
    {
        var t = _clock.Elapsed.TotalMilliseconds;
        if (t < InMs)
            Render((float)(t / InMs));
        else if (t < InMs + HoldMs)
        {
            Render(1);
            _timer.Interval = 100; // Haltephase: kaum CPU
        }
        else if (t < InMs + HoldMs + OutMs)
        {
            _timer.Interval = 10;
            Render(1 - (float)((t - InMs - HoldMs) / OutMs));
        }
        else
            Finish();
    }

    private void Render(float progress)
    {
        if (_surface is null)
            return;
        var e = 1 - MathF.Pow(1 - Math.Clamp(progress, 0, 1), 3);
        var location = _target with { Y = _target.Y - (int)(_slide * (1 - e)) };
        Present(_surface, location, (byte)(255 * e));
    }

    private void Finish()
    {
        _timer.Stop();
        _timer.Interval = 10;
        Hide();
        _surface?.Dispose();
        _surface = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _surface?.Dispose();
        }
        base.Dispose(disposing);
    }
}
