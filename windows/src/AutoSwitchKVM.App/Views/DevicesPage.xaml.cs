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
        // The button carries its device in Tag (x:Bind). Its DataContext is not the device: the row
        // sits in a SettingsExpander's Items, which the toolkit hosts in its own repeater.
        if ((sender as FrameworkElement)?.Tag is not DeviceViewModel device)
        {
            Support.Log.Warn("app", "remove device: the button has no device attached; nothing removed");
            return;
        }
        if (await Dialogs.ConfirmAsync(XamlRoot, $"Remove {device.Name}?",
                "AutoSwitch will stop managing this device. It stays paired however it is right now.", "Remove"))
            ViewModel.RemoveDevice(device.Id);
    }
}
