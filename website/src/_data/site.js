// Site-wide values. Download buttons go through /download/* (see src/_redirects), so the real
// release URLs live in one place.
export default {
  name: "AutoSwitch KVM",
  url: "https://autoswitchkvm.com",
  tagline: "Your Bluetooth trackpad follows your KVM switch.",
  description:
    "AutoSwitch KVM moves your Bluetooth trackpad, keyboard and mouse to whichever computer your KVM switch picks. Free for macOS and Windows.",
  github: "https://github.com/casab/AutoSwitchKVM",
  releases: "https://github.com/casab/AutoSwitchKVM/releases",
  issues: "https://github.com/casab/AutoSwitchKVM/issues",
  download: {
    mac: "/download/mac",
    windows: "/download/windows",
    windowsArm: "/download/windows-arm64",
  },
};
