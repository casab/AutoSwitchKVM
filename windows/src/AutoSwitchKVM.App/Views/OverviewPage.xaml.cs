using AutoSwitchKVM.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoSwitchKVM.App.Views;

public sealed partial class OverviewPage : Page
{
    public AppViewModel ViewModel => AppViewModel.Instance;

    public OverviewPage() => InitializeComponent();

    private void OnOpenSource(object sender, RoutedEventArgs e) => SettingsWindow.Current?.Navigate("source");
    private void OnOpenDevices(object sender, RoutedEventArgs e) => SettingsWindow.Current?.Navigate("devices");
}
