using AutoSwitchKVM.Core;

namespace AutoSwitchKVM.Core.Tests.Fakes;

/// In-memory IBluetoothController for engine tests (mirrors the macOS test fake). Records every call
/// and tracks connected/paired state so tests can assert ordering, pair-before-connect, backoff, etc.
public sealed class FakeBluetoothController : IBluetoothController
{
    public List<string> Calls { get; } = new();
    public HashSet<string> Connected { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Paired { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool? PoweredOn { get; set; } = true;

    /// Awaited at the start of connect/disconnect — lets tests suspend a call mid-flight to
    /// interleave engine runs at a realistic suspension point (mirrors the macOS mock hooks).
    public Func<Task>? BeforeConnect { get; set; }
    public Func<Task>? BeforeDisconnect { get; set; }
    /// When set, connect/disconnect record the call, then throw without changing state.
    public Exception? ConnectError { get; set; }
    public Exception? DisconnectError { get; set; }

    private Action<ConnectionChange>? _onChange;

    private static string Norm(string a) => a.Replace(":", "").Replace("-", "").ToUpperInvariant();

    // Reads are intentionally NOT recorded in Calls (mirrors the macOS mock), so order/count
    // assertions reflect only state-changing calls (connect/disconnect/pair/unpair).
    public Task<bool> IsConnectedAsync(string address, CancellationToken ct = default)
        => Task.FromResult(Connected.Contains(Norm(address)));

    public async Task ConnectAsync(string address, CancellationToken ct = default)
    {
        if (BeforeConnect is not null) await BeforeConnect();
        Calls.Add($"connect:{Norm(address)}");
        if (ConnectError is not null) throw ConnectError;
        Connected.Add(Norm(address));
        _onChange?.Invoke(new ConnectionChange(Norm(address), true));
    }

    public async Task DisconnectAsync(string address, CancellationToken ct = default)
    {
        if (BeforeDisconnect is not null) await BeforeDisconnect();
        Calls.Add($"disconnect:{Norm(address)}");
        if (DisconnectError is not null) throw DisconnectError;
        Connected.Remove(Norm(address));
        _onChange?.Invoke(new ConnectionChange(Norm(address), false));
    }

    public Task PairAsync(string address, CancellationToken ct = default)
    {
        Calls.Add($"pair:{Norm(address)}");
        Paired.Add(Norm(address));
        return Task.CompletedTask;
    }

    public Task UnpairAsync(string address, CancellationToken ct = default)
    {
        Calls.Add($"unpair:{Norm(address)}");
        Paired.Remove(Norm(address));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PairedDeviceInfo>> PairedDevicesAsync(CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<PairedDeviceInfo>)
            Paired.Select(p => new PairedDeviceInfo(p, p)).ToList());

    public Task<bool?> IsPoweredOnAsync(CancellationToken ct = default)
        => Task.FromResult(PoweredOn);

    public void StartMonitoring(IEnumerable<string> addresses, Action<ConnectionChange> onChange)
        => _onChange = onChange;

    public void StopMonitoring() => _onChange = null;

    /// Simulate a ConnectionStatusChanged notification for a monitored device.
    public void FireConnectionChange(string address, bool connected)
        => _onChange?.Invoke(new ConnectionChange(Norm(address), connected));
}
