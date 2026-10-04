# V2.1.2 hands and controllers test

This ZIP adds an independently authored experimental Virtual Desktop
hands/controller backend and Touch Pro thumb-rest input. Both are off by
default. It is a **test build**, not a validated hardware release.

## Changes

- Added **Optional hands and controllers** to First-time setup, with component
  installation and controller add-on removal. Close SteamVR before either
  action. Optional Frida dependencies are downloaded into Qpro's separate
  environment and checked against pinned official SHA-256 values.
- Added **Hands and controllers** to Live tracking. The optical finger route
  keeps physical controller poses/buttons; the separate thumb-rest route adds
  SteamVR trackpad inputs with Trackpad, Relative joystick, Swipe and explicitly
  selected Desktop mouse modes.
- Added exact version/fingerprint checks, fresh-input readiness reporting,
  renewable adapter leases, parent-EOF and stop-file supervision, stale-packet
  neutralization, and cleanup confirmation.
- Hand startup now asks ADB for a free PC port, preserving other listeners and
  forwards. Cleanup uses each worker's explicit restoration result, drains its
  final Activity lines and keeps startup errors separate from cleanup failures.
- Kept the Hub's existing visual style. Grouped wireless pairing and standalone
  cheek-camera training into expandable sections, aligned the new fields and
  improved disabled toggle readability.
- Grouped Live tracking into **Eyes** (gaze, pupils and eyebrows), **Lower-face
  tracking** (tongue and cheek controls), and **Hands and controllers** at the
  bottom. Cheek response and calibration controls are directly visible.
- Updated the bundled PDF and text instructions to match the visible controls.
- Added the 30 supplied dot-free gaze firmware IDs to the preparation helper's
  approval catalog. Diagnostics report firmware approval separately from engine
  support. The gaze compatibility test now has exact size/hash profiles for
  those engines; see **GAZE_ENGINE_TEST_NOTES.md**. This does not expand the
  controller firmware profile.

## Compatibility and test limits

The first hand profile admits Virtual Desktop headset **1.34.22.0** and the
exact PC Streamer driver fingerprint documented in **Docs/CONTROLLER_INPUT.md**.
Enable headset hand tracking and Singularity's **Simultaneous Hands &
Controllers** switch; turn off a separate Frida Server first. The thumb-rest
reader admits firmware build **51503870024400340**. Steam Link controller input
is not admitted by this prototype. Unknown builds are refused.

Builds and offline checks passed: 16 fake hand lifecycle checks, synthetic
adapter ABI/skeleton/restore checks, 45 native packet/contact/settings checks,
11 fake-page/lifetime checks, 18 optional-component checks, and a functional
Windows PowerShell/native-child EOF cleanup check. There were 2,782 private UI
layout/state checks across narrow, standard and wide Windows fixtures.
The startup fix also has functional Python-child output/EOF checks; see
**HANDS_STARTUP_FIX_NOTES.md** in the source repository.

These checks do not establish live headset behaviour. Before a release, verify
both hands while holding controllers, mixed controller/empty-hand use, optical
loss, controller reconnect, haptics/buttons, app input bindings, sleep/reconnect,
and confirmed restoration after Stop or a process exit. Body-tracking
interaction and high-DPI physical displays still need validation.

No live module/add-on was installed during this build. The ZIP contains no raw
camera recordings, personal profiles, saved headset addresses, local Python
environments, compiler caches, debug symbols or reference repository copy.

## Recovery

Press **Stop tracking** and wait for Activity's cleanup result. If restoration
was not confirmed, keep the Hub open until its workers exit, then restart
headset Virtual Desktop and SteamVR before retrying. To remove the thumb-rest
add-on, close SteamVR, use **Uninstall controller add-on**, and reopen SteamVR to
reload the normal controller profile. Installation/uninstallation keeps recovery
copies in Qpro's own local storage.
