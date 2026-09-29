# BT-Switcher

Audio-Ausgabe am HTPC vom Sofa aus wechseln – per Hotkey, ohne Maus, ohne Windows-Einstellungen.

Ein Tastendruck (Standard: `Ctrl+Alt+B`) legt ein halbtransparentes, TV-taugliches Overlay über die laufende Vollbild-App. Mit den Pfeiltasten ein Gerät wählen, `Enter` drücken: Bluetooth-Geräte werden bei Bedarf verbunden und das Gerät wird als Windows-Standard-Ausgabe gesetzt. Danach bekommt die Vollbild-App den Fokus zurück.

## Funktionen

- Globaler Hotkey, funktioniert auch über Vollbild-Apps (erneuter Druck blendet das Overlay wieder aus)
- Bluetooth-Kopfhörer, -Earbuds und -Lautsprecher verbinden sowie HDMI-/USB-Ausgaben umschalten
- Beim Wechsel das vorherige Bluetooth-Gerät trennen (pro Gerät einstellbar)
- Akkustand bei unterstützten Geräten
- Laufenden Film pausieren, während ein Bluetooth-Gerät verbindet
- Ergebnis-Toasts, die den Fokus nicht stehlen
- Overlay schließt sich nach Inaktivität automatisch
- Tray-Menü mit Einstellungen (Hotkey-Aufnahme, Geräte-Editor), Autostart und Logs

## Bedienung im Overlay

| Taste | Aktion |
|---|---|
| `↑` `↓` | Gerät auswählen |
| `1`–`9` | Gerät direkt wählen |
| `Enter` | Umschalten |
| `Esc` / Hotkey | Schließen |

## Voraussetzungen

- Windows 10/11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Bauen

```powershell
dotnet publish BluetoothSwitcher -c Release
```

Ergebnis: eine einzelne `BTSwitcher.exe` (< 1 MB) unter `BluetoothSwitcher\bin\Release\net10.0-windows\win-x64\publish\`.

## Konfiguration & Logs

- Einstellungen: `%APPDATA%\BTSwitcher\config.json` (wird beim ersten Start aus den vorhandenen Audiogeräten erzeugt)
- Logs: `%APPDATA%\BTSwitcher\logs\`

## Technik

- C# / .NET 10 WinForms, Win32-Interop über [CsWin32](https://github.com/microsoft/CsWin32)
- Bluetooth-Verbindung über `KSPROPSETID_BtAudio` (`KSPROPERTY_ONESHOT_RECONNECT` / `_DISCONNECT`), derselbe Mechanismus wie „Verbinden“ in der Windows-Soundsteuerung
- Standardgerät über `IPolicyConfig`
- Overlay als Per-Pixel-Alpha-Layered-Window (`UpdateLayeredWindow`)
