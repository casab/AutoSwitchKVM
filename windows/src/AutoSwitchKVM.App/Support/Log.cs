using System.Diagnostics;

namespace AutoSwitchKVM.App.Support;

/// App-wide logging. Each entry goes to three places:
///   1. Trace  -> the Visual Studio "Output (Debug)" window while debugging.
///   2. A rolling file: %LOCALAPPDATA%\AutoSwitchKVM\logs\app-yyyyMMdd.log (inspect after a run).
///   3. An in-memory ring buffer shown + exportable from Settings > Diagnostics (Changed fires on update).
///
/// Mirrors the macOS `Log` (categories app/usb/bluetooth/engine). Use Log.Info/Warn/Error instead of
/// Console/Debug.WriteLine so output is consistent and captured everywhere.
public static class Log
{
    private static readonly object Gate = new();
    private static readonly LinkedList<string> Buffer = new();
    private const int MaxBuffer = 2000;
    private static string? _filePath;

    public static event Action? Changed;

    public static string FilePath
    {
        get
        {
            lock (Gate) { return _filePath ??= InitFilePath(); }
        }
    }

    private static string InitFilePath()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AutoSwitchKVM", "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "autoswitchkvm.log");
        }
    }

    public static void Info(string category, string message) => Write("INFO", category, message);
    public static void Warn(string category, string message) => Write("WARN", category, message);

    public static void Error(string category, string message, Exception? ex = null) =>
        Write("ERROR", category, ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string category, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {level,-5} [{category}]  {message}";
        Trace.WriteLine(line);
        lock (Gate)
        {
            Buffer.AddLast(line);
            while (Buffer.Count > MaxBuffer) Buffer.RemoveFirst();
            try { File.AppendAllText(FilePath, line + Environment.NewLine); }
            catch { /* never let logging crash the app */ }
        }
        Changed?.Invoke();
    }

    public static string PlainText()
    {
        lock (Gate) return string.Join(Environment.NewLine, Buffer);
    }

    public static void Clear()
    {
        lock (Gate) Buffer.Clear();
        Changed?.Invoke();
    }
}
