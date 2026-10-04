# V2.1.2 hands runtime fix test

This test follows a run where real finger movement was reported, but Qpro's
skeletal readiness counter stayed at zero and SteamVR crashed around the
45-second readiness timeout. The crash destroyed the PC adapter before it
could acknowledge restoration. This remains an experimental test build.

## Changes

- **Skeletal updates:** Observe the existing SteamVR update call without
  replacing it, suppressing calls by interface-owner identity or retrying a
  failed native call. Also observe Virtual Desktop's validated saved original
  callable: its optical generator bypasses the public entry, which caused the
  old counter to miss real finger updates and trigger the readiness timeout.
- **Pose handling:** Adjust a private copy of the hand pose while an optical
  route is active. Leave the driver's original pose buffer intact.
- **Callback failures:** Schedule restoration outside the native callback.
- **Readiness diagnostics:** Report native calls and their results separately
  from accepted optical submissions, with the per-side routing conditions.
- **Failure reporting:** Keep the original startup/readiness error visible even
  if cleanup also fails. Record the disconnected process and Frida's reason.
- **Previous fixes retained:** Automatic ADB port allocation and validated ARM64
  returning-call traversal remain in place.

## Verification and limits

Offline checks cover native-call observation, copied poses, error handling,
readiness, session detachment and restoration reporting. They do not establish
that this change resolves the reported SteamVR crash on hardware. The previous
run is evidence of reported finger movement, not a completed stability check.

The compatibility limits in **CONTROLLER_INPUT.md** still apply. Test both
controllers, their buttons and haptics, mixed hand/controller use, optical loss,
reconnect and Stop restoration before release. Gaze compatibility retains its
separate limits in **GAZE_ENGINE_TEST_NOTES.md**.

## Trying this build

After the failed run, restart Virtual Desktop on the headset and SteamVR.
Extract this ZIP into a new folder and use the already installed optional
components. Choose Virtual Desktop, enable **Experimental hands + controllers**
and press **Start tracking**.

Keep your fingers visible while holding the controllers. Check that real finger
movement, controller poses and buttons work together. Activity should report
`HANDS_READY`; if it does not, send the new `HANDS_STATUS`, `HANDS_STOP_CAUSE` and
`HANDS_DETACHED` lines. Press **Stop tracking** and wait for cleanup.

`confirmed: false` still means restoration was not acknowledged. Restart
headset Virtual Desktop and SteamVR before retrying; a destroyed adapter is
not treated as proof that cleanup succeeded.

The ZIP excludes private recordings, diagnostic probes, extracted libraries,
personal settings, installed Python environments and compiler caches.
