# Changelog

What changed for users, per release. The release workflow copies a version's section into its
GitHub release (and from there into the in-app update notes).

**How to use it** (both agents):

- When you commit a change a user would notice, add a line under **Unreleased**, in the right
  group: **Both**, **macOS** or **Windows**. Plain words, what the user sees, not how. Skip
  internal changes (refactors, tests, CI) unless they change what users get.
- To release: rename `## Unreleased` to `## vX.Y.Z (YYYY-MM-DD)`, check that both platforms'
  changes since the last tag are in it (`git log vPREV..HEAD`), add a new empty `## Unreleased`
  above it, commit, then tag. A stable tag without its section fails the release.
- A pre-release tag (`vX.Y.Z-rc.1`) uses its own section if there is one, otherwise
  `vX.Y.Z`, otherwise **Unreleased**.

## Unreleased

## v0.3.1 (2026-11-13)

### macOS

- Updates work again: Macs on 0.2.0 or earlier weren't offered 0.3.0 (its build number was too
  low). The log now records each update check's result.
- The Logitech channel choice shows each channel's computer name, as the keyboard stores it.

## v0.3.0 (2026-11-13)

### Both

- Logitech Easy-Switch keyboards and mice (e.g. K380) follow the KVM: when the KVM leaves a
  computer, AutoSwitch tells the device to switch to the other computer's channel. The pairings
  stay as they are. The other computer's channel is found automatically.
- Settings shows how each device handles a second computer (its "handoff type") and whether
  AutoSwitch can switch it. Add device sets Manage pairing to match.
- A quick switch away and back no longer leaves a Logitech keyboard on the wrong computer.

### macOS

- Adding, removing or re-enabling a device no longer reconnects every device, only that one.
- Headphones and speakers with Manage pairing off connect and disconnect with the KVM (they
  were counted as failed connections before).
- The website's Download for Mac link always gets the newest version.
- The Mac asks once for **Input Monitoring** permission: it needs it to switch Logitech devices.

### Windows

- Devices are released in a better order (channel switches first), and a release stops as soon
  as the KVM comes back.
- A Logitech keyboard on its way from the other computer is waited for instead of showing
  "Problem", and one that lands on the wrong computer after a quick switch is sent on.
- The Logitech channel choice shows each channel's computer name (e.g. "Channel 2 · MacBook").
- Device guide: Bluetooth-to-USB adapters (e.g. 8BitDo for controllers) as a way to make
  devices that can't be switched follow the KVM.

## v0.2.0 (2026-11-13)

### Both

- New README and website (autoswitchkvm.com) for users.

### Windows

- Bluetooth LE devices (e.g. ZMK keyboards) can be added and show their status.
- Fixed a crash when adding or removing a device after visiting Overview.
- Devices ▸ Remove works again.
- Adding, removing or re-enabling a device only connects that device; expanded rows stay put.

## v0.1.1 (2026-11-13)

### macOS

- Updates live in Settings ▸ General; the menu bar panel no longer has an Updates button.

## v0.1.0 (2026-11-13)

First release.

- Windows: installer, with automatic updates from GitHub releases.
- macOS: DMG, with automatic updates (Sparkle).
