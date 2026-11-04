# Experimental Virtual Desktop hands and controllers

This backend is independently authored. It uses temporary runtime adapters to
send optical fingers through Virtual Desktop while retaining physical controller
poses and buttons. It does not modify gaze, install an APK, or patch installed
Virtual Desktop files. It is a prototype; live operation is not yet validated.

## Requirements

- Rooted ARM64 Quest Pro with hand tracking enabled.
- Singularity **Simultaneous Hands & Controllers** enabled and its separate
  Frida Server turned off.
- Virtual Desktop with an admitted PC layout. Android **1.34.22.0** is the
  researched reference; different app version labels may attempt guarded
  startup. Reviewed PC full hashes remain evidence, including the inspected
  **1.34.23** beta and exact **1.34.25** rebuild. An unfamiliar full hash must
  match the conservative approved PE code/layout-family fingerprint. Startup
  still validates the loaded image/native interfaces before fixed-offset hooks
  and the Quest's actual Mono hand-result layout before adapter activation.
  None of these prechecks proves a new PC/headset pair works live.
- SteamVR running through Virtual Desktop. Steam Link is unsupported.
- Hand components installed separately: Python Frida **17.18.0** and its matching
  Android ARM64 server. Tracking never downloads or installs dependencies.

## CLI

```text
python controller.py --target SERIAL --adb PATH --check
python controller.py --target SERIAL --adb PATH --frida-server PATH --stop-file PATH --parent-stdin
```

`--check` reads package versions, driver hash and headset preferences. It never
starts a helper, forwards a port, attaches to a process or loads an adapter.
An unfamiliar Android version is reported for guarded startup, not marked as
runtime-validated. A PC family match is preflight evidence only; live validation
still occurs when tracking starts. Green prechecks do not establish live input.
It can run without Frida; `componentsReady` then reports whether setup is needed.
It does request root for preference reads, so allow the existing Magisk prompt.
All preferences are read for the currently active headset user. Recognized
inactive hand/multimodal boolean values are warnings before VD activation;
simultaneous mode must be `1`, and missing, duplicate or unsupported readings
remain errors. The temporary adapters must still establish real same-side
optical/skeletal readiness after activation.

The tracking command uses the selected ADB target. It refuses an occupied headset
helper port and known existing Frida servers. It owns only its helper PID,
`/data/local/tmp/qpro-hands-frida`, headset loopback port **27062**, an automatically
allocated PC port, and the exact forward it creates. It does not terminate
another server or remove another
forward. The stop file may be created by the Hub; without it, stdin EOF requests
stop. `--parent-stdin` also watches parent EOF when a stop file is supplied, so
a parent crash or failed stop-file write still requests restoration. A stop file
already present is refused before any headset command.

## Status and recovery

- `HANDS_CHECK`: compatibility and component details.
- `HANDS_TRANSPORT`: allocated PC port and headset helper port.
- `HANDS_STAGE`: validation or optical-hand readiness progress.
- `HANDS_READY`: fresh valid optical data has reached at least one held physical
  controller's skeleton. `activeSides` identifies which side(s).
- `HANDS_STATUS`: adapter states, frame and skeleton counters, native callback
  results and per-side reasons a route is not ready.
- `HANDS_STOP_CAUSE`: original failure and the last adapter states before cleanup.
- `HANDS_DETACHED`: process identity and the session-disconnection reason.
- `HANDS_CLEANUP`: restoration confirmation; `HANDS_CLEANUP_FAILED` is a failure.

Adapters use 20-second leases renewed every three seconds. Stop restores saved
managed settings, skeleton handles and flags, removes hooks, stops the owned
helper and removes the owned forward. Failed restoration returns exit **5**;
restart headset Virtual Desktop and SteamVR before retrying. Rejected layouts
return **4**, ordinary failures return **1**, and an acknowledged stop returns
**0**. A stalled supervisor returns a cleanup failure while adapter leases expire.

The PC adapter probes the public OpenVR skeletal interface and checks roles,
pointer access, 31-bone input and the admitted ABI. Its driver object layout and
the Android query are private compatibility facts, so future Virtual Desktop or
firmware versions require fresh validation. Body tracking interaction needs a
live check; this prototype makes no support claim for simultaneous body tracking.

## Guarded version compatibility

Version strings are not a blanket allowlist. An unfamiliar PC full hash can
proceed only when the conservative PE code/layout-family fingerprint matches
an approved layout. The loaded module and native interfaces are then validated
before fixed-RVA hooks are installed. A changed instruction or layout family
still needs review; a marketing version number cannot override that failure.

An unfamiliar Android version may attempt startup only through the existing
validation path. The adapter must prove the actual Mono hand-result fields,
types, sizes and layout before using its result offsets. Native query callers,
controller roles, bone data, freshness, leases and confirmed cleanup remain
required. Frida/components/settings errors retain their existing failure rules.
No arbitrary driver or APK is accepted, and the manual read-only check does not
claim these runtime checks passed. See `COMPATIBILITY_FACTS.md` for historical
inspections and the current policy boundary.

Skeletal updates are observed without replacing the native function or retrying
native calls. Virtual Desktop sends optical bones through its saved original
callable, so the exact driver profile also identifies and validates that callable.
Only the saved original is observed; the public entry is checked in preflight.
Only successful, valid 31-bone updates through the saved original for an enabled
side establish readiness; a suppressed public call can also return success.
Each skeleton is copied once and all 248 float values are checked. Handle and
device matching precede object checks so unrelated updates avoid those queries.
The duplicate hand-device pose uses a retained copy of the supplied const pose;
the driver's original buffer is untouched. Callback errors schedule restoration
outside the callback.

Native routing validation follows returning ARM64 calls only when the side flag
is held in an AAPCS64 callee-saved register. It stops when that register or the
stack pointer is overwritten, and still requires one uniquely validated query
caller, two-sided result copies and four distinct held/side routing choices.
Test/compare aliases have read-only integer operands; their condition-code writes
cannot be treated as changes to the side flag.

## Offline checks

```text
python tests/test_controller.py
node tests/test_adapters.js
```

These use fake ADB/Frida and isolated JavaScript guards. They do not establish
live tracking or firmware compatibility. Before distribution, test both and
mixed controller/empty-hand use, optical loss, sleep/reconnect, VD restart,
SteamVR exit and Hub termination, including confirmed restoration.

Public skeletal interface documentation:
https://github.com/ValveSoftware/openvr/wiki/Creating-a-Skeletal-Input-Driver
https://github.com/ValveSoftware/openvr/wiki/Hand-Skeleton

Upstream Frida's license and source notice must accompany a bundled server/wheel.
The project does not distribute Virtual Desktop's proprietary driver.
