using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Models;
using Xunit;

namespace AutoSwitchKVM.Core.Tests;

/// Ports osx ModelsTests.swift, plus C#-specific model checks (DisplayName, DeviceStatus equality).
public class ModelsTests
{
    [Fact]
    public void Address_digits_strip_separators_and_uppercase()
    {
        Assert.Equal("3C5002BF2245", new BTDevice { Address = "3C:50:02:BF:22:45" }.AddressDigits);
        Assert.Equal("3C5002BF2245", new BTDevice { Address = "3c-50-02-bf-22-45" }.AddressDigits);
    }

    [Fact]
    public void Source_display_vid_pid_sorts_product_ids()
    {
        var source = new USBSource { Name = "Hub", VendorID = 0x05E3, ProductIDs = new() { 0x0626, 0x0610 } };
        Assert.Equal("0x05E3 : 0x0610, 0x0626", source.DisplayVidPid);
    }

    [Fact]
    public void Device_status_labels()
    {
        Assert.Equal("Connected", DeviceStatus.Connected.Label);
        Assert.Equal("Bluetooth off", DeviceStatus.BluetoothOff.Label);
        Assert.Equal("Error: boom", DeviceStatus.Error("boom").Label);
    }

    [Fact]
    public void Device_status_value_equality()
    {
        Assert.Equal(DeviceStatus.Connected, DeviceStatus.Connected);
        Assert.NotEqual(DeviceStatus.Connected, DeviceStatus.Disconnected);
        Assert.Equal(DeviceStatus.Error("a"), DeviceStatus.Error("a"));
        Assert.NotEqual(DeviceStatus.Error("a"), DeviceStatus.Error("b"));
    }

    [Fact]
    public void Usb_device_display_name()
    {
        Assert.Equal("Hub  (0x05E3:0x0626)", new UsbDeviceInfo(0x05E3, 0x0626, "Hub").DisplayName);
        Assert.Equal("Unknown USB device  (0x1234:0x5678)", new UsbDeviceInfo(0x1234, 0x5678).DisplayName);
    }

    [Fact]
    public void Default_config_has_one_active_profile_with_defaults()
    {
        var cfg = AppConfig.Default();
        Assert.Single(cfg.Profiles);
        Assert.Equal(cfg.Profiles[0].Id, cfg.ActiveProfileID);
        Assert.Null(cfg.Source);
        Assert.Empty(cfg.Devices);
    }

    [Fact]
    public void Normalized_repairs_empty_profiles_and_bad_active_id()
    {
        var cfg = new AppConfig { Profiles = new List<Profile>(), ActiveProfileID = Guid.NewGuid() };
        cfg.Normalized();
        Assert.Single(cfg.Profiles);
        Assert.Equal(cfg.Profiles[0].Id, cfg.ActiveProfileID);
    }
}
