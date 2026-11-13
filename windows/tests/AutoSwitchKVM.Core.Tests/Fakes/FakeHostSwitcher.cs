using AutoSwitchKVM.Core;

namespace AutoSwitchKVM.Core.Tests.Fakes;

/// IHostSwitcher fake: one channel-switching device (a K380-like keyboard with 3 channels).
public sealed class FakeHostSwitcher : IHostSwitcher
{
    public string? SupportedAddress { get; set; }
    public bool Reachable { get; set; } = true;
    public int Count { get; set; } = 3;
    public int Current { get; set; }
    public byte[] Marks { get; set; } = new byte[3];
    public bool[]? Paired { get; set; }
    public List<string> Calls { get; } = new();
    /// Also record calls here (e.g. the Bluetooth fake's Calls, to check ordering across both).
    public List<string>? SharedLog { get; set; }
    /// Runs inside GetChannelsAsync (e.g. to make the KVM come back mid-release).
    public Action? OnGetChannels { get; set; }

    private static string Norm(string a) => a.Replace(":", "").Replace("-", "").ToUpperInvariant();
    private bool Mine(string address) => SupportedAddress is not null && Norm(address) == Norm(SupportedAddress);

    public Task<HostChannels?> GetChannelsAsync(string address, CancellationToken ct = default)
    {
        OnGetChannels?.Invoke();
        return Task.FromResult(Mine(address) && Reachable ? new HostChannels(Count, Current, Marks.ToArray()) { Paired = Paired } : null);
    }

    public Task SwitchToAsync(string address, int channel, CancellationToken ct = default)
    {
        Calls.Add($"switch:{Norm(address)}:{channel}");
        SharedLog?.Add($"switch:{Norm(address)}:{channel}");
        Current = channel;
        return Task.CompletedTask;
    }

    public Task MarkCurrentChannelAsync(string address, CancellationToken ct = default)
    {
        Calls.Add($"mark:{Norm(address)}:{Current}");
        Marks[Current] = HostChannels.AutoSwitchMark;
        return Task.CompletedTask;
    }
}
