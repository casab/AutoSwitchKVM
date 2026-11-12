using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AutoSwitchKVM.App.ViewModels;

/// One managed Bluetooth device as shown in the Settings window and the tray panel.
public sealed class DeviceViewModel : ObservableObject
{
    private readonly AppViewModel _app;
    private DeviceStatus _status;
    private int _index, _count;

    public DeviceViewModel(AppViewModel app, BTDevice model)
    {
        _app = app;
        Model = model;
    }

    public BTDevice Model { get; private set; }
    public Guid Id => Model.Id;
    public string Name => Model.Name;
    public string Address => Model.Address;

    // ---------------- Status ----------------

    public DeviceStatusKind StatusKind => _status.Kind;
    public bool IsConnected => _status.Kind == DeviceStatusKind.Connected;
    public bool IsBusy => _status.Kind == DeviceStatusKind.Connecting;
    public bool IsNotBusy => !IsBusy;
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public string StatusText => _status.Kind switch
    {
        DeviceStatusKind.Connected => "Connected",
        DeviceStatusKind.Connecting => "Connecting…",
        DeviceStatusKind.BluetoothOff => "Bluetooth off",
        DeviceStatusKind.Error => "Problem",
        _ => "Not connected",
    };

    /// Secondary line: the error detail when there is one, else the address (+ disabled hint).
    public string Detail => _status.Kind == DeviceStatusKind.Error && !string.IsNullOrEmpty(_status.Message)
        ? _status.Message!
        : Model.Enabled ? Address : $"{Address} · automatic handoff off";

    public Brush PillBackground => Theme.Brush(_status.Kind switch
    {
        DeviceStatusKind.Connected => "SystemFillColorSuccessBackgroundBrush",
        DeviceStatusKind.Connecting => "SystemFillColorAttentionBackgroundBrush",
        DeviceStatusKind.Error => "SystemFillColorCriticalBackgroundBrush",
        DeviceStatusKind.BluetoothOff => "SystemFillColorCautionBackgroundBrush",
        _ => "SystemFillColorNeutralBackgroundBrush",
    });

    public Brush PillForeground => Theme.Brush(_status.Kind switch
    {
        DeviceStatusKind.Connected => "SystemFillColorSuccessBrush",
        DeviceStatusKind.Connecting => "SystemFillColorAttentionBrush",
        DeviceStatusKind.Error => "SystemFillColorCriticalBrush",
        DeviceStatusKind.BluetoothOff => "SystemFillColorCautionBrush",
        _ => "TextFillColorSecondaryBrush",
    });

    public string PrimaryActionText => IsConnected ? "Disconnect" : "Connect";

    // ---------------- Settings (two-way) ----------------

    public bool Enabled
    {
        get => Model.Enabled;
        set { if (value != Model.Enabled) _app.Controller.Mutate(_ => Model.Enabled = value, reevaluate: true); }
    }

    public bool ManagePairing
    {
        get => Model.ManagePairing;
        set { if (value != Model.ManagePairing) _app.Controller.Mutate(_ => Model.ManagePairing = value); }
    }

    public double ConnectDelayMs
    {
        get => Model.ConnectDelayMs;
        set
        {
            if (double.IsNaN(value)) return;
            var v = (int)Math.Round(value);
            if (v != Model.ConnectDelayMs) _app.Controller.Mutate(_ => Model.ConnectDelayMs = v);
        }
    }

    public bool CanMoveUp => _index > 0;
    public bool CanMoveDown => _index < _count - 1;

    // ---------------- Actions (x:Bind event targets) ----------------

    public void PrimaryAction()
    {
        _ = IsConnected ? _app.Controller.ReleaseDeviceAsync(Model) : _app.Controller.ConnectDeviceAsync(Model);
    }

    public void Connect() => _ = _app.Controller.ConnectDeviceAsync(Model);
    public void Release() => _ = _app.Controller.ReleaseDeviceAsync(Model);
    public void Pair() => _ = _app.Controller.PairDeviceAsync(Model);
    public void Unpair() => _ = _app.Controller.UnpairDeviceAsync(Model);
    public void Diagnose() => _ = _app.Controller.DiagnoseAsync(Model);
    public void MoveUp() => _app.MoveDevice(Id, -1);
    public void MoveDown() => _app.MoveDevice(Id, +1);

    // ---------------- Refresh ----------------

    internal void Update(BTDevice model, DeviceStatus status, int index, int count)
    {
        Model = model;
        _status = status;
        _index = index;
        _count = count;
        OnPropertyChanged(string.Empty);
    }
}
