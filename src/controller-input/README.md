# Experimental Touch Pro thumb-rest input

This independently authored prototype adds a two-dimensional thumb-rest input to
the existing Quest Pro controllers in SteamVR. It preserves their native driver,
poses, haptics, button paths and `oculus_touch` controller type. It does not add
hand tracking or trigger-slide input.

**Experimental; live checks are limited.** On firmware build
`51503870024400340`, a live Virtual Desktop check confirmed sensor input and
scrolling in SteamVR Dashboard and VRChat. In Swipe mode with **Both** selected
and sensitivity at **100%**, the wearer confirmed both thumbs scrolled well and
ordinary buttons worked; the driver reported no input errors. Stop disabled
output and restored neutral values without errors, orphaned reader workers or
ADB forwards; the wearer confirmed scrolling stopped and normal controls worked.
Desktop mouse movement also felt smoother to the wearer in the live check;
after Stop, disabled/neutral output and no remaining workers or forwards were
confirmed without input, mouse or restoration errors.
The adoption rule is limited to Virtual
Desktop controllers reporting a `quest_pro` render model and the exact native
`{oculus}/input/touch_profile.json` profile. Other firmware and Steam Link remain
unverified; offline tests do not establish their compatibility.

## Requirements and boundaries

- Rooted Quest Pro, Touch Pro controllers and SteamVR on Windows x64.
- Explicit experimental reader profile `legacy-51503870024400340`. The reader
  checks the exact incremental firmware build `51503870024400340`; other builds
  exit before mapping memory. Live checks cover one headset with this layout,
  not every headset or controller revision.
- Both named 4 KiB controller shared pages must be present in `trackingservice`.
  They are opened read-only through `/proc/<pid>/map_files`. The reader neither
  modifies these pages nor restarts or injects into a headset service.
- The PC relay needs no external server. It owns the ADB forwarding, foreground
  reader and this add-on's settings file. Binding setup separately manages only
  verified application selections, with recovery copies.

The server host interception uses the pinned public OpenVR driver ABI. It is
still an experimental hook: other add-ons that alter the same host method could
conflict. Cleanup compare-restores the slot only if it still points to Qpro's
method, and compare-restores controller profiles only if they are still Qpro's.
Install and uninstall with **SteamVR closed**. Hot unloading is unsupported;
restart SteamVR after removing the add-on.

## Reader CLI

```text
qpro-controller-input --profile legacy-51503870024400340 --check
qpro-controller-input --profile legacy-51503870024400340 --port 27063 --rate 60 --stop-file /data/local/tmp/<owned-session>.stop
```

`--check` reads for at most three seconds and needs a fresh, valid pair of sensor
samples. Keep both controllers awake. The continuous mode listens only on headset
loopback TCP, with rates from 40 to 90 Hz. The port is acquired before mapping,
so a duplicate reader cannot attach while that port is owned. No first client
within 15 seconds exits with code 8. A disconnect allows two seconds to reconnect,
then closes the socket and releases both mappings. SIGTERM, SIGINT and the owned
stop file also end the foreground reader; it attempts a bounded neutral packet.

Exit codes: 2 arguments/profile, 3 build mismatch, 4 root required, 5 source
unavailable, 6 invalid/stale samples, 7 port unavailable, 8 initial client timeout.

## Packet and settings contract

The relay forwards complete 64-byte `QPTP` v1 packets from TCP 27063 to Windows
loopback UDP **27064**. All fields are little endian:

| Offset | Field |
| --- | --- |
| 0 | `QPTP` magic |
| 4 / 6 | uint16 version 1 / uint16 length 64 |
| 8 / 16 | uint64 sequence / uint64 headset monotonic nanoseconds |
| 24 / 44 | Left / right: uint32 flags, float x, y, force, size |

Flags: bit 0 valid, bit 1 contact; no other bits. Position is -1 to 1, force is
0 to 1 and contact size is bounded from 0 to 512. Nonfinite values and invalid
flags are rejected. Inputs expire **200 ms after local arrival**, clear outputs
and release any mouse button. Sequence and source time must both increase within
an active lease. Once expired, a new reader/headset clock epoch may be accepted;
an exact duplicate cannot renew it. The loopback relay is a trust boundary, not
an authenticated or cryptographically replay-proof channel.

