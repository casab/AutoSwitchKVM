namespace AutoSwitchKVM.App.Support;

/// In-memory categorized log buffer shown + exportable from Settings > Diagnostics (mirrors the
/// macOS DebugLog). Thread-safe; raises Changed after each append/clear.
public sealed class DebugLog
{
    private readonly object _gate = new();
    private readonly LinkedList<string> _entries = new();
    private readonly int _max;

    public event Action? Changed;

    public DebugLog(int max = 500) => _max = max;

    public void Log(string category, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  [{category}]  {message}";
        lock (_gate)
        {
            _entries.AddLast(line);
            while (_entries.Count > _max) _entries.RemoveFirst();
        }
        Changed?.Invoke();
    }

    public string PlainText()
    {
        lock (_gate) return string.Join(Environment.NewLine, _entries);
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Changed?.Invoke();
    }
}
