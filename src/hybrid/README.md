# Experimental Virtual Desktop hands and controllers

This backend is independently authored. It uses temporary runtime adapters to
send optical fingers through Virtual Desktop while retaining physical controller
poses and buttons. It does not modify gaze, install an APK, or patch installed
Virtual Desktop files. It is a prototype; live operation is not yet validated.

## Requirements

- Rooted ARM64 Quest Pro with hand tracking enabled.
- Singularity **Simultaneous Hands & Controllers** enabled and its separate
  Frida Server turned off.
- Virtual Desktop Android **1.34.22.0** and one of the exact, independently
  inspected Streamer driver fingerprints in `compatibility.json`.
  The signed Streamer **1.34.23** PC driver shares the checked PC layout and is
  admitted independently. Android **1.34.23.0** still needs its own hand-layout
  evidence and is not admitted. The new PC/headset pair has not been live tested.
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
restart headset Virtual Desktop and SteamVR before retrying. Unknown builds
return **4**, ordinary failures return **1**, and an acknowledged stop returns
**0**. A stalled supervisor returns a cleanup failure while adapter leases expire.

The PC adapter probes the public OpenVR skeletal interface and checks roles,
pointer access, 31-bone input and the admitted ABI. Its driver object layout and
the Android query are private compatibility facts, so future Virtual Desktop or
firmware versions require fresh validation. Body tracking interaction needs a
live check; this prototype makes no support claim for simultaneous body tracking.

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
