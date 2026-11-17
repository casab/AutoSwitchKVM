using System.Runtime.InteropServices;
using AutoSwitchKVM.App.Support;
using Microsoft.Win32;

namespace AutoSwitchKVM.App.Platform;

/// Observes system sleep/wake and log-off/shutdown (mirrors the macOS SleepWakeMonitor). On suspend
/// and on session end we proactively release devices (so another host can take them while this PC
/// is away); on resume we re-seed.
///
/// Sleep/wake come from SystemEvents (raised where its broadcast window lives; delivery requires a
/// pumping message loop — the WinUI dispatcher provides one). OnSuspend deliberately runs (and may
/// block, bounded) on that thread: Windows gives only ~2 s of grace, so the consumer must NOT defer
/// the release to a queue that may never get pumped. OnResume may be marshaled to the UI thread.
///
/// Log-off/shutdown come from our own hidden window on its own thread instead of
/// SystemEvents.SessionEnding. With SessionEnding the app was ended before the release finished
/// on every power-off on 2026-11-17 (three of six even within 1.5 s), leaving the trackpad bonded
/// to this PC while it was off. The window answers WM_QUERYENDSESSION only once OnSessionEnding
/// returns, and registers a shutdown block reason meanwhile: that is how Windows lets an app
/// without a visible window ask for time (it shows the reason on the "apps are closing" screen
/// if the wait passes ~5 s). The process also asks to be told first (SetProcessShutdownParameters),
/// before other apps and services begin to close.
public sealed class PowerMonitor : IDisposable
{
    public Action? OnSuspend;
    public Action? OnResume;
    /// Log-off or shutdown (the reason is passed for the log). Runs on the session-end thread and
    /// may block (bounded by the consumer); Windows waits for it to return.
    public Action<string>? OnSessionEnding;

    private const string BlockReason = "Releasing Bluetooth devices for your other computer";

    private bool _started;
    private Thread? _thread;
    private IntPtr _hwnd;
    private WndProcDelegate? _wndProc;   // kept alive for the lifetime of the window
    private int _sessionEndHandled;      // once per session end; reset if the shutdown is cancelled

    public void Start()
    {
        if (_started) return;
        _started = true;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // 0x3FF: the highest level an app can ask for (default 0x280): shut down first.
        if (!SetProcessShutdownParameters(0x3FF, 0))
            Log.Debug("power", $"SetProcessShutdownParameters failed (Win32 error {Marshal.GetLastWin32Error()})");

        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => RunSessionWindow(ready)) { IsBackground = true, Name = "session-end" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) OnSuspend?.Invoke();
        else if (e.Mode == PowerModes.Resume) OnResume?.Invoke();
    }

    // ---- Session end: a hidden top-level window on its own thread ----

    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const long ENDSESSION_LOGOFF = 0x80000000;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    private void RunSessionWindow(ManualResetEventSlim ready)
    {
        try
        {
            _wndProc = WndProc;
            var className = "AutoSwitchKVM_Session_" + Guid.NewGuid().ToString("N");
            var wc = new WNDCLASS { lpfnWndProc = _wndProc, hInstance = GetModuleHandleW(null), lpszClassName = className };
            if (RegisterClassW(ref wc) == 0)
                Log.Warn("power", $"RegisterClassW failed (Win32 error {Marshal.GetLastWin32Error()})");
            // Top-level (not message-only: those don't get WM_QUERYENDSESSION) and never shown.
            _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, className, "AutoSwitch KVM", WS_POPUP, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Log.Error("power", $"CreateWindowExW failed (Win32 error {Marshal.GetLastWin32Error()}); devices are not released at shutdown");
                return;
            }
        }
        finally { ready.Set(); }

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_QUERYENDSESSION:
                if (Interlocked.Exchange(ref _sessionEndHandled, 1) == 0)
                {
                    var reason = ((long)lParam & ENDSESSION_LOGOFF) != 0 ? "log-off" : "shutdown";
                    var blocked = ShutdownBlockReasonCreate(hWnd, BlockReason);
                    if (!blocked)
                        Log.Debug("power", $"ShutdownBlockReasonCreate failed (Win32 error {Marshal.GetLastWin32Error()})");
                    try { OnSessionEnding?.Invoke(reason); }
                    catch (Exception ex) { Log.Warn("power", $"{reason} handler failed: {ex.Message}"); }
                    finally { if (blocked) ShutdownBlockReasonDestroy(hWnd); }
                }
                return new IntPtr(1);   // never veto: we only need the time

            case WM_ENDSESSION:
                // wParam FALSE: the shutdown was cancelled (by the user or another app).
                if (wParam == IntPtr.Zero)
                {
                    Interlocked.Exchange(ref _sessionEndHandled, 0);
                    Log.Info("power", "session end cancelled");
                }
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_started) return;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _hwnd = IntPtr.Zero;
        _started = false;
    }

    // ---- Win32 interop ----

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessShutdownParameters(uint level, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
