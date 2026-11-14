using System.Collections.Concurrent;

namespace AutoSwitchKVM.Core;

/// Several channel-switching backends behind one IHostSwitcher (Logitech HID++, the AutoSwitch ZMK
/// module, …). The first backend that answers for a device owns it from then on (a device's
/// protocol never changes).
public sealed class CompositeHostSwitcher : IHostSwitcher
{
    private readonly IReadOnlyList<IHostSwitcher> _backends;
    private readonly ConcurrentDictionary<string, IHostSwitcher> _owner = new();

    public CompositeHostSwitcher(params IHostSwitcher[] backends) => _backends = backends;

    private static string Key(string address) => address.Replace(":", "").Replace("-", "").ToUpperInvariant();

    public async Task<HostChannels?> GetChannelsAsync(string address, CancellationToken ct = default)
    {
        if (_owner.TryGetValue(Key(address), out var owner))
            return await owner.GetChannelsAsync(address, ct);
        foreach (var backend in _backends)
        {
            var channels = await backend.GetChannelsAsync(address, ct);
            if (channels is null) continue;
            _owner[Key(address)] = backend;
            return channels;
        }
        return null;
    }

    public async Task SwitchToAsync(string address, int channel, CancellationToken ct = default) =>
        await (await OwnerAsync(address, ct)).SwitchToAsync(address, channel, ct);

    public async Task MarkCurrentChannelAsync(string address, CancellationToken ct = default) =>
        await (await OwnerAsync(address, ct)).MarkCurrentChannelAsync(address, ct);

    public async Task<int?> GetCurrentChannelAsync(string address, CancellationToken ct = default)
    {
        if (_owner.TryGetValue(Key(address), out var owner)) return await owner.GetCurrentChannelAsync(address, ct);
        return (await GetChannelsAsync(address, ct))?.Current;
    }

    public async Task<IReadOnlyList<string?>?> GetChannelNamesAsync(string address, CancellationToken ct = default)
    {
        if (!_owner.TryGetValue(Key(address), out var owner))
        {
            if (await GetChannelsAsync(address, ct) is null || !_owner.TryGetValue(Key(address), out owner)) return null;
        }
        return await owner.GetChannelNamesAsync(address, ct);
    }

    private async Task<IHostSwitcher> OwnerAsync(string address, CancellationToken ct)
    {
        if (_owner.TryGetValue(Key(address), out var owner)) return owner;
        if (await GetChannelsAsync(address, ct) is not null && _owner.TryGetValue(Key(address), out owner)) return owner;
        throw new BluetoothException(BluetoothFailureKind.DeviceNotFound,
            "the device doesn't answer any channel-switching protocol (not connected to this computer?)");
    }
}
