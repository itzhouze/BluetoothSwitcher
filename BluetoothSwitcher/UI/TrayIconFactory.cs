using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace BluetoothSwitcher.UI;

/// <summary>Erzeugt das Tray-Icon zur Laufzeit aus dem Kopfhörer-Glyph (keine .ico-Datei nötig).</summary>
internal static class TrayIconFactory
{
    public static Icon Create()
    {
        var size = SystemInformation.SmallIconSize.Width * 2; // scharf auch bei 150–200 % Skalierung
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            Theme.Prepare(g);
            g.Clear(Color.Transparent);
            var rect = new RectangleF(0.5f, 0.5f, size - 1, size - 1);
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(rect, Theme.Accent,
                       Color.FromArgb(255, 124, 92, 255), System.Drawing.Drawing2D.LinearGradientMode.ForwardDiagonal))
                g.FillEllipse(brush, rect);
            using var font = Theme.PixelFont(Theme.IconFont, size * 0.55f);
            Theme.DrawTextCentered(g, Theme.GlyphHeadphones, font, Color.White, rect);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone(); // Clone besitzt eigene Kopie → Handle darf zerstört werden
        }
        finally
        {
            PInvoke.DestroyIcon(new HICON(handle));
        }
    }
}
