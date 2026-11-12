using System.Diagnostics;
using AutoSwitchKVM.App.Support;
using AutoSwitchKVM.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AutoSwitchKVM.App.Views;

public sealed partial class DiagnosticsPage : Page
{
    public AppViewModel ViewModel => AppViewModel.Instance;

    public string LogHint => $"Daily log files are kept for 14 days in {Path.GetDirectoryName(Log.FilePath)}. " +
                             "Driver traces: windows\\tools\\driver-trace.ps1.";

    // Re-rendering the whole buffer on every log line made the window sluggish during handoffs;
    // refresh at most twice a second.
    private int _refreshQueued;

    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Log.Changed += OnLogChanged;
            RenderLog();
        };
        Unloaded += (_, _) => Log.Changed -= OnLogChanged;
    }

    private void OnLogChanged()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await Task.Delay(500);
                Interlocked.Exchange(ref _refreshQueued, 0);
                RenderLog();
            }
            catch (Exception ex) { Log.Warn("ui", $"log view refresh failed: {ex.Message}"); }
        });
    }

    private void RenderLog()
    {
        // Keep the view pinned to the newest line unless the user scrolled up.
        LogBox.Text = Log.PlainText();
        LogBox.SelectionStart = LogBox.Text.Length;
    }

    private async void OnRefreshUsb(object sender, RoutedEventArgs e)
    {
        var snapshot = await ViewModel.Controller.UsbSnapshotAsync();
        UsbText.Text = AppViewModel.DescribeMatchingUsb(ViewModel.Controller.Config.Source, snapshot);
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        var pkg = new DataPackage();
        pkg.SetText(Log.PlainText());
        Clipboard.SetContent(pkg);
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(Log.FilePath);
            if (!string.IsNullOrEmpty(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("ui", $"open log folder failed: {ex.Message}"); }
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => Log.Clear();
}
