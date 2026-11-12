using AutoSwitchKVM.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoSwitchKVM.App.Views;

public sealed partial class DevicesPage : Page
{
    public AppViewModel ViewModel => AppViewModel.Instance;

    public DevicesPage() => InitializeComponent();

    private async void OnAddDevice(object sender, RoutedEventArgs e) => await Dialogs.AddDeviceAsync(XamlRoot);

    private async void OnRemoveDevice(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceViewModel device) return;
        if (await Dialogs.ConfirmAsync(XamlRoot, $"Remove {device.Name}?",
                "AutoSwitch will stop managing this device. It stays paired however it is right now.", "Remove"))
            ViewModel.RemoveDevice(device.Id);
    }
}
