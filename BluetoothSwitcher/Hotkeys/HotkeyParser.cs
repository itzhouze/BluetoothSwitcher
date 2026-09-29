using System.Diagnostics.CodeAnalysis;

namespace BluetoothSwitcher.Hotkeys;

internal readonly record struct Hotkey(Keys Modifiers, Keys Key)
{
    public override string ToString() => HotkeyParser.Format(this);
}

/// <summary>Wandelt "Ctrl+Alt+B" ↔ <see cref="Hotkey"/> um. Modifier: Ctrl/Strg, Alt, Shift/Umschalt, Win.</summary>
internal static class HotkeyParser
{
    public static bool TryParse(string? text, [NotNullWhen(true)] out Hotkey? hotkey)
    {
        hotkey = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var modifiers = Keys.None;
        Keys? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control" or "strg":
                    modifiers |= Keys.Control;
                    break;
                case "alt":
                    modifiers |= Keys.Alt;
                    break;
                case "shift" or "umschalt":
                    modifiers |= Keys.Shift;
                    break;
                case "win" or "windows":
                    modifiers |= Keys.LWin;
                    break;
                default:
                    if (key is not null || !TryParseKey(raw, out var k))
                        return false;
                    key = k;
                    break;
            }
        }

        if (key is null)
            return false;

        hotkey = new Hotkey(modifiers, key.Value);
        return true;
    }

    public static string Format(Hotkey hotkey)
    {
        var parts = new List<string>(5);
        if (hotkey.Modifiers.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (hotkey.Modifiers.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (hotkey.Modifiers.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (hotkey.Modifiers.HasFlag(Keys.LWin)) parts.Add("Win");
        parts.Add(FormatKey(hotkey.Key));
        return string.Join('+', parts);
    }

    public static bool IsModifierKey(Keys key) => key is
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or
        Keys.Menu or Keys.LMenu or Keys.RMenu or
        Keys.LWin or Keys.RWin;

    private static bool TryParseKey(string token, out Keys key)
    {
        if (token.Length == 1 && char.IsAsciiDigit(token[0]))
        {
            key = Keys.D0 + (token[0] - '0');
            return true;
        }
        return Enum.TryParse(token, ignoreCase: true, out key) && key != Keys.None && !IsModifierKey(key);
    }

    private static string FormatKey(Keys key) =>
        key is >= Keys.D0 and <= Keys.D9 ? ((char)('0' + (key - Keys.D0))).ToString() : key.ToString();
}
