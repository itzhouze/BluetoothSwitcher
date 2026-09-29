using System.Runtime.InteropServices;
using BluetoothSwitcher.Logging;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace BluetoothSwitcher.Audio;

/// <summary>
/// Pausiert/fortsetzt die laufende Wiedergabe (Harbor) über die globale Play/Pause-Medientaste.
/// Damit ein bereits pausierter Film nicht versehentlich startet, wird nur pausiert, wenn auf dem
/// aktuellen Ausgabegerät tatsächlich Ton anliegt (Pegelmesser des Endpoints).
/// </summary>
internal static class PlaybackControl
{
    private const float SilenceThreshold = 0.0005f;

    /// <summary>Misst ~150 ms lang den Pegel des Standard-Ausgabegeräts.</summary>
    public static bool IsAudioPlaying()
    {
        var enumerator = AudioEndpointService.CreateEnumerator();
        IMMDevice? device = null;
        IAudioMeterInformation? meter = null;
        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device);
            device.Activate(typeof(IAudioMeterInformation).GUID, CLSCTX.CLSCTX_ALL, null, out var obj);
            meter = (IAudioMeterInformation)obj;

            for (var i = 0; i < 6; i++)
            {
                meter.GetPeakValue(out var peak);
                if (peak > SilenceThreshold)
                    return true;
                Thread.Sleep(25);
            }
            return false;
        }
        catch (COMException ex)
        {
            Log.Warn($"Pegel konnte nicht gemessen werden: {ex.Message}");
            return false;
        }
        finally
        {
            if (meter is not null) Marshal.ReleaseComObject(meter);
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Sendet einen Druck auf die Play/Pause-Medientaste.</summary>
    public static unsafe void SendPlayPause()
    {
        Span<INPUT> inputs = stackalloc INPUT[2];
        inputs[0].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[0].ki.wVk = VIRTUAL_KEY.VK_MEDIA_PLAY_PAUSE;
        inputs[1].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[1].ki.wVk = VIRTUAL_KEY.VK_MEDIA_PLAY_PAUSE;
        inputs[1].ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;

        var sent = PInvoke.SendInput(inputs, sizeof(INPUT));
        if (sent != 2)
            Log.Warn($"Play/Pause-Taste konnte nicht gesendet werden (Win32-Fehler {Marshal.GetLastPInvokeError()})");
    }
}
