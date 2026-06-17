using System.Runtime.InteropServices;
using AutoSwitchKVM.App.Support;
using AutoSwitchKVM.Core.Models;

namespace AutoSwitchKVM.App.Platform;

public enum HotKeyAction { TogglePause = 1, ConnectAll = 2, DisconnectAll = 3 }

/// Global hotkeys via Win32 RegisterHotKey, delivered to a hidden message-only window's WndProc
/// (mirrors the macOS Carbon HotKeyManager). The macOS defaults were Ctrl+Alt+Cmd; Windows has no
/// Command, so defaults are Ctrl+Alt+P/C/D.
///
/// Start() must be called on the UI thread so WM_HOTKEY is delivered through that thread's message
/// loop; OnAction therefore fires on the UI thread.
public sealed class HotKeyService : IDisposable
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    private const uint WM_HOTKEY = 0x0312;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    public static KeyShortcut DefaultPause => new() { KeyCode = 0x50, Modifiers = MOD_CONTROL | MOD_ALT, Display = "Ctrl+Alt+P" };       // P
    public static KeyShortcut DefaultConnectAll => new() { KeyCode = 0x43, Modifiers = MOD_CONTROL | MOD_ALT, Display = "Ctrl+Alt+C" };  // C
    public static KeyShortcut DefaultDisconnectAll => new() { KeyCode = 0x44, Modifiers = MOD_CONTROL | MOD_ALT, Display = "Ctrl+Alt+D" }; // D

    public Action<HotKeyAction>? OnAction;

    private IntPtr _hwnd;
    private WndProcDelegate? _wndProc;   // kept alive for the lifetime of the window
    private string? _className;
    private readonly List<int> _registered = new();

    public void Start()
    {
        if (_hwnd != IntPtr.Zero) return;

        _wndProc = WndProc;
        _className = "AutoSwitchKVM_HotKey_" + Guid.NewGuid().ToString("N");
        var wc = new WNDCLASS
        {
            lpfnWndProc = _wndProc,
            hInstance = GetModuleHandleW(null),
            lpszClassName = _className,
        };
        var atom = RegisterClassW(ref wc);
        if (atom == 0)
            Log.Warn("hotkey", $"RegisterClassW failed (Win32 error {Marshal.GetLastWin32Error()})");

        _hwnd = CreateWindowExW(0, _className, string.Empty, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            Log.Error("hotkey", $"CreateWindowExW failed (Win32 error {Marshal.GetLastWin32Error()}); global hotkeys disabled");
        else
            Log.Info("hotkey", "message-only window created");
    }

    /// Idempotent: re-register from scratch whenever the enabled flag or any shortcut changes.
    public void Apply(bool enabled, KeyShortcut? pause, KeyShortcut? connectAll, KeyShortcut? disconnectAll)
    {
        Unregister();
        if (!enabled || _hwnd == IntPtr.Zero) return;
        Register((int)HotKeyAction.TogglePause, pause ?? DefaultPause);
        Register((int)HotKeyAction.ConnectAll, connectAll ?? DefaultConnectAll);
        Register((int)HotKeyAction.DisconnectAll, disconnectAll ?? DefaultDisconnectAll);
    }

    private void Register(int id, KeyShortcut s)
    {
        if (s.Modifiers == 0 || s.KeyCode == 0) return; // a bare key makes a poor global hotkey
        if (RegisterHotKey(_hwnd, id, s.Modifiers | MOD_NOREPEAT, s.KeyCode))
            _registered.Add(id);
        else
            Log.Warn("hotkey", $"RegisterHotKey failed for {(HotKeyAction)id} ('{s.Display}') - likely already in use");
    }

    private void Unregister()
    {
        foreach (var id in _registered) UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            var id = (int)wParam;
            if (Enum.IsDefined(typeof(HotKeyAction), id)) OnAction?.Invoke((HotKeyAction)id);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        Unregister();
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);
}
