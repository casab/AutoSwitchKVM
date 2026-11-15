using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using AutoSwitchKVM.App.Services;
using AutoSwitchKVM.App.ViewModels;
using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace AutoSwitchKVM.App;

/// Switch status by the tray (Settings ▸ Notifications): when devices start changing, a small
/// borderless panel in the tray panel's style shows each changing device live (connecting →
/// connected / released / problem) and hides by itself once they've settled. It replaced Windows
/// toasts for device events on 2026-11-16: one toast and one sound per device per switch was
/// too much. It never takes focus; hovering keeps it open, a click opens the tray panel.
public sealed partial class StatusPopup : Window
{
    public AppViewModel ViewModel => AppViewModel.Instance;
    public ObservableCollection<StatusPopupRow> Rows { get; } = new();

    /// How long it stays after the last change once nothing is connecting (longer with a problem).
    private const int HoldMs = 3500;
    private const int HoldWithProblemMs = 8000;
    private const int PanelWidth = 320;
    private const int Margin = 12;

    private readonly AppController _c;
    private readonly Func<bool> _trayPanelOpen;
    private readonly Action _openTrayPanel;
    private readonly DispatcherQueueTimer _timer;
    private readonly StatusChangeFilter _filter = new();
    /// Devices shown as dropped (Unexpected disconnects) while they stay disconnected.
    private readonly HashSet<Guid> _dropped = new();
    private long _lastChange;
    private bool _hover;
    private double _contentHeight;

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    public StatusPopup(AppController controller, Func<bool> trayPanelOpen, Action openTrayPanel)
    {
        _c = controller;
        _trayPanelOpen = trayPanelOpen;
        _openTrayPanel = openTrayPanel;
        InitializeComponent();
        Title = "AutoSwitch KVM status";
        SystemBackdrop = new DesktopAcrylicBackdrop();

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = true;
        AppWindow.Closing += (_, e) => { e.Cancel = true; HidePopup(); };

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Tick();

        OnStateChanged();   // primes the filter: nothing shows at launch

        // A WinUI window shown without ever being activated doesn't load its content (only the
        // caption rendered). Activate it once, off-screen, at creation (app start), then hide it;
        // later it's shown without activation, so it never takes focus.
        AppWindow.Move(new PointInt32(-32000, -32000));
        Activate();
        AppWindow.Hide();
    }

    /// Engine/config state changed (on the UI thread). Adds the devices whose status changed in a
    /// way worth showing, refreshes the rows, and shows the panel.
    public void OnStateChanged()
    {
        var enabled = _c.Config.Devices.Where(d => d.Enabled).ToList();
        var ids = _filter.Changed(enabled.Select(d => (d.Id, _c.StatusFor(d).Kind)), _c.Selected,
            _c.IsPreview || _c.Engine.PresenceEvaluated);
        var changed = enabled.Where(d => ids.Contains(d.Id)).ToList();
        if (changed.Count > 0 && _c.Config.ShowNotifications && !_trayPanelOpen())
            Show(changed);
        else if (AppWindow.IsVisible)
            RefreshRows();
    }

    /// A device dropped while this PC is selected (Settings ▸ Notifications ▸ Unexpected
    /// disconnects): shown even when switch status is off.
    public void ShowDrop(BTDevice device)
    {
        if (_trayPanelOpen()) return;
        _dropped.Add(device.Id);
        Show(new[] { device });
    }

    /// Developer preview: every device, as it is now.
    public void ShowAllForPreview() => Show(_c.Config.Devices);

    private void Show(IEnumerable<BTDevice> devices)
    {
        foreach (var d in devices)
            if (!Rows.Any(r => r.Id == d.Id)) Rows.Add(new StatusPopupRow(d.Id));
        RefreshRows();
        _lastChange = Environment.TickCount64;
        if (AppWindow.IsVisible) return;
        try
        {
            Place(_contentHeight > 0 ? _contentHeight : 80 + 52 * Rows.Count);
            AppWindow.Show(activateWindow: false);
            _timer.Start();
        }
        catch (Exception ex)
        {
            Support.Log.Error("ui", "status popup show failed", ex);
        }
    }