The add-on reads only its own `resources/settings.json` once per second. Missing
or invalid settings disable it. The shipped file is:

```json
{ "enabled": false, "mode": "trackpad" }
```

The supervisor should atomically write `enabled:true` only for an opted-in run
and write `enabled:false` in its cleanup. The installed driver registers extra
controls when it discovers eligible controllers, including while tracking is
off, so applications can load bindings against them at startup. Registration
does not enable output: Qpro values stay neutral unless tracking is enabled and
fresh input is available. If SteamVR caches the old profile or bindings, restart
SteamVR through Virtual Desktop. Disabling clears Qpro outputs, but the custom
profile may remain until driver cleanup or SteamVR restart.

Choose **Left**, **Right** or **Both** in the Hub for any mode. The corresponding
`side` setting is `left`, `right` or `both`; **Both** is the default. This selects
which thumb rests contribute extra input. An excluded controller sends neutral
extra values. The selection leaves optical finger routing and the physical
controllers' joystick, buttons, poses and haptics unchanged.

| Hub mode / `mode` value | Behavior on each selected thumb rest |
| --- | --- |
| Trackpad / `trackpad` | Smoothed absolute thumb position, contact, pressure and pressure click |
| Relative joystick / `joystick` | Displacement from the first touch position, with a small soft center deadband and the full -1 to 1 range; lifting the thumb resets the anchor |
| Swipe / `swipe` | Thumb movement velocity with a small noise allowance and a short glide after release; slow continued movement still accumulates, and pressure and click clear on release |
| Desktop mouse / `mouse` | Relative Windows pointer movement and pressure left-click; the extra SteamVR surface stays neutral |

In **Controllers**, choose **Thumb-rest mode > Swipe** and **Apply mode to >
Left (L), Right (R) or Both**. The **Swipe sensitivity** slider offers 25%–200%
in 25% steps. The Hub's 100% setting writes `swipeGain: 0.22`, matching the native
fallback default; lower values scroll more slowly and higher values faster.
Stop tracking, adjust, then start again. This
setting is a sensitivity preference, not sensor calibration, and does not repair
invalid or missing source data. Other modes retain their own response settings.

The first three modes all use the added trackpad components. They do not change
the physical joystick. Position smoothing and the Swipe velocity derivative
advance once per accepted source sample; repeated render frames do not invent
sensor motion. A 12 ms exponential filter then eases Swipe velocity in render
time, adding roughly 7–8 ms of measured lag. Swipe glide decays using elapsed
Windows time. Invalid or stale input still clears the filtered output immediately;
the filter does not bridge those gaps. An expired packet lease or a new reader
epoch clears the anchors and velocity. A source-clock reset or a gap of at least
200 ms releases contact before the next sample reanchors. Trackpad positions and
pressure click thresholds are unchanged.

Desktop mouse affects Windows and is off until explicitly selected. Each
selected thumb anchors independently on contact. With **Both**, their relative
movements are summed into one pointer movement. The shared left button is held
while either selected thumb has contact and crosses the pressure threshold;
one thumb releasing does not cancel the other thumb's held click. Touch release,
stale data, disable and cleanup release the button when no selected thumb remains
pressed. Controller deactivation also removes that controller's contribution;
retained packets cannot restore it until the controller is adopted again.
Fractional pointer movement accumulates between samples, so slow movement is not
discarded when it covers less than one screen pixel at a time.
Mouse position steps use a two-frame average followed by a 12 ms render-time
exponential filter to reduce source-rate bursts. Clicks, releases and invalid-data
guards remain immediate; Windows pointer settings are kept.
When the driver reads a mode or side change, it releases the old extra
input and mouse button before reanchoring the selected thumbs.

