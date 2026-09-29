using System.Drawing.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace BluetoothSwitcher.UI;

/// <summary>
/// 32-bit-DIB-Section mit vormultipliziertem Alpha, auf die GDI+ direkt zeichnet und die ohne
/// Kopie per UpdateLayeredWindow angezeigt wird.
/// </summary>
internal sealed unsafe class DibSurface : IDisposable
{
    private readonly HBITMAP _bitmap;
    private readonly HGDIOBJ _oldBitmap;

    public HDC Dc { get; }
    public Bitmap Bitmap { get; }
    public int Width { get; }
    public int Height { get; }

    public DibSurface(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        var info = new BITMAPINFO();
        info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = Width;
        info.bmiHeader.biHeight = -Height; // top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = 0; // BI_RGB

        Dc = PInvoke.CreateCompatibleDC(HDC.Null);
        void* bits;
        _bitmap = PInvoke.CreateDIBSection(Dc, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (_bitmap.IsNull)
        {
            PInvoke.DeleteDC(Dc);
            throw new OutOfMemoryException($"CreateDIBSection {Width}x{Height} fehlgeschlagen");
        }
        _oldBitmap = PInvoke.SelectObject(Dc, (HGDIOBJ)_bitmap.Value);
        Bitmap = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, (IntPtr)bits);
    }

    public Graphics CreateGraphics(bool clear = true)
    {
        var g = Graphics.FromImage(Bitmap);
        if (clear)
            g.Clear(Color.Transparent);
        Theme.Prepare(g);
        return g;
    }

    public void Dispose()
    {
        Bitmap.Dispose();
        PInvoke.SelectObject(Dc, _oldBitmap);
        PInvoke.DeleteObject((HGDIOBJ)_bitmap.Value);
        PInvoke.DeleteDC(Dc);
    }
}

/// <summary>
/// Basis für per-Pixel-transparente, rahmenlose Topmost-Fenster (Card und Toast).
/// Zeichnet ausschließlich über <see cref="Present"/>; WM_PAINT wird nicht benutzt.
/// </summary>
internal class LayeredWindow : Form
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_POPUP = unchecked((int)0x80000000);

    private readonly bool _clickThrough;

    protected LayeredWindow(bool clickThrough)
    {
        _clickThrough = clickThrough;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = WS_POPUP;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            if (_clickThrough)
                cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    /// <summary>Zeigt den Inhalt der Surface an Position <paramref name="location"/> (Bildschirmpixel).</summary>
    protected unsafe void Present(DibSurface surface, Point location, byte alpha)
    {
        var screenDc = PInvoke.GetDC(HWND.Null);
        try
        {
            var dst = location;
            var size = new SIZE(surface.Width, surface.Height);
            var src = Point.Empty;
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
            PInvoke.UpdateLayeredWindow((HWND)Handle, screenDc, &dst, &size, surface.Dc, &src,
                new COLORREF(0), &blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
        }
        finally
        {
            PInvoke.ReleaseDC(HWND.Null, screenDc);
        }
    }

    /// <summary>Ändert nur die Gesamt-Deckkraft des bereits angezeigten Inhalts (kein Neuzeichnen).</summary>
    protected unsafe void SetAlpha(byte alpha)
    {
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
        PInvoke.UpdateLayeredWindow((HWND)Handle, HDC.Null, null, null, HDC.Null, null,
            new COLORREF(0), &blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
    }

    /// <summary>Holt das Fenster ganz nach oben in den Topmost-Bereich, ohne es zu aktivieren.</summary>
    protected void BringToTopNoActivate()
    {
        PInvoke.SetWindowPos((HWND)Handle, new HWND(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_DPICHANGED = 0x02E0;
        if (m.Msg == WM_DPICHANGED)
            return; // Größe/Position werden in Pixeln selbst gesetzt
        base.WndProc(ref m);
    }

    // Kein Hintergrund-Löschen / Painting – der Inhalt kommt aus UpdateLayeredWindow.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }
}
