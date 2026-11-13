using AutoSwitchKVM.App.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace AutoSwitchKVM.App;

/// Custom entry point (DISABLE_XAML_GENERATED_MAIN): Velopack must run first, before WinUI starts,
/// so it can handle its install / update / uninstall hooks (it may exit the process right here).
/// The rest mirrors the XAML-generated Main.
public static class Program
{
    /// True on the first launch after a fresh install (the app then opens Settings for setup).
    public static bool IsFirstRun { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnFirstRun(_ => IsFirstRun = true)
            // Uninstall: undo what the app registered outside its folder. Fast callbacks must be
            // quick and must not show UI.
            .OnBeforeUninstallFastCallback(_ =>
            {
                try { LoginItem.SetEnabled(false); } catch { /* best-effort */ }
                try { Microsoft.Windows.AppNotifications.AppNotificationManager.Default.UnregisterAll(); } catch { /* best-effort */ }
            })
            .Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callbackParams =>
        {
            // Guarded context: an exception from an async void method is logged instead of
            // killing the process (see SafeSynchronizationContext).
            SynchronizationContext.SetSynchronizationContext(
                new Support.SafeSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
