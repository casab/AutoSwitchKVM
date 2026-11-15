// Site-wide values. Download buttons go through /download/* (see src/_redirects), so the real
// release URLs live in one place.
export default {
  name: "AutoSwitch KVM",
  url: "https://autoswitchkvm.com",
  tagline: "Your Bluetooth devices follow your KVM switch.",
  description:
    "AutoSwitch KVM moves your Bluetooth keyboard, mouse, trackpad and headphones to whichever computer your KVM switch picks: Apple Magic accessories, Logitech Easy-Switch, ZMK keyboards, headphones and speakers. Free for macOS and Windows.",
  github: "https://github.com/casab/AutoSwitchKVM",
  releases: "https://github.com/casab/AutoSwitchKVM/releases",
  issues: "https://github.com/casab/AutoSwitchKVM/issues",
  download: {
    mac: "/download/mac",
    windows: "/download/windows",
    windowsArm: "/download/windows-arm64",
  },
};
