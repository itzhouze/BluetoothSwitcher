using System.Drawing.Drawing2D;
using System.Drawing.Text;
using BluetoothSwitcher.Config;

namespace BluetoothSwitcher.UI;

/// <summary>Farben, Schriften, Icons und Zeichen-Helfer für den dunklen TV-Look.</summary>
internal static class Theme
{
    public static readonly Color CardBackground = Color.FromArgb(246, 28, 28, 34);
    public static readonly Color CardBorder = Color.FromArgb(34, 255, 255, 255);
    public static readonly Color TextPrimary = Color.FromArgb(255, 245, 245, 247);
    public static readonly Color TextSecondary = Color.FromArgb(255, 160, 160, 172);
    public static readonly Color TextDisabled = Color.FromArgb(255, 96, 96, 106);
    public static readonly Color Accent = Color.FromArgb(255, 76, 141, 255);
    public static readonly Color Success = Color.FromArgb(255, 61, 220, 132);
    public static readonly Color Danger = Color.FromArgb(255, 255, 90, 95);
    public static readonly Color Warning = Color.FromArgb(255, 255, 196, 64);

    // Settings-Fenster (opak)
    public static readonly Color WindowBackground = Color.FromArgb(24, 24, 29);
    public static readonly Color PanelBackground = Color.FromArgb(34, 34, 41);
    public static readonly Color InputBackground = Color.FromArgb(44, 44, 53);

    public static readonly string TextFont = PickFont("Segoe UI Variable Display", "Segoe UI");
    public static readonly string TextFontSmall = PickFont("Segoe UI Variable Text", "Segoe UI");
    public static readonly string IconFont = PickFont("Segoe Fluent Icons", "Segoe MDL2 Assets");

    // Glyphen (Segoe Fluent Icons / MDL2 Assets)
    public const string GlyphHeadphones = "";

    /// <summary>Earbuds (AirPods-Form) – nur in Segoe Fluent Icons (Windows 11), sonst Kopfhörer.</summary>
    public static readonly string GlyphEarbuds = IconFont == "Segoe Fluent Icons" ? "" : GlyphHeadphones;

    public const string GlyphSpeaker = "";
    public const string GlyphSoundbar = "";
    public const string GlyphTv = "";
    public const string GlyphBluetooth = "";
    public const string GlyphCheck = "";
    public const string GlyphError = "";

    public static string GlyphFor(DeviceKind kind) => kind switch
    {
        DeviceKind.Headphones => GlyphHeadphones,
        DeviceKind.Earbuds => GlyphEarbuds,
        DeviceKind.Speaker => GlyphSpeaker,
        DeviceKind.Soundbar => GlyphSoundbar,
        DeviceKind.Tv => GlyphTv,
        _ => GlyphSpeaker,
    };

    /// <summary>Akku-Glyph Battery0..Battery10 (E850–E859, E83F).</summary>
    public static string BatteryGlyph(byte percent)
    {
        var step = Math.Clamp((int)Math.Round(percent / 10.0), 0, 10);
        return step == 10 ? "" : ((char)(0xE850 + step)).ToString();
    }

    public static Color WithAlpha(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);

    public static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, RectangleF r, float radius, Color color)
    {
        using var path = RoundedRect(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void StrokeRounded(Graphics g, RectangleF r, float radius, Color color, float width = 1f)
    {
        using var path = RoundedRect(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    /// <summary>
    /// Weicher Schlagschatten aus mehreren transparenten Lagen. Wird in 1/6-Auflösung gerendert und
    /// hochskaliert – ~36× weniger Pixelarbeit und durch die Interpolation zusätzlich weichgezeichnet.
    /// </summary>
    public static void DrawShadow(Graphics g, RectangleF r, float radius, float spread, float offsetY, int layers = 14, int maxAlpha = 110)
    {
        const int factor = 6;
        var outer = RectangleF.Inflate(r, spread + factor, spread + factor);
        outer.Offset(0, offsetY);
        var w = (int)Math.Ceiling(outer.Width / factor);
        var h = (int)Math.Ceiling(outer.Height / factor);

        using var small = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var sg = Graphics.FromImage(small))
        {
            sg.SmoothingMode = SmoothingMode.AntiAlias;
            sg.ScaleTransform(1f / factor, 1f / factor);
            sg.TranslateTransform(-outer.X, -outer.Y);

            var alphaPerLayer = Math.Max(1, maxAlpha / layers);
            for (var i = layers; i >= 1; i--)
            {
                var grow = spread * i / layers;
                var rect = RectangleF.Inflate(r, grow, grow);
                rect.Offset(0, offsetY);
                FillRounded(sg, rect, radius + grow, Color.FromArgb(alphaPerLayer, 0, 0, 0));
            }
        }

        var state = g.Save();
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(small, new RectangleF(outer.X, outer.Y, w * factor, h * factor));
        g.Restore(state);
    }

    public static Font PixelFont(string family, float px, FontStyle style = FontStyle.Regular) =>
        new(family, Math.Max(1f, px), style, GraphicsUnit.Pixel);

    public static SizeF Measure(Graphics g, string text, Font font) =>
        g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);

    /// <summary>Zeichnet Text links, vertikal zentriert auf <paramref name="centerY"/>.</summary>
    public static void DrawTextLeft(Graphics g, string text, Font font, Color color, float x, float centerY, float maxWidth)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
            LineAlignment = StringAlignment.Center,
        };
        var h = font.GetHeight(g) * 1.3f;
        g.DrawString(text, font, brush, new RectangleF(x, centerY - h / 2, maxWidth, h), format);
    }

    public static void DrawTextCentered(Graphics g, string text, Font font, Color color, RectangleF rect)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(text, font, brush, rect, format);
    }

    private static string PickFont(params string[] candidates)
    {
        // Gezielt prüfen statt InstalledFontCollection (die enumeriert alle Fonts und kostet >100 ms).
        foreach (var name in candidates)
        {
            try
            {
                using var family = new FontFamily(name);
                return family.Name;
            }
            catch (ArgumentException)
            {
                // nicht installiert
            }
        }
        return "Segoe UI";
    }
}
