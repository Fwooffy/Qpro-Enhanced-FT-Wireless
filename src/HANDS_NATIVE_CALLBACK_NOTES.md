# V2.1.2 hands native callback test

The previous startup-check build produced real finger movement, but the live
test reported high CPU use and a game frame rate drop of about 30 FPS. This
revision moves frequent interception work out of JavaScript. It is a test build;
the amount of improvement needs to be measured in the same game and scene.

## Changes

- **SteamVR callbacks:** Skeleton validation and controller-pose filtering run
  in native CModule callbacks. Unrelated devices are rejected before expensive
  hand checks. All 31 bones still receive complete finite-value validation.
- **Pose ownership:** Each redirected pose uses its own retained copy. The
  source pose is unchanged, and nested calls cannot overwrite another call's
  copy. Original skeleton arguments and return codes pass through unchanged.
- **Headset callbacks:** Controller-query filtering runs natively. The managed
  Update callback enters JavaScript only for activation and restoration. Finger
  conversion keeps the existing managed-call error handling.
- **Stop and errors:** New mutable work is disabled before cleanup. Accepted
  callbacks drain before restoration. Native code, state and callback references
  remain retained until Frida unloads the script. A drain or restoration failure
  remains an explicit cleanup error.
- **Startup checks retained:** Known inactive hand preference readings remain
  warnings. Simultaneous mode, exact runtime identities, validated ABIs and fresh
  same-side optical/skeleton output remain required.

## Verification and limits

Offline adapter tests and an isolated native harness check the callback paths,
argument preservation and cleanup. The harness uses a disposable process owned
by the test; it does not attach to SteamVR or change the headset.

In one isolated comparison of 10,000 calls, matched skeleton callbacks took
374 ms in the previous implementation and 22 ms in this revision. Matched pose
callbacks took 144 ms and 9 ms. Both retained their validation checks. The
portable headset callback cores also compiled and ran in the isolated Windows
process; their actual Android/Mono execution still needs a headset check.

Native harness timings measure callback overhead only. They are not a game FPS
measurement, and they do not establish sustained stability on the headset.
Steam Link hands and body-tracker interactions remain unverified. The feature
stays optional and off by default.

## Trying this build

1. Extract the ZIP into a new folder. Existing hand/controller components can
   be reused; no component reinstall is needed for this source-only revision.
2. Keep Quest hand tracking and Singularity's **Simultaneous Hands & Controllers**
   on, and its separate Frida Server off. Launch SteamVR through Virtual Desktop.
3. Enable only **Experimental hands + controllers** and press **Start tracking**.
   Confirm `HANDS_READY`, real finger movement, controller poses and buttons.
4. Compare frame rate and frame time with Qpro stopped in the same scene.
5. Press **Stop tracking** and wait for confirmed cleanup. If lag or a cleanup
   error remains, send the complete Activity log.

This ZIP excludes private recordings, probes, extracted libraries, installed
environments, personal settings and compiler caches. See **CONTROLLER_INPUT.md**
for the compatibility and recovery limits.
