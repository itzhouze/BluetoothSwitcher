using System.Text;
using System.Threading.Channels;

namespace BluetoothSwitcher.Logging;

/// <summary>
/// Minimaler, threadsicherer Datei-Logger. Schreibt über eine Background-Queue nach
/// %APPDATA%\BTSwitcher\logs\btswitcher-yyyyMMdd.log und hält 14 Tage vor.
/// </summary>
internal static class Log
{
    private const int RetentionDays = 14;

    private static readonly Channel<string> Queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    private static Task? _writer;

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BTSwitcher", "logs");

    public static void Start()
    {
        System.IO.Directory.CreateDirectory(Directory);
        CleanupOldFiles();
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Leert die Queue und beendet den Writer (beim App-Exit).</summary>
    public static void Shutdown()
    {
        Queue.Writer.TryComplete();
        _writer?.Wait(TimeSpan.FromSeconds(2));
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId,-3}] {message}";
        Queue.Writer.TryWrite(line);
#if DEBUG
        System.Diagnostics.Debug.WriteLine(line);
#endif
    }

    private static async Task WriteLoopAsync()
    {
        var reader = Queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            try
            {
                var path = Path.Combine(Directory, $"btswitcher-{DateTime.Now:yyyyMMdd}.log");
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(stream, Encoding.UTF8);
                while (reader.TryRead(out var line))
                    sw.WriteLine(line);
            }
            catch
            {
                // Logging darf die App niemals mitreißen.
            }
        }
    }

    private static void CleanupOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in new DirectoryInfo(Directory).EnumerateFiles("btswitcher-*.log"))
            {
                if (file.LastWriteTime < cutoff)
                    file.Delete();
            }
        }
        catch
        {
            // ignorieren
        }
    }
}
