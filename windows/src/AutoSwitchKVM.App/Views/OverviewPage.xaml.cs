using AutoSwitchKVM.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoSwitchKVM.App.Views;

public sealed partial class OverviewPage : Page
{
    public AppViewModel ViewModel => AppViewModel.Instance;

    public OverviewPage()
    {
        InitializeComponent();
        // Bind the shared device list only while this page is on screen. A page navigated away
        // from stays subscribed until it is collected, and its detached ItemsRepeater throws
        // E_FAIL (0x80004005) when the list's membership changes (add/remove a device): that
        // crashed the app on 2026-11-13.
        Loaded += (_, _) => DeviceList.ItemsSource = ViewModel.Devices;
        Unloaded += (_, _) => DeviceList.ItemsSource = null;
    }

    private void OnOpenSource(object sender, RoutedEventArgs e) => SettingsWindow.Current?.Navigate("source");
    private void OnOpenDevices(object sender, RoutedEventArgs e) => SettingsWindow.Current?.Navigate("devices");
}
