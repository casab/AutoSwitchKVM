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

    private Action<ConnectionChange>? _onChange;

    private static string Norm(string a) => a.Replace(":", "").Replace("-", "").ToUpperInvariant();

    // Reads are intentionally NOT recorded in Calls (mirrors the macOS mock), so order/count
    // assertions reflect only state-changing calls (connect/disconnect/pair/unpair).
    public Task<bool> IsConnectedAsync(string address, CancellationToken ct = default)
        => Task.FromResult(Connected.Contains(Norm(address)));

    public Task ConnectAsync(string address, CancellationToken ct = default)
    {
        Calls.Add($"connect:{Norm(address)}");
        Connected.Add(Norm(address));
        _onChange?.Invoke(new ConnectionChange(Norm(address), true));
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string address, CancellationToken ct = default)
    {
        Calls.Add($"disconnect:{Norm(address)}");
        Connected.Remove(Norm(address));
        _onChange?.Invoke(new ConnectionChange(Norm(address), false));
        return Task.CompletedTask;
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
}
