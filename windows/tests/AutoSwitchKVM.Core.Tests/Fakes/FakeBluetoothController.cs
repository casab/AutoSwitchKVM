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
    public Func<Task>? BeforePair { get; set; }
    /// When set, connect/disconnect record the call, then throw without changing state.
    public Exception? ConnectError { get; set; }
    public Exception? DisconnectError { get; set; }
    /// Per-call scripted failures: each pair/connect/unpair call dequeues one entry (null =
    /// succeed) before falling back to the sticky *Error property. Lets tests model sequences
    /// like "busy, busy, busy, then pairs".
    public Queue<Exception?> PairErrors { get; } = new();
    public Queue<Exception?> ConnectErrors { get; } = new();
    public Queue<Exception?> UnpairErrors { get; } = new();
    /// When set, IsConnectedAsync throws it (models a failing status query).
    public Exception? IsConnectedError { get; set; }
    /// When true, PairAsync never completes on its own — only the caller's token ends it
    /// (models a pair the device never answers).
    public bool PairHangs { get; set; }

    private Action<ConnectionChange>? _onChange;

    private static string Norm(string a) => a.Replace(":", "").Replace("-", "").ToUpperInvariant();

    // Reads are intentionally NOT recorded in Calls (mirrors the macOS mock), so order/count
    // assertions reflect only state-changing calls (connect/disconnect/pair/unpair).
    public Task<bool> IsConnectedAsync(string address, CancellationToken ct = default)
    {
        if (IsConnectedError is not null) throw IsConnectedError;
        return Task.FromResult(Connected.Contains(Norm(address)));
    }

    /// HID readiness per call (address digits → true/false/null). Default null = "can't tell",
    /// so the engine falls back to the link-only behavior (drop + settle + connect after a pair).
    public Func<string, bool?>? HidReady { get; set; }

    public Task<bool?> IsHidReadyAsync(string address, CancellationToken ct = default)
        => Task.FromResult(HidReady?.Invoke(Norm(address)));

    public async Task ConnectAsync(string address, CancellationToken ct = default)
    {
        if (BeforeConnect is not null) await BeforeConnect();
        Calls.Add($"connect:{Norm(address)}");
        if (ConnectErrors.Count > 0 && ConnectErrors.Dequeue() is { } scripted) throw scripted;
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

    public async Task PairAsync(string address, CancellationToken ct = default)
    {
        if (BeforePair is not null) await BeforePair();
        Calls.Add($"pair:{Norm(address)}");
        if (PairHangs) await Task.Delay(Timeout.Infinite, ct);
        if (PairErrors.Count > 0 && PairErrors.Dequeue() is { } scripted) throw scripted;
        Paired.Add(Norm(address));
    }

    /// Invoked after a successful unpair — lets tests model state that a fresh bond repairs
    /// (e.g. clear ConnectError to simulate a dead link key being replaced).
    public Action? AfterUnpair { get; set; }

    public Task UnpairAsync(string address, CancellationToken ct = default)
    {
        Calls.Add($"unpair:{Norm(address)}");
        if (UnpairErrors.Count > 0 && UnpairErrors.Dequeue() is { } scripted) throw scripted;
        Paired.Remove(Norm(address));
        AfterUnpair?.Invoke();
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
