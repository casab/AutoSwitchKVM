using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AutoSwitchKVM.App.Platform;

/// Windows toast notifications via the Windows App SDK AppNotificationManager (mirrors the macOS
/// Notifier). For an unpackaged app, Register() performs the COM/registry registration needed for
/// toasts to appear; without it they are silently dropped.
public sealed class ToastNotifier
{
    private bool _registered;

    public void Register()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            _registered = false; // toasts unavailable; Notify() will no-op
        }
    }

    public void Unregister()
    {
        try { if (_registered) AppNotificationManager.Default.Unregister(); }
        catch { /* ignore */ }
    }

    public void Notify(string title, string body)
    {
        if (!_registered) return;
        try
        {
            var toast = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body)
                .BuildNotification();
            AppNotificationManager.Default.Show(toast);
        }
        catch { /* ignore */ }
    }
}
