using AutoSwitchKVM.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoSwitchKVM.App.Views;

public sealed partial class SourcePage : Page
{
    public AppViewModel ViewModel => AppViewModel.Instance;

    public SourcePage() => InitializeComponent();

    private async void OnLearn(object sender, RoutedEventArgs e) => await Dialogs.LearnSourceAsync(XamlRoot);
    private async void OnPick(object sender, RoutedEventArgs e) => await Dialogs.PickSourceManuallyAsync(XamlRoot);
    private async void OnEdit(object sender, RoutedEventArgs e) => await Dialogs.EditSourceAsync(XamlRoot);

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.ConfirmAsync(XamlRoot, "Remove the KVM source?",
                "Devices won't connect or release automatically until you set up a new source.", "Remove"))
            ViewModel.SetSource(null);
    }
}
