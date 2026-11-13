using Microsoft.UI.Dispatching;

namespace AutoSwitchKVM.App.Support;

/// The UI thread's SynchronizationContext, with every posted callback guarded. `await`
/// continuations and, crucially, exceptions from `async void` methods (click handlers, view-model
/// actions) are posted through here. Unguarded, such an exception becomes a stowed exception that
/// kills the process (0xc000027b in CoreMessagingXP.dll) without a line in the app log; seen
/// 2026-11-13. Guarded, it is logged with its stack and the app keeps running.
public sealed class SafeSynchronizationContext : SynchronizationContext
{
    private readonly DispatcherQueue _queue;

    public SafeSynchronizationContext(DispatcherQueue queue) => _queue = queue;

    public override void Post(SendOrPostCallback d, object? state) =>
        _queue.TryEnqueue(() => Run(d, state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (_queue.HasThreadAccess)
        {
            Run(d, state);
            return;
        }
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        _queue.TryEnqueue(() =>
        {
            try { d(state); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (error is not null) throw new InvalidOperationException("Send callback failed", error);
    }

    public override SynchronizationContext CreateCopy() => new SafeSynchronizationContext(_queue);

    private static void Run(SendOrPostCallback d, object? state)
    {
        try { d(state); }
        catch (Exception ex) { Log.Error("app", "unhandled exception on the UI thread (async continuation)", ex); }
    }
}
