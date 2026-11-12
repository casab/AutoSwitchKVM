using AutoSwitchKVM.App.Platform;
using AutoSwitchKVM.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutoSwitchKVM.App.Views;

public sealed partial class GeneralPage : Page
{
    public AppViewModel ViewModel => AppViewModel.Instance;

    public GeneralPage() => InitializeComponent();

    private void OnAddProfile(object sender, RoutedEventArgs e) => ViewModel.AddProfile();
    private async void OnRenameProfile(object sender, RoutedEventArgs e) => await Dialogs.RenameProfileAsync(XamlRoot);
    private async void OnDeleteProfile(object sender, RoutedEventArgs e) => await Dialogs.DeleteProfileAsync(XamlRoot);

    private async void OnRecordPause(object sender, RoutedEventArgs e) =>
        await Dialogs.RecordShortcutAsync(XamlRoot, "Pause or resume", HotKeyAction.TogglePause);
    private async void OnRecordConnect(object sender, RoutedEventArgs e) =>
        await Dialogs.RecordShortcutAsync(XamlRoot, "Connect all", HotKeyAction.ConnectAll);
    private async void OnRecordDisconnect(object sender, RoutedEventArgs e) =>
        await Dialogs.RecordShortcutAsync(XamlRoot, "Release all", HotKeyAction.DisconnectAll);

    private void OnClearPause(object sender, RoutedEventArgs e) => ViewModel.SetShortcut(HotKeyAction.TogglePause, null);
    private void OnClearConnect(object sender, RoutedEventArgs e) => ViewModel.SetShortcut(HotKeyAction.ConnectAll, null);
    private void OnClearDisconnect(object sender, RoutedEventArgs e) => ViewModel.SetShortcut(HotKeyAction.DisconnectAll, null);
    private void OnRestoreShortcuts(object sender, RoutedEventArgs e) => ViewModel.RestoreDefaultShortcuts();

    private async void OnOpenProject(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/casab/AutoSwitchKVM"));

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (SettingsWindow.Current is { } w) await Dialogs.ExportAsync(w);
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (SettingsWindow.Current is { } w) await Dialogs.ImportAsync(w, XamlRoot);
    }
}
