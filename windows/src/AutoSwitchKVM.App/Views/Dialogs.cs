using AutoSwitchKVM.App.Services;
using AutoSwitchKVM.App.Platform;
using AutoSwitchKVM.App.Support;
using AutoSwitchKVM.App.ViewModels;
using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace AutoSwitchKVM.App.Views;

/// The Settings window's dialogs (spec §6 source learning/picking, device add, shortcut recording,
/// config import/export, profile rename/delete). Each takes the XamlRoot of the page that opens it.
internal static class Dialogs
{
    private static AppViewModel Vm => AppViewModel.Instance;

    private static ContentDialog Dialog(XamlRoot root, string title, object content, string primary, string close = "Cancel") => new()
    {
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary,
        XamlRoot = root,
    };

    private static TextBlock Paragraph(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.WrapWholeWords,
        Foreground = Theme.Brush("TextFillColorSecondaryBrush"),
    };

    public static async Task MessageAsync(XamlRoot root, string title, string message) =>
        await new ContentDialog { Title = title, Content = Paragraph(message), CloseButtonText = "OK", XamlRoot = root }.ShowAsync();

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string action)
    {
        var d = Dialog(root, title, Paragraph(message), action);
        d.DefaultButton = ContentDialogButton.Close;
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    // ---------------- Source ----------------

    public static async Task LearnSourceAsync(XamlRoot root)
    {
        var learner = Vm.Controller.CreateLearner();
        learner.Start();

        var count = new TextBlock { Text = "No changes yet", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        var body = new StackPanel { Spacing = 12, MaxWidth = 420 };
        body.Children.Add(Paragraph("Switch your KVM to the other computer and back. AutoSwitch watches which USB devices disappear and reappear."));
        body.Children.Add(new ProgressBar { IsIndeterminate = true });
        body.Children.Add(count);

        void OnLearn() => root.Content?.DispatcherQueue.Enqueue("learn progress", () =>
            count.Text = learner.ChangeCount == 1 ? "1 device changed" : $"{learner.ChangeCount} devices changed");
        learner.Changed += OnLearn;

        var result = await Dialog(root, "Learn your KVM", body, "Done").ShowAsync();
        learner.Changed -= OnLearn;
        if (result != ContentDialogResult.Primary) { learner.Cancel(); return; }

        await PickSourceFromCandidatesAsync(root, "Choose the KVM devices", learner.Finish(), "KVM", precheckMajorityVendor: true);
    }

    public static async Task PickSourceManuallyAsync(XamlRoot root)
    {
        var attached = (await Vm.Controller.UsbSnapshotAsync())
            .GroupBy(d => ((uint)d.VendorID << 16) | d.ProductID)
            .Select(g => g.First())
            .ToList();
        await PickSourceFromCandidatesAsync(root, "Pick from connected devices", attached, "KVM", precheckMajorityVendor: false);
    }

    private static async Task PickSourceFromCandidatesAsync(
        XamlRoot root, string title, IReadOnlyList<UsbDeviceInfo> candidates, string defaultName, bool precheckMajorityVendor)
    {
        if (candidates.Count == 0)
        {
            await MessageAsync(root, title, "No USB devices changed. Try again, and switch the KVM away and back while the dialog is open.");
            return;
        }

        // Ties are common (one changed VID:PID per vendor, e.g. the KVM hub vs a receiver behind
        // it): prefer the vendor that has a hub among its devices, since a KVM switches a hub.
        var majorityVendor = candidates
            .GroupBy(c => c.VendorID)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Any(d => (d.Name ?? "").Contains("hub", StringComparison.OrdinalIgnoreCase)))
            .First().Key;

        var checks = new List<(CheckBox Box, UsbDeviceInfo Device)>();
        var list = new StackPanel { Spacing = 2 };
        foreach (var dev in candidates.OrderBy(d => d.VendorID).ThenBy(d => d.ProductID))
        {
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(dev.Name) ? "Unknown USB device" : dev.Name });
            label.Children.Add(new TextBlock
            {
                Text = $"{dev.VendorID:X4}:{dev.ProductID:X4}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = Theme.Brush("TextFillColorSecondaryBrush"),
            });
            var box = new CheckBox { Content = label, IsChecked = precheckMajorityVendor && dev.VendorID == majorityVendor };
            checks.Add((box, dev));
            list.Children.Add(box);
        }

        var nameBox = new TextBox { Header = "Name", Text = defaultName };
        var body = new StackPanel { Spacing = 12, MaxWidth = 440 };
        body.Children.Add(Paragraph("Tick the devices that belong to the KVM. Prefer the ones that disappear when you switch away. A source uses one vendor."));
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260 });
        body.Children.Add(nameBox);

        if (await Dialog(root, title, body, "Use as source").ShowAsync() != ContentDialogResult.Primary) return;

        var chosen = checks.Where(c => c.Box.IsChecked == true).Select(c => c.Device).ToList();
        if (chosen.Count == 0) return;
        var vendor = chosen.GroupBy(d => d.VendorID).OrderByDescending(g => g.Count()).First();
        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? defaultName : nameBox.Text.Trim();
        Vm.SetSource(new USBSource { Name = name, VendorID = vendor.Key, ProductIDs = vendor.Select(d => d.ProductID).ToHashSet() });
    }

    public static async Task EditSourceAsync(XamlRoot root)
    {
        var s = Vm.Controller.Config.Source;
        var name = new TextBox { Header = "Name", PlaceholderText = "Desk KVM", Text = s?.Name ?? "" };
        var vendor = new TextBox { Header = "Vendor ID (hex)", PlaceholderText = "05E3", Text = s is null ? "" : s.VendorID.ToString("X4") };
        var pids = new TextBox
        {
            Header = "Product IDs (hex, comma-separated)",
            PlaceholderText = "0626, 0610",
            Text = s is null ? "" : string.Join(", ", s.ProductIDs.OrderBy(p => p).Select(p => p.ToString("X4"))),
        };
        var error = new TextBlock { Foreground = Theme.Brush("SystemFillColorCriticalBrush"), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 12, MinWidth = 360 };
        body.Children.Add(name);
        body.Children.Add(vendor);
        body.Children.Add(pids);
        body.Children.Add(error);

        var dialog = Dialog(root, "Edit source", body, "Save");
        dialog.PrimaryButtonClick += (sender, e) =>
        {
            var ok = TryParseHex(vendor.Text, out _) &&
                     pids.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(p => TryParseHex(p, out _));
            if (!ok)
            {
                error.Text = "Enter a hex vendor ID and at least one hex product ID.";
                error.Visibility = Visibility.Visible;
                e.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        TryParseHex(vendor.Text, out var v);
        var set = new HashSet<ushort>();
        foreach (var part in pids.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (TryParseHex(part, out var pid)) set.Add(pid);
        Vm.SetSource(new USBSource { Name = string.IsNullOrWhiteSpace(name.Text) ? "KVM" : name.Text.Trim(), VendorID = v, ProductIDs = set });
    }

    // ---------------- Devices ----------------

    public static async Task AddDeviceAsync(XamlRoot root)
    {
        var paired = await Vm.Controller.PairedDevicesAsync();

        var combo = new ComboBox { Header = "Paired with this PC", PlaceholderText = paired.Count == 0 ? "None found" : "Choose a device", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var p in paired) combo.Items.Add($"{p.Name}  ({p.Address}){(p.IsLowEnergy ? "  ·  Bluetooth LE" : "")}");
        var name = new TextBox { Header = "Name", PlaceholderText = "Magic Trackpad" };
        var addr = new TextBox { Header = "Bluetooth address", PlaceholderText = "A4:83:E7:5C:19:2D" };
        // On Windows a Classic HID bond is exclusive and release means unpair, so default it on.
        var managePairing = new CheckBox
        {
            Content = "Manage pairing (unpair when the KVM switches away, pair when it comes back)",
            IsChecked = true,
        };
        // How the picked device switches (spec §5a): what AutoSwitch can do with it, and the
        // matching Manage pairing default.
        var handoffTitle = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var handoffText = Paragraph("");
        var handoffLink = new HyperlinkButton { Content = "Which devices can switch?", NavigateUri = new Uri(HandoffText.LearnMoreUrl), Padding = new Thickness(0) };
        // Channel-switching devices (type 5): the channel to send the device to on release.
        var channel = new ComboBox
        {
            Header = "Other computer's channel (where the device goes when the KVM leaves this PC)",
            ItemsSource = new[] { "Automatic (found when AutoSwitch runs there)", "Channel 1", "Channel 2", "Channel 3" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        var handoffPanel = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
        handoffPanel.Children.Add(handoffTitle);
        handoffPanel.Children.Add(handoffText);
        handoffPanel.Children.Add(handoffLink);
        var pickVersion = 0;
        combo.SelectionChanged += async (_, _) =>
        {
            var i = combo.SelectedIndex;
            if (i < 0 || i >= paired.Count) return;
            var picked = paired[i];
            name.Text = picked.Name;
            addr.Text = picked.Address;
            managePairing.IsChecked = !picked.IsLowEnergy;
            var version = ++pickVersion;
            handoffTitle.Text = "Checking how this device switches…";
            handoffText.Text = "";
            handoffPanel.Visibility = Visibility.Visible;
            try
            {
                var a = await Vm.Controller.ClassifyAsync(picked.Address, picked.Name);
                if (version != pickVersion) return;   // another device was picked meanwhile
                handoffTitle.Text = HandoffText.Title(a.Type) + HandoffText.Confidence(a);
                handoffText.Text = a.Type == HandoffType.ChannelSwitch
                    ? HandoffText.WhatHappens(a.Type) + " " + a.Reason
                    : HandoffText.WhatHappens(a.Type);
                managePairing.IsChecked = a.DefaultManagePairing(picked.IsLowEnergy);
                channel.Visibility = a.Type == HandoffType.ChannelSwitch ? Visibility.Visible : Visibility.Collapsed;
                if (a.Type == HandoffType.ChannelSwitch)
                {
                    var channels = await Vm.Controller.GetChannelsAsync(picked.Address);
                    var names = await Vm.Controller.GetChannelNamesAsync(picked.Address);
                    if (version != pickVersion) return;
                    if (names is not null || channels is not null)
                    {
                        channel.ItemsSource = AppController.ChannelChoiceLabels("Automatic (found when AutoSwitch runs there)",
                            names, channels?.Count ?? names!.Count, channels);
                        channel.SelectedIndex = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Support.Log.Warn("app", $"add device: could not tell how {picked.Name} switches ({ex.Message})");
                handoffPanel.Visibility = Visibility.Collapsed;
            }
        };
        var error = new TextBlock { Foreground = Theme.Brush("SystemFillColorCriticalBrush"), Visibility = Visibility.Collapsed };

        var body = new StackPanel { Spacing = 12, MinWidth = 380 };
        body.Children.Add(Paragraph("Pick a device paired with this PC, or type its name and address."));
        body.Children.Add(combo);
        body.Children.Add(name);
        body.Children.Add(addr);
        body.Children.Add(handoffPanel);
        body.Children.Add(channel);
        body.Children.Add(managePairing);
        body.Children.Add(error);

        var dialog = Dialog(root, "Add a Bluetooth device", body, "Add");
        dialog.PrimaryButtonClick += (sender, e) =>
        {
            var digits = addr.Text.Replace(":", "").Replace("-", "").Trim();
            if (digits.Length != 12 || !digits.All(Uri.IsHexDigit))
            {
                error.Text = "Enter a Bluetooth address like 3C:50:02:BF:22:45.";
                error.Visibility = Visibility.Visible;
                e.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        Vm.AddDevice(new BTDevice
        {
            Name = string.IsNullOrWhiteSpace(name.Text) ? addr.Text.Trim() : name.Text.Trim(),
            Address = addr.Text.Trim(),
            ManagePairing = managePairing.IsChecked == true,
            ReleaseToChannel = channel.Visibility == Visibility.Visible && channel.SelectedIndex > 0 ? channel.SelectedIndex : null,
        });
    }

    // ---------------- Profiles ----------------

    public static async Task RenameProfileAsync(XamlRoot root)
    {
        var box = new TextBox { Text = Vm.ActiveProfileName, MinWidth = 320 };
        box.SelectAll();
        if (await Dialog(root, "Rename profile", box, "Rename").ShowAsync() == ContentDialogResult.Primary)
            Vm.RenameProfile(box.Text);
    }

    public static async Task DeleteProfileAsync(XamlRoot root)
    {
        if (!Vm.CanDeleteProfile) return;
        if (await ConfirmAsync(root, "Delete profile?", $"\"{Vm.ActiveProfileName}\" and its source and devices will be removed.", "Delete"))
            Vm.DeleteProfile();
    }

    // ---------------- Shortcuts ----------------

    public static async Task RecordShortcutAsync(XamlRoot root, string label, HotKeyAction action)
    {
        var preview = new TextBlock
        {
            Text = "Press keys…",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 8),
        };
        var body = new StackPanel { Spacing = 8, MinWidth = 340 };
        body.Children.Add(Paragraph("Press the new shortcut. It must include Ctrl, Alt, Shift or Win."));
        body.Children.Add(new Border
        {
            Child = preview,
            Background = Theme.Brush("ControlFillColorSecondaryBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
        });

        var dialog = Dialog(root, $"Shortcut: {label}", body, "Save");
        dialog.IsPrimaryButtonEnabled = false;
        KeyShortcut? captured = null;
        dialog.KeyDown += (_, e) =>
        {
            if (IsModifierKey(e.Key)) return;
            var mods = CurrentModifiers(out var disp);
            if (mods == 0) return;
            captured = new KeyShortcut { KeyCode = (uint)e.Key, Modifiers = mods, Display = disp + KeyName(e.Key) };
            preview.Text = captured.Display;
            dialog.IsPrimaryButtonEnabled = true;
            e.Handled = true;
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && captured != null)
            Vm.SetShortcut(action, captured);
    }

    private static bool IsModifierKey(VirtualKey k) =>
        k is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
          or VirtualKey.LeftControl or VirtualKey.RightControl
          or VirtualKey.LeftShift or VirtualKey.RightShift
          or VirtualKey.LeftMenu or VirtualKey.RightMenu
          or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static uint CurrentModifiers(out string display)
    {
        uint mods = 0;
        var sb = "";
        static bool Down(VirtualKey k) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);
        if (Down(VirtualKey.Control)) { mods |= HotKeyService.MOD_CONTROL; sb += "Ctrl+"; }
        if (Down(VirtualKey.Menu)) { mods |= HotKeyService.MOD_ALT; sb += "Alt+"; }
        if (Down(VirtualKey.Shift)) { mods |= HotKeyService.MOD_SHIFT; sb += "Shift+"; }
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) { mods |= HotKeyService.MOD_WIN; sb += "Win+"; }
        display = sb;
        return mods;
    }

    private static string KeyName(VirtualKey k)
    {
        if (k >= VirtualKey.A && k <= VirtualKey.Z) return ((char)('A' + (k - VirtualKey.A))).ToString();
        if (k >= VirtualKey.Number0 && k <= VirtualKey.Number9) return ((char)('0' + (k - VirtualKey.Number0))).ToString();
        return k.ToString();
    }

    // ---------------- Config import / export ----------------

    public static async Task ExportAsync(Window window)
    {
        var picker = new FileSavePicker { SuggestedFileName = "autoswitchkvm-config" };
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        new ConfigStore(file.Path) { OnSaveError = ex => Log.Error("config", $"export to '{file.Path}' failed", ex) }
            .Save(Vm.Controller.Config);
    }

    public static async Task ImportAsync(Window window, XamlRoot root)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        AppConfig imported;
        try
        {
            // Strict parse: a bad file must surface an error, not silently become defaults.
            imported = ConfigStore.Parse(await File.ReadAllTextAsync(file.Path));
        }
        catch (Exception ex)
        {
            Log.Error("config", $"import of '{file.Path}' failed", ex);
            await MessageAsync(root, "Import failed", $"\"{file.Name}\" is not a valid AutoSwitch KVM configuration.\n\n{ex.Message}");
            return;
        }
        Vm.Controller.ReplaceConfig(imported);
    }

    private static bool TryParseHex(string? text, out ushort value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return ushort.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