Optional settings: `side` (`both`, `left` or `right`), `smoothingMs` (0–250),
`clickOn` and `clickOff` (0–1, off below on), `joystickSpan` (0.1–2),
`swipeGain` (0.01–5), `glideMs` (0–2000), `mouseScale`
(0–4000), `ignoreRestingThumb` (boolean), `restingSize` (0–512), and
`intentionalForce` (0–1). Defaults and bounds are defined in `settings.hpp`.

## SteamVR application bindings

### Automatic menu scrolling

Controller component setup prepares additive SteamVR Dashboard and VRChat menu
bindings from the locally installed bindings. Existing movement, buttons,
gestures, poses and haptics are retained. The dedicated **Qpro Companion menu
scroll** input sends values only in **Swipe** mode while tracking is running;
other modes, stale input and Stop leave it neutral.

SteamVR binding formats and stored selections must be recognized before setup
can select a generated binding. If they cannot be verified, Activity explains
the manual selection needed and keeps the existing choice. Setup retains private
recovery copies; uninstall restores only selections that still refer to its
owned files and preserves later user choices. Actual scrolling still needs a
check after installation and any required SteamVR restart; a successful setup
message is not proof that the selected application consumes the added input.

### Other application actions

In SteamVR's controller bindings for VRChat or another application, edit your
personal binding and look for **Qpro thumb-rest surface (extra)** on either
controller. Left/Right/Both controls which of those sources sends values. Choose
the application's actions for the added surface, then save your personal binding.
The available actions come from the selected application;
Beyond the Dashboard and VRChat menu-scroll helpers, Qpro assigns no game actions.

| Extra surface component | What it provides |
| --- | --- |
| Position (`/input/trackpad/x`, `/input/trackpad/y`) | Two axes using the Hub's Trackpad, Relative joystick or Swipe mode |
| Touch (`/input/trackpad/touch`) | Thumb contact; Swipe mode also stays touched while its short glide remains active |
| Force (`/input/trackpad/force`) | Pressure from 0 to 1 while touched |
| Click (`/input/trackpad/click`) | Pressure crossing Qpro's configured press threshold |

The physical joystick is a separate source named **Physical joystick**. Native
thumb-rest contact remains **Native thumb-rest touch**. The profile keeps the
ordinary Touch controller identity, all native button/pose/haptic/skeleton paths,
and left/right button mirror mappings. Qpro's Trackpad, Relative joystick and
Swipe modes change the extra surface values; they do not remap the physical
joystick. Desktop mouse mode sends neutral values to the extra SteamVR surface.

The installed add-on changes an eligible existing controller's input profile at
runtime. After installing, restart SteamVR through Virtual Desktop; the added
controls should appear before Qpro tracking starts and remain neutral. Enable
thumb-rest input in the Hub for live values. Confirm ordinary controller inputs
and added actions in the intended application.

Profile sources, localization and editor image coordinates follow Valve's
[Input Profiles](https://github.com/ValveSoftware/openvr/wiki/Input-Profiles)
and [driver documentation](https://github.com/ValveSoftware/openvr/blob/master/docs/Driver_API_Documentation.md#device-inputs).

## Build and offline checks

Use a portable Zig 0.15.2 compiler and the pinned Valve header in
`third_party/openvr`. No compiler installation or system settings change is
required:

```powershell
& .\controller-input\build-native.ps1 -Zig 'C:\path\to\zig.exe'
```

The script compiles the static ARM64 Linux/musl reader, Windows x64 driver and six
Windows-only offline test programs. Tests cover packet validation, independent
sides, active-lease ordering, reboot epochs, neutral/release behavior, contact
hysteresis, all four modes at multiple reader/render rates, quantized and slow
movement, source-clock gaps, side changes, shared mouse button release, settings
parsing, dashboard output gating, double-buffer pages and abandonment deadlines.
It does not execute the reader, load the driver in SteamVR, or install anything.
Those offline checks do not replace device testing on the intended firmware.

OpenVR is distributed under its included license. No QFTPlus code is included;
that project's published controller interface informed the layout investigation.
Qpro's surrounding repository license applies to these independently authored
source files.
