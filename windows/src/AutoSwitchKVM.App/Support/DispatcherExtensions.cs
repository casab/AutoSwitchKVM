using Microsoft.UI.Dispatching;

namespace AutoSwitchKVM.App.Support;

/// An exception thrown inside a DispatcherQueue.TryEnqueue callback does not reach
/// Application.UnhandledException: it becomes a stowed exception that kills the process
/// (0xc000027b in CoreMessagingXP.dll, with no line in the app log). Every callback the app
/// enqueues goes through Enqueue, which logs the exception instead.
public static class DispatcherExtensions
{
    public static bool Enqueue(this DispatcherQueue queue, string what, Action action) =>
        queue.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error("app", $"UI callback '{what}' failed", ex); }
        });

    /// Async callback: the synchronous part and the awaited remainder are both covered.
    public static bool Enqueue(this DispatcherQueue queue, string what, Func<Task> action) =>
        queue.TryEnqueue(async () =>
        {
            try { await action(); }
            catch (Exception ex) { Log.Error("app", $"UI callback '{what}' failed", ex); }
        });
}
