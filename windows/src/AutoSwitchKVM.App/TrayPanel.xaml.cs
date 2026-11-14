using System.Runtime.InteropServices;
using AutoSwitchKVM.App.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace AutoSwitchKVM.App;

/// The tray panel (left-click on the notification-area icon): a borderless, always-on-top acrylic
/// window in the bottom-right of the work area, like the Windows 11 quick settings. It hides when it
/// loses focus. Its own window rather than H.NotifyIcon's flyout: that one rendered blank (its
/// secondary window doesn't inherit the app theme).
public sealed partial class TrayPanel : Window
{
    public AppViewModel ViewModel => AppViewModel.Instance;

    /// Raised when the gear button asks for the Settings window.
    public event Action? SettingsRequested;

    private const int PanelWidth = 360;
    private const int Margin = 12;
    private long _hiddenAt;

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    public TrayPanel()
    {
        InitializeComponent();
        Title = "AutoSwitch KVM";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = true;

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) HidePanel();
        };
        // Alt+F4 / close: hide instead of destroying the window.
        AppWindow.Closing += (_, e) => { e.Cancel = true; HidePanel(); };
    }

    public bool IsShown => AppWindow.IsVisible;

    /// Tray left-click. A click on the icon while the panel is open first deactivates (hides) it;
    /// ignore the click that immediately follows so it doesn't reopen.
    public void Toggle()
    {
        if (IsShown) { HidePanel(); return; }
        if (Environment.TickCount64 - _hiddenAt < 400) return;
        ShowPanel();
    }

    public void ShowPanel()
    {
        try
        {
            // Place with the last known content height (or a generous first guess); Root's
            // SizeChanged then fits the window to the laid-out content.
            Place(_contentHeight > 0 ? _contentHeight : 520);
            AppWindow.Show(activateWindow: true);
            Activate();
            // The panel hides when it loses focus, so it has to get focus first. Since Windows App
            // SDK 2.6.5, Activate() alone left it without focus (seen 2026-11-14): it stayed open
            // until clicked once, then clicked away. The tray click grants this process the right
            // to set the foreground window.
            var hwnd = WindowNative.GetWindowHandle(this);
            if (GetForegroundWindow() != hwnd && !SetForegroundWindow(hwnd))
                Support.Log.Warn("ui", "tray panel could not take focus; it hides on the next click outside it only after it is clicked");
        }
        catch (Exception ex)
        {
            Support.Log.Error("ui", "tray panel show failed", ex);
        }
    }

    public void HidePanel()
    {
        if (!AppWindow.IsVisible) return;
        _hiddenAt = Environment.TickCount64;
        AppWindow.Hide();
    }

    private double _contentHeight;

    /// Fit the window to the content whenever the content's laid-out height changes (device count,
    /// wrapping, profile picker). Measuring the window's root directly throws, so this uses the
    /// actual size of Root, which sits top-aligned inside the host grid.
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var h = Math.Ceiling(e.NewSize.Height);
        if (Math.Abs(h - _contentHeight) < 1) return;
        _contentHeight = h;
        if (AppWindow.IsVisible)
        {
            try { Place(h); }
            catch (Exception ex) { Support.Log.Error("ui", "tray panel resize failed", ex); }
        }
    }

    /// Bottom-right of the work area of the monitor under the cursor (where the notification area
    /// normally is); the work area already excludes the taskbar wherever it is docked.
    private void Place(double contentHeight)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary);
        var work = area.WorkArea;

        var m = (int)(Margin * scale);
        var cw = (int)Math.Ceiling(PanelWidth * scale);
        var ch = Math.Min((int)Math.Ceiling(contentHeight * scale), work.Height - 2 * m);
        // Size the client area (the content), not the outer window: the border takes a few pixels,
        // and sizing the outer frame to the content width clipped the right and bottom edges.
        AppWindow.ResizeClient(new SizeInt32(cw, ch));
        // With the title bar hidden, ResizeClient still budgets a caption strip (measured: the client
        // came out 38 px taller than asked at 125%), leaving empty space under the content. Correct
        // the outer size by whatever the client area is off by.
        var dw = AppWindow.ClientSize.Width - cw;
        var dh = AppWindow.ClientSize.Height - ch;
        if (dw != 0 || dh != 0)
            AppWindow.Resize(new SizeInt32(AppWindow.Size.Width - dw, AppWindow.Size.Height - dh));
        var outer = AppWindow.Size;
        AppWindow.Move(new PointInt32(work.X + work.Width - outer.Width - m, work.Y + work.Height - outer.Height - m));
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        HidePanel();
        SettingsRequested?.Invoke();
    }

    private void OnDeviceToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: DeviceViewModel device } toggle) return;
        // The switch also fires when the binding updates it; act only on a real user change.
        if (toggle.IsOn == device.IsConnected || device.IsBusy) return;
        if (toggle.IsOn) device.Connect(); else device.Release();
    }
}
