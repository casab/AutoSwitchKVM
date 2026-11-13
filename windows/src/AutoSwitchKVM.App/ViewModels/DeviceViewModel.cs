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
        _ = LoadHandoffAsync();
    }

    // ---------------- How it switches (spec §5a) ----------------

    private HandoffAssessment? _handoff;
    public HandoffType HandoffType => _handoff?.Type ?? HandoffType.Unknown;
    public string HandoffTitle => _handoff is { } h ? "How it switches: " + HandoffText.Title(h.Type) + HandoffText.Confidence(h) : "How it switches";
    public string HandoffDescription => _handoff is { } h ? HandoffText.WhatHappens(h.Type) : "Checking this device…";
    public string HandoffGlyph => HandoffText.Glyph(HandoffType);

    // Channel switching (type 5): which channel the device goes to when the KVM leaves this PC.
    private HostChannels? _channels;
    public Visibility ChannelSettingVisibility => HandoffType == HandoffType.ChannelSwitch ? Visibility.Visible : Visibility.Collapsed;
    public string ChannelSettingDescription => _channels is { } c
        ? $"Where the device goes when the KVM leaves this PC. This PC is channel {c.Current + 1} of {c.Count}. Automatic uses the channel AutoSwitch marked on the other computer."
        : "Where the device goes when the KVM leaves this PC. Automatic uses the channel AutoSwitch marked on the other computer.";
    public IReadOnlyList<string> ChannelChoices { get; } = new[] { "Automatic", "Channel 1", "Channel 2", "Channel 3" };

    /// 0 = automatic, n = channel n.
    public int ReleaseChannelIndex
    {
        get => Model.ReleaseToChannel is int n && n >= 1 && n <= 3 ? n : 0;
        set
        {
            if (value < 0 || value == ReleaseChannelIndex) return;
            _app.Controller.Mutate(_ => Model.ReleaseToChannel = value == 0 ? null : value);
        }
    }

    private async Task LoadHandoffAsync()
    {
        try
        {
            _handoff = await _app.Controller.ClassifyAsync(Model.Address, Model.Name);
            if (_handoff?.Type == HandoffType.ChannelSwitch)
                _channels = await _app.Controller.GetChannelsAsync(Model.Address);
        }
        catch (Exception ex)
        {
            Support.Log.Warn("app", $"{Model.Name}: could not tell how it switches ({ex.Message})");
            _handoff = new HandoffAssessment(HandoffType.Unknown, HandoffConfidence.Unsure, "lookup failed");
        }
        OnPropertyChanged(nameof(HandoffTitle));
        OnPropertyChanged(nameof(HandoffDescription));
        OnPropertyChanged(nameof(HandoffGlyph));
        OnPropertyChanged(nameof(HandoffType));
        OnPropertyChanged(nameof(ChannelSettingVisibility));
        OnPropertyChanged(nameof(ChannelSettingDescription));
    }

    public BTDevice Model { get; private set; }

    /// Whether this device's row is expanded in Settings ▸ Devices. Kept here, not in the row
    /// control: the list recycles row controls, and their own state would jump between devices.
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
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

    /// Icon guessed from the device name (Segoe Fluent Icons); Bluetooth when unknown.
    public string Glyph
    {
        get
        {
            var n = Name.ToLowerInvariant();
            if (n.Contains("trackpad") || n.Contains("touchpad")) return "";
            if (n.Contains("keyboard")) return "";
            if (n.Contains("mouse") || n.Contains("mx master") || n.Contains("mx anywhere")) return "";
            if (n.Contains("airpods") || n.Contains("headphone") || n.Contains("buds") || n.Contains("headset")) return "";
            if (n.Contains("speaker")) return "";
            if (n.Contains("controller") || n.Contains("gamepad")) return "";
            return "";
        }
    }

    /// Tooltip for compact rows (tray panel): the error detail, else the status.
    public string Tooltip => _status.Kind == DeviceStatusKind.Error && !string.IsNullOrEmpty(_status.Message)
        ? $"{Name}: {_status.Message}"
        : $"{Name}: {StatusText}";

    /// Divider above every row but the first (grouped list in the tray panel).
    public Visibility DividerVisibility => _index > 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- Settings (two-way) ----------------

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (value == Model.Enabled) return;
            // Only this device: enabling connects it if this PC is selected; disabling leaves it as it is.
            _app.Controller.Mutate(_ => Model.Enabled = value);
            if (value) _app.Controller.DeviceAddedOrEnabled(Model.Id);
        }
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