    private void RefreshRows()
    {
        var selected = _c.Selected;
        HeaderText.Text = selected ? "KVM → this PC" : "KVM → the other computer";
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            var model = _c.Config.Devices.FirstOrDefault(d => d.Id == Rows[i].Id);
            if (model is null) { Rows.RemoveAt(i); continue; }
        }
        for (var i = 0; i < Rows.Count; i++)
        {
            var model = _c.Config.Devices.First(d => d.Id == Rows[i].Id);
            var vm = ViewModel.Devices.FirstOrDefault(v => v.Id == model.Id);
            var status = _c.StatusFor(model);
            if (_dropped.Contains(model.Id) && status.Kind is not (DeviceStatusKind.Disconnected or DeviceStatusKind.Idle))
                _dropped.Remove(model.Id);
            var e = _c.Engine;
            var (text, tone) = StatusPanelText.For(status, selected, model.ManagePairing, e.IsAsleep(model.Id),
                e.WasSentAway(model.Id), e.SwitchesChannels(model), _dropped.Contains(model.Id));
            Rows[i].Update(model.Name, vm?.Glyph ?? "", text, tone, i);
        }
    }

    private void Tick()
    {
        if (!AppWindow.IsVisible) { _timer.Stop(); return; }
        // The hold counts from when everything has settled and the pointer has left.
        if (_hover || Rows.Any(r => r.IsBusy)) { _lastChange = Environment.TickCount64; return; }
        var hold = Rows.Any(r => r.IsProblem) ? HoldWithProblemMs : HoldMs;
        if (Environment.TickCount64 - _lastChange >= hold) HidePopup();
    }

    public void HidePopup()
    {
        _timer.Stop();
        if (AppWindow.IsVisible) AppWindow.Hide();
        Rows.Clear();
        _dropped.Clear();
        _filter.EndBurst();
        _hover = false;
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => _hover = true;
    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hover = false;
        _lastChange = Environment.TickCount64;   // a moment to move away before it goes
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        HidePopup();
        _openTrayPanel();
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var h = Math.Ceiling(e.NewSize.Height);
        if (Math.Abs(h - _contentHeight) < 1) return;
        _contentHeight = h;
        if (AppWindow.IsVisible)
        {
            try { Place(h); }
            catch (Exception ex) { Support.Log.Error("ui", "status popup resize failed", ex); }
        }
    }

    /// Bottom-right of the primary display's work area, where the notification area is.
    private void Place(double contentHeight)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;
        var work = DisplayArea.Primary.WorkArea;
        var m = (int)(Margin * scale);
        var cw = (int)Math.Ceiling(PanelWidth * scale);
        var ch = Math.Min((int)Math.Ceiling(contentHeight * scale), work.Height - 2 * m);
        // Same correction as TrayPanel.Place: ResizeClient budgets a caption strip even without a
        // title bar, so fix the outer size by whatever the client area is off by.
        AppWindow.ResizeClient(new SizeInt32(cw, ch));
        var dw = AppWindow.ClientSize.Width - cw;
        var dh = AppWindow.ClientSize.Height - ch;
        if (dw != 0 || dh != 0)
            AppWindow.Resize(new SizeInt32(AppWindow.Size.Width - dw, AppWindow.Size.Height - dh));
        var outer = AppWindow.Size;
        AppWindow.Move(new PointInt32(work.X + work.Width - outer.Width - m, work.Y + work.Height - outer.Height - m));
    }
}

/// One device in the status popup.
public sealed class StatusPopupRow : ObservableObject
{
    public StatusPopupRow(Guid id) => Id = id;

    public Guid Id { get; }
    public string Name { get; private set; } = "";
    public string Glyph { get; private set; } = "";
    public string Text { get; private set; } = "";
    public Brush? Foreground { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsProblem { get; private set; }
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DividerVisibility { get; private set; } = Visibility.Collapsed;

    internal void Update(string name, string glyph, string text, StatusTone tone, int index)
    {
        Name = name;
        Glyph = glyph;
        Text = text;
        IsBusy = tone == StatusTone.Busy;
        IsProblem = tone == StatusTone.Problem;
        Foreground = Theme.Brush(tone switch
        {
            StatusTone.Busy => "SystemFillColorAttentionBrush",
            StatusTone.Problem => "SystemFillColorCriticalBrush",
            StatusTone.Ok when text == "Connected" => "SystemFillColorSuccessBrush",
            _ => "TextFillColorSecondaryBrush",
        });
        DividerVisibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
        OnPropertyChanged(string.Empty);
    }
}
