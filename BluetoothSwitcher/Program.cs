using BluetoothSwitcher.Logging;

namespace BluetoothSwitcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
#if DEBUG
        switch (args)
        {
            case ["--preview", var folder]:
                UI.PreviewRenderer.Run(folder);
                return;
            case ["--selftest", var output]:
                UI.PreviewRenderer.SelfTest(output);
                return;
            case ["--settings"]:
                ApplicationConfiguration.Initialize();
                Application.Run(new UI.SettingsForm(Config.ConfigStore.LoadOrCreate(), Audio.AudioEndpointService.GetRenderEndpoints));
                return;
        }
#endif
        using var mutex = new Mutex(initiallyOwned: true, @"Local\BTSwitcher.SingleInstance", out var isFirstInstance);
        Log.Start();

        if (!isFirstInstance)
        {
            Log.Info("BT-Switcher läuft bereits – zweite Instanz beendet sich.");
            Log.Shutdown();
            return;
        }

        Application.ThreadException += (_, e) => Log.Error("Unbehandelte Ausnahme (UI-Thread)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unbehandelte Ausnahme", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unbeobachtete Task-Ausnahme", e.Exception);
            e.SetObserved();
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        ApplicationConfiguration.Initialize();
        // Wird sonst erst mit dem ersten Control installiert – die Services brauchen ihn aber schon vorher.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        try
        {
            Application.Run(new TrayApplicationContext());
        }
        catch (Exception ex)
        {
            Log.Error("Fataler Fehler", ex);
        }
        finally
        {
            Log.Shutdown();
        }
    }
}
