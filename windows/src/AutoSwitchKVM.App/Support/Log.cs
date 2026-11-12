using System.Diagnostics;

namespace AutoSwitchKVM.App.Support;

/// App-wide logging. Each entry goes to three places:
///   1. Trace  -> the Visual Studio "Output (Debug)" window while debugging.
///   2. A rolling file: %LOCALAPPDATA%\AutoSwitchKVM\logs\app-yyyyMMdd.log (inspect after a run).
///   3. An in-memory ring buffer shown + exportable from Settings > Diagnostics (Changed fires on update).
///
/// Mirrors the macOS `Log` (categories app/usb/bluetooth/engine). Use Log.Debug/Info/Warn/Error instead
/// of Console/Debug.WriteLine so output is consistent and captured everywhere. Level policy: see
/// Core.LogLevel; the minimum level comes from Settings ▸ Diagnostics ▸ Log detail.
public static class Log
{
    private static readonly object Gate = new();
    private static readonly LinkedList<string> Buffer = new();
    private const int MaxBuffer = 2000;
    private static string? _filePath;
    private static DateTime _fileDate;
    private const int KeepDays = 14;   // same retention as the macOS file log

    public static event Action? Changed;

    /// Today's log file. Re-evaluated on every write so a tray app running past midnight starts
    /// the new day's file (it used to keep writing to the day it started).
    public static string FilePath
    {
        get
        {
            lock (Gate)
            {
                var today = DateTime.Now.Date;
                if (_filePath is null || today != _fileDate)
                {
                    _fileDate = today;
                    _filePath = InitFilePath(today);
                    CleanupOldLogs(Path.GetDirectoryName(_filePath));
                }
                return _filePath;
            }
        }
    }

    private static string InitFilePath(DateTime day)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AutoSwitchKVM", "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"app-{day:yyyyMMdd}.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "autoswitchkvm.log");
        }
    }

    /// Delete app logs and captured driver traces older than KeepDays (the folder grew forever).
    private static void CleanupOldLogs(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var cutoff = DateTime.Now.AddDays(-KeepDays);
        foreach (var pattern in new[] { "app-*.log", "driver-*.etl", "driver-*.txt" })
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, pattern))
                {
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); }
                    catch { /* in use or no access: try again tomorrow */ }
                }
            }
            catch { /* best-effort */ }
        }
    }

    /// Entries below this level are dropped (file, ring buffer, debugger output). Set from
    /// AppConfig.LogLevel at startup and whenever the setting changes. Volatile: read from any thread.
    public static Core.LogLevel MinLevel
    {
        get => (Core.LogLevel)Volatile.Read(ref _minLevel);
        set => Volatile.Write(ref _minLevel, (int)value);
    }
    private static int _minLevel = (int)Core.LogLevel.Info;

    /// Whether entries at this level are written. Use it to skip *work* that only feeds the log
    /// (release probe, USB diffs), not just the line.
    public static bool IsEnabled(Core.LogLevel level) => level >= MinLevel;

    public static void Debug(string category, string message)
    {
        if (IsEnabled(Core.LogLevel.Debug)) Write("DEBUG", category, message);
    }

    public static void Info(string category, string message)
    {
        if (IsEnabled(Core.LogLevel.Info)) Write("INFO", category, message);
    }

    public static void Warn(string category, string message)
    {
        if (IsEnabled(Core.LogLevel.Warn)) Write("WARN", category, message);
    }

    /// Written at any level (as INFO): for the few lines that must always be in the log, like
    /// a change of the log level itself.
    public static void Always(string category, string message) => Write("INFO", category, message);

    /// Errors are always written.
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
