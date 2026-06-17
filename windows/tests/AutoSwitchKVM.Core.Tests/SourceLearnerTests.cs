using AutoSwitchKVM.Core;
using AutoSwitchKVM.Core.Tests.Fakes;
using Xunit;

namespace AutoSwitchKVM.Core.Tests;

public class SourceLearnerTests
{
    private static UsbDeviceInfo Dev(ushort v, ushort p, string name = "") => new(v, p, name);

    [Fact]
    public void Detects_device_that_appears_during_window()
    {
        var usb = new FakeUsbMonitor();
        usb.SetDevices(Dev(0x1111, 0x2222, "Keyboard"));   // pre-existing baseline
        var learner = new SourceLearner(usb);

        learner.Start();
        // Simulate a KVM switch: the hub appears.
        usb.SetDevices(Dev(0x1111, 0x2222, "Keyboard"), Dev(0x05E3, 0x0626, "Hub"));

        var result = learner.Finish();
        var hit = Assert.Single(result);
        Assert.Equal((ushort)0x05E3, hit.VendorID);
        Assert.Equal((ushort)0x0626, hit.ProductID);
        Assert.Equal("Hub", hit.Name);
    }

    [Fact]
    public void Detects_device_that_disappears_during_window()
    {
        var usb = new FakeUsbMonitor();
        usb.SetDevices(Dev(0x1111, 0x2222, "Keyboard"), Dev(0x05E3, 0x0626, "Hub"));
        var learner = new SourceLearner(usb);

        learner.Start();
        usb.SetDevices(Dev(0x1111, 0x2222, "Keyboard"));   // hub removed (switched away)

        var result = learner.Finish();
        var hit = Assert.Single(result);
        Assert.Equal((ushort)0x05E3, hit.VendorID);
        Assert.Equal("Hub", hit.Name);   // resolved from the baseline snapshot
    }

    [Fact]
    public void Ignores_devices_present_for_the_whole_window()
    {
        var usb = new FakeUsbMonitor();
        usb.SetDevices(Dev(0x1111, 0x2222, "Keyboard"));
        var learner = new SourceLearner(usb);

        learner.Start();
        usb.SetDevices(Dev(0x1111, 0x2222, "Keyboard"));   // unchanged
        Assert.Equal(0, learner.ChangeCount);

        var result = learner.Finish();
        Assert.Empty(result);
    }

    [Fact]
    public void Cancel_clears_state_and_unsubscribes()
    {
        var usb = new FakeUsbMonitor();
        usb.SetDevices(Dev(0x1111, 0x2222));
        var learner = new SourceLearner(usb);

        learner.Start();
        usb.SetDevices(Dev(0x1111, 0x2222), Dev(0x05E3, 0x0626));
        Assert.Equal(1, learner.ChangeCount);

        learner.Cancel();
        Assert.False(learner.IsLearning);
        Assert.Equal(0, learner.ChangeCount);

        // After cancel, further USB changes must not be tracked.
        usb.SetDevices(Dev(0x1111, 0x2222), Dev(0x05E3, 0x0626), Dev(0x09DA, 0x0001));
        Assert.Equal(0, learner.ChangeCount);
    }
}
