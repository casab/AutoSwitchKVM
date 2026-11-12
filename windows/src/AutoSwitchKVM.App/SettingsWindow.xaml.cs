using AutoSwitchKVM.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT.Interop;

namespace AutoSwitchKVM.App;

/// Settings window: Mica backdrop, custom title bar, NavigationView with one page per area
/// (Views/*Page.xaml). Pages bind to the shared AppViewModel; dialogs live in Views/Dialogs.cs.
public sealed partial class SettingsWindow : Window
{
    /// The open Settings window (pages use it for file pickers), or null.
    public static new SettingsWindow? Current { get; private set; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["overview"] = typeof(OverviewPage),
        ["devices"] = typeof(DevicesPage),
        ["source"] = typeof(SourcePage),
        ["general"] = typeof(GeneralPage),
        ["diagnostics"] = typeof(DiagnosticsPage),
    };

    public SettingsWindow()
    {
        InitializeComponent();
        Title = "AutoSwitch KVM";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        Current = this;
        Closed += (_, _) => { if (Current == this) Current = null; };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico")); }
        catch { /* icon is cosmetic */ }

        try
        {
            // AppWindow.Resize takes physical pixels; scale the logical size by the window DPI.
            var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
            if (scale <= 0) scale = 1.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1040 * scale), (int)(740 * scale)));
        }
        catch { /* sizing is best-effort */ }

        Navigate("overview");
    }

    /// Show a page by tag (overview, devices, source, general, diagnostics).
    public void Navigate(string tag)
    {
        if (!Pages.ContainsKey(tag)) tag = "overview";
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string)i.Tag == tag);
        if (item != null && !ReferenceEquals(Nav.SelectedItem, item)) Nav.SelectedItem = item;   // raises SelectionChanged
        else NavigateFrame(tag);
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag }) NavigateFrame(tag);
    }

    private void NavigateFrame(string tag)
    {
        if (!Pages.TryGetValue(tag, out var type) || ContentFrame.CurrentSourcePageType == type) return;
        ContentFrame.Navigate(type, null, new EntranceNavigationTransitionInfo());
    }
}
