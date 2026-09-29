using System.ComponentModel;
using BluetoothSwitcher.Hotkeys;
using Windows.Win32;

namespace BluetoothSwitcher.UI;

/// <summary>
/// Eingabefeld, das eine Tastenkombination aufnimmt: fokussieren, Kombination drücken, fertig.
/// Esc stellt den vorherigen Wert wieder her. Reine Modifier werden ignoriert.
/// </summary>
internal sealed class HotkeyCaptureBox : TextBox
{
    private Hotkey? _hotkey;
    private Hotkey? _beforeCapture;

    public event Action? CaptureStarted;
    public event Action? CaptureEnded;

    public HotkeyCaptureBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Hotkey? Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            Text = value?.ToString() ?? "";
        }
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        _beforeCapture = _hotkey;
        Text = "Tastenkombination drücken …";
        BackColor = Theme.WithAlpha(Theme.Accent, 255);
        CaptureStarted?.Invoke();
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        Text = _hotkey?.ToString() ?? "";
        BackColor = Theme.InputBackground;
        CaptureEnded?.Invoke();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;
        if (IsWinPressed())
            modifiers |= Keys.LWin;

        switch (key)
        {
            case Keys.Tab when modifiers is Keys.None or Keys.Shift:
                return base.ProcessCmdKey(ref msg, keyData); // Fokuswechsel erlauben
            case Keys.Escape when modifiers == Keys.None:
                Hotkey = _beforeCapture;
                Parent?.SelectNextControl(this, true, true, true, true);
                return true;
        }

        if (HotkeyParser.IsModifierKey(key))
        {
            Text = PartialText(modifiers) + " …";
            return true;
        }

        // Ohne Modifier nur F-Tasten und Sondertasten zulassen, sonst blockiert der Hotkey normale Eingaben.
        var isFunctionKey = key is >= Keys.F1 and <= Keys.F24 or Keys.Pause or Keys.Scroll
            or Keys.BrowserHome or Keys.LaunchApplication1 or Keys.LaunchApplication2 or Keys.SelectMedia;
        if (modifiers == Keys.None && !isFunctionKey)
        {
            Text = "Bitte mit Ctrl/Alt/Shift/Win kombinieren";
            return true;
        }

        Hotkey = new Hotkey(modifiers, key);
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_CONTEXTMENU = 0x007B;
        if (m.Msg == WM_CONTEXTMENU)
            return;
        base.WndProc(ref m);
    }

    private static string PartialText(Keys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(Keys.LWin)) parts.Add("Win");
        return string.Join('+', parts);
    }

    private static bool IsWinPressed() =>
        (PInvoke.GetKeyState((int)Keys.LWin) & 0x8000) != 0 || (PInvoke.GetKeyState((int)Keys.RWin) & 0x8000) != 0;
}
