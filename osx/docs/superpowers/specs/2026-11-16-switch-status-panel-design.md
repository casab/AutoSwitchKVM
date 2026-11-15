# macOS switch status panel (design)

2026-11-16. Approved by the user in conversation. Mirrors Windows' `StatusPopup` (9db4ebc).

## Why

The user wants a short, temporary "notification-like" view of devices connecting when the KVM
switches. On Windows, one toast and one sound per device per switch was too much, so Windows
replaced them with a small panel by the tray. On the Mac, device events are macOS notifications
(`showNotifications`), which the user has off, so a switch shows nothing today.

## Goals

- While devices switch, show where the KVM went and each changing device's live state.
- Hide by itself once things settle. No sound, nothing in Notification Center, never take focus.
- Same setting and behavior as Windows, so the two apps feel alike.

## Non-goals / anti-goals

- Not a notification history and not interactive controls (one click target only).
- Must never steal focus or keyboard input (the user is typing right after a switch).
- Must not show on app launch or for devices that didn't change.

## Approaches

1. **Floating non-activating `NSPanel` hosting a SwiftUI view, at the top-right under the menu
   bar** (chosen). Native look (material background), never key, joins all Spaces.
2. Keep `UNUserNotification` banners, one per switch. Rejected: banners pile up in
   Notification Center, can't update live, and the user has them off.
3. Show it inside the `MenuBarExtra` window. Rejected: that opens only on a click and takes focus.

## Design

- **Window:** `NSPanel`, `.nonactivatingPanel` + borderless, `level = .statusBar`,
  `collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]`,
  `hidesOnDeactivate = false`, `ignoresMouseEvents = false`, never becomes key or main. ~320 pt
  wide, rounded material background (`.regularMaterial` or `NSVisualEffectView` `.hudWindow`).
  Placed at the top-right of the screen with the menu bar (`NSScreen.main.visibleFrame`), 8 pt
  in. Fades in/out.
- **Content:** a header with where the KVM is ("KVM → this Mac" / "KVM → the other computer"),
  then one row per device whose status changed in this burst: symbol (trackpad / keyboard /
  speaker by handoff type or name), name, state text: Connecting…, Connected ✓, Sent to the
  other computer, Released, Disconnected, or a problem (short reason). A device in slow mode
  (asleep) reads "Asleep: wake it to connect", calm, not an error.
- **Which changes count** (as Windows): a device whose status kind changes, except into Idle
  and except Idle → a resting state directly (launch). Rows update live. A new burst (KVM moved
  again) clears old rows.
- **Hiding:** 3.5 s after the last change once nothing is connecting; 8 s if a row shows a
  problem; never while the pointer is over it. A click opens Settings and hides it.
- **Setting:** reuse `showNotifications` (same key as Windows; label "Switch status",
  description as Windows'). `notifyUnexpectedDisconnect` shows a drop in the panel even when the
  panel is otherwise off. Device `UNUserNotification`s go away (`onNotice` no longer posts them).
  Update notices stay with Sparkle.
- **Not while** the menu bar panel is open (it already shows the same rows).

## Left to the builder

- How the engine exposes the needed state: statuses are already `@Published`; "asleep" needs
  the engine to expose slow-mode devices (`slowDevices`) and "sent to the other computer" vs
  "released" needs the last release result per device.
- Exact symbols and colors; animation details.
- A debug/preview hook to show the panel with demo rows (Windows has `--popup`).
