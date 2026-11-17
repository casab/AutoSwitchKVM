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

## v0.5.1 (2026-11-18)

### macOS

- The switch status panel no longer pops up when you wake a device after a switch. It shows only
  while the KVM is switching, plus once when a device that didn't connect comes back.
- A device that can't switch channels and didn't connect now reads "Not connected" instead of
  "Didn't come over: press its channel key".

### Windows

- **The Magic Trackpad no longer gets stuck after you shut down the PC while it's using it.**
  AutoSwitch now asks Windows for the few seconds it needs to release the trackpad before the PC
  turns off. Before, Windows closed it too soon, the PC stayed paired, and the trackpad wouldn't
  connect again for minutes after the next start. If the release takes longer than about 5
  seconds, the shutdown screen shows "Releasing Bluetooth devices for your other computer".
- After the KVM leaves, a pairing attempt that Windows is still finishing can no longer pair the
  trackpad to this PC while the Mac is taking it.
- Trackpad retries no longer stall for 30 seconds on a speaker check that only applies to
  speakers and headphones.

## v0.5.0 (2026-11-16)

### Both

- **Bluetooth speakers and headphones follow the KVM, now on Windows too:** when the KVM leaves
  a computer, AutoSwitch disconnects them there, keeping their pairing, and the other computer
  connects them. Keep Manage pairing off for them.
- **A switch status panel instead of notifications** (by the tray on Windows, under the menu bar
  on the Mac): while the KVM switches, it shows each device connecting, connected, sent over or
  released, then hides by itself. No sound, it never takes focus, and nothing piles up in the
  notification center. A device that's switched off is reported once, not at every retry or
  switch. On by default (Settings ▸ Switch status).
- **A device that's asleep or switched off no longer holds up the others** (say, a keyboard you
  haven't touched for days): devices that don't need pairing connect at the same time, the ones
  that answer go first, and the sleeping one connects as soon as you wake it. AutoSwitch keeps
  trying it every 30 seconds instead of giving up with an error.

### Windows

- When a speaker or headphones follow the KVM too, the trackpad no longer waits for them on its
  way to the other computer.
- A Logitech keyboard you wake up on this PC while the KVM is on the other computer is sent
  there, even when it had been asleep since AutoSwitch started.

## v0.4.0 (2026-11-14)

### Both

- **ZMK keyboards follow the KVM**, with the free AutoSwitch firmware module
  [zmk-autoswitch](https://github.com/casab/zmk-autoswitch): when the KVM leaves a computer,
  AutoSwitch switches the keyboard to the other computer's Bluetooth profile, the same way it
  does for Logitech Easy-Switch keyboards. Add the module to your zmk-config, flash, and pair the
  keyboard again on each computer. A keyboard paired with just two computers works without setup.
- Channel-switching keyboards show which computer each channel belongs to: AutoSwitch writes the
  computer's name on its own channel when the keyboard has none there (ZMK keyboards). Settings
  flags channels that aren't paired with anything, on Logitech keyboards too, and never sends a
  keyboard to one.
- A keyboard you switch by hand stays where you put it: AutoSwitch no longer sends it back when
  it comes to this computer while the KVM is on the other one, or pulls it back when it leaves
  while the KVM is here.
- Starting AutoSwitch while the KVM is on the other computer no longer sends your keyboard away:
  a channel-switching keyboard stays on whichever computer it's on. The trackpad is still
  released.

### Windows

- Notifications show up in installed versions (they never did before: only development builds
  showed them). This includes "Restart to update".
- This PC's name is written on a keyboard in capitals, as Windows gives it to Logitech keyboards.

### macOS

- Bluetooth LE keyboards (such as ZMK keyboards) can be added, and show as connected.

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
