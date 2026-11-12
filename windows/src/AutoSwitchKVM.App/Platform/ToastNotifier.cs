using AutoSwitchKVM.App.Support;
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
            // Unpackaged: there is no package manifest to take the name and icon from, so pass them
            // (otherwise toasts show the exe name and a generic icon).
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.png");
            if (File.Exists(icon))
                AppNotificationManager.Default.Register("AutoSwitch KVM", new Uri(icon));
            else
                AppNotificationManager.Default.Register();
            _registered = true;
            Log.Debug("toast", "AppNotificationManager registered");
        }
        catch (Exception ex)
        {
            _registered = false; // toasts unavailable; Notify() will no-op
            Log.Warn("toast", $"registration failed (toasts disabled): {ex.Message}");
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
        catch (Exception ex)
        {
            Log.Warn("toast", $"showing '{title}' failed: {ex.Message}");
        }
    }
}
