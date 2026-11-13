# AutoSwitch KVM module for ZMK

A [ZMK](https://zmk.dev) module that lets the [AutoSwitch KVM](https://autoswitchkvm.com) app
change your keyboard's Bluetooth profile. When you press the KVM button, AutoSwitch on the
computer you're leaving tells the keyboard to switch to the other computer's profile, the same
way it does for Logitech Easy-Switch keyboards. Your pairings stay as they are.

> **Status:** builds against ZMK `main` (Zephyr 4.1) and the Windows app supports it. Not yet
> tried on a keyboard; the macOS app doesn't speak it yet.

## How it works

- Each computer pairs with its own profile, as usual (`&bt BT_SEL 0`, `BT_SEL 1`, ...).
- The module adds a small vendor HID interface (from
  [zzeneg/zmk-raw-hid](https://github.com/zzeneg/zmk-raw-hid)) over Bluetooth and USB. AutoSwitch
  uses it to read the profiles and to select one.
- When AutoSwitch first sees the keyboard while the KVM is on its computer, it marks that profile
  as "AutoSwitch runs here". The mark is stored on the keyboard with the profile's pairing and is
  dropped if that profile is later paired with another computer.
- When the KVM leaves, AutoSwitch sends the keyboard to the other computer's marked profile (or the
  profile you chose in the device's settings). It never sends it to an empty profile.

## Add it to your zmk-config

1. `config/west.yml`: add the raw HID transport and this module.

   ```yaml
   manifest:
     remotes:
       - name: zmkfirmware
         url-base: https://github.com/zmkfirmware
       - name: zzeneg
         url-base: https://github.com/zzeneg
       - name: casab
         url-base: https://github.com/casab
     projects:
       - name: zmk
         remote: zmkfirmware
         revision: main
         import: app/west.yml
       - name: zmk-raw-hid
         remote: zzeneg
         revision: main
       - name: zmk-autoswitch
         remote: casab
         repo-path: AutoSwitchKVM
         revision: main
     self:
       path: config
   ```

2. `build.yaml`: add the `raw_hid_adapter` shield next to your keyboard's shield. For a split
   keyboard, add it to the central half only.

   ```yaml
   include:
     - board: nice_nano//zmk
       shield: reviung41 raw_hid_adapter
   ```

3. `config/<keyboard>.conf`:

   ```
   CONFIG_ZMK_AUTOSWITCH=y
   ```

4. Build and flash as usual.

5. **After flashing, remove the keyboard from each computer's Bluetooth settings and pair it
   again** (each on its own profile). The keyboard now has a second HID service, and computers
   cache the old service list.

Then open AutoSwitch ▸ Devices and add the keyboard (or reopen its settings): it should say
"Switches channel on request" with the number of profiles.

## Options

| Option | Default | Meaning |
|---|---|---|
| `CONFIG_ZMK_AUTOSWITCH` | n | Enable the module. |
| `CONFIG_ZMK_AUTOSWITCH_SELECT_DELAY_MS` | 100 | Time between replying to a profile switch and doing it, so the reply reaches the computer first. |

The raw HID interface's own options (`CONFIG_RAW_HID_USAGE_PAGE`, `CONFIG_RAW_HID_USAGE`,
`CONFIG_RAW_HID_REPORT_SIZE`) must stay at their defaults (0xFF60, 0x61, 32): AutoSwitch looks
for that interface.

## Protocol

Version 1, 32-byte reports, in `SPECIFICATION.md` §5a ("ZMK keyboards: the AutoSwitch firmware
module") at the root of this repository. The Windows implementation is
`windows/src/AutoSwitchKVM.Core/ZmkHostProtocol.cs` and
`windows/src/AutoSwitchKVM.App/Platform/ZmkHostSwitcher.cs`.

## Building it locally

`ci/build.sh` builds a test keyboard (nice!nano + Reviung41 + raw HID) with the module, in
ZMK's build image. CI runs it on every change (`.github/workflows/firmware.yml`).

```sh
docker run --rm -v zmk-ws:/ws -v "$PWD:/repo" -e WS=/ws zmkfirmware/zmk-build-arm:stable \
  bash /repo/firmware/zmk-autoswitch/ci/build.sh
```

The firmware lands in `firmware/out/` (git-ignored). Use `CONFIG=/path/to/your/zmk-config/config`
and `SHIELDS=...` to build your own keyboard instead.

## Notes

- Works over Bluetooth and over USB. AutoSwitch only uses it for Bluetooth keyboards.
- Other raw HID tools (e.g. qmk-hid-host) can share the interface: the module ignores reports
  that don't start with `AS`.
