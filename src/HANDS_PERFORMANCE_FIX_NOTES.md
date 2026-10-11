# V2.1.2 hands performance fix test

## V3.0 routing optimisation

The PC route poll still runs every 16 milliseconds. Optical bone updates,
finite-value validation and the per-call native identity checks keep their
existing behaviour. The optimisation removes repeated work from that poll:

- Check the loaded Virtual Desktop driver identity once per poll, then read
  each side's current controller/hand identity.
- Read a six-value native fault/freshness summary during routing. Full callback
  counters and the result histogram are read only for RPC diagnostics.
- Publish the native side state only when its identities, handle, physical
  state or active state change.
- Read actual routing fields before writing them. Unchanged fields avoid a
  write, while fields overwritten by Virtual Desktop are repaired on the next
  poll. Restoration still reads back and verifies all original fields.
- Include route work, elapsed time and per-second callback/update rates in
  `HANDS_STATUS`. These measurements distinguish route-poll work from process
  CPU usage and VR frame time; unavailable counters are not reported as zero.

An isolated Frida 17.18.0 child-process fixture measured four blocks of 5,000
steady route polls. Mean process CPU time for that work fell from 472.66 to
367.19 milliseconds (about 22%). Mean elapsed time fell from 569.50 to 455.25
milliseconds. Driver-module queries fell from 15,000 to 5,000 per block;
unchanged native side publications fell from 10,000 to zero; native values
read into JavaScript fell from 225,000 to 30,000. These are measurements of a
synthetic route-poll workload on one PC, not whole-app CPU or game FPS gains.

Offline verification passed 41 controller lifecycle/counter tests, 22 Android
adapter boundary tests and the PC route/ABI tests. A real CModule fixture also
passed complete bone finite checks, unchanged original results, const-pose
preservation, object identity refusal, nested calls, Stop during an original
pose call, fresh and expired skeleton summaries, overwritten routing fields,
optical loss/recovery and confirmed restoration/unload.

The feature remains off by default and requires the admitted Virtual Desktop
builds. No installed component or headset process was changed for these
checks. Sustained VR frame time, actual finger accuracy and coexistence with
camera tracking still need a live session before claiming the reported game
lag or SteamVR crash has been resolved.

This build follows a short run where both hands routed correctly and Stop
restored the adapters, followed by a report of severe game lag with only
**Experimental hands + controllers** enabled. The lag cleared after Stop.
This remains an experimental test build.

The later startup-check test revises the preference rule in step 3 below; see
**HANDS_PREFERENCE_FIX_NOTES.md**. The performance changes remain in that build.

## Changes

- **Bone validation:** Read each 31-bone skeleton in one 992-byte copy instead
  of 248 separate float reads. All 248 values are still checked for NaN and
  infinity before the update counts toward readiness.
- **Skeletal observation:** Observe only Virtual Desktop's validated saved
  original callable. The extra public-entry observer was diagnostic and did
  not establish optical readiness.
- **Other devices:** Match the skeleton handle or hand-device ID before checking
  driver objects. Unrelated device updates avoid module and memory-range queries.
- **Compatibility messages:** Preserve the structured compatibility result and
  explain startup refusal instead of showing only a generic Python exit code.
- **Previous fixes retained:** Automatic ADB port allocation, validated ARM64
  routing, transparent skeleton calls, copied poses and confirmed restoration.

## Verification and limits

Offline checks cover the bulk read and complete finite-value scan, malformed
input refusal, unchanged native arguments/results, unrelated-device fast paths,
readiness, restoration and failed compatibility output. No live hooks or
installed components were changed while preparing this build.

The preceding short live run accepted 114 optical skeleton updates per side
and acknowledged cleanup after Stop. It does not establish sustained stability
or game performance. The performance changes in this ZIP still need a live
check. If lag remains, native callback overhead needs further measurement.

A subsequent startup was refused because both headset hand settings were off,
despite installed components being ready. The logs do not establish why the
settings changed. That refused attempt never activated the adapters and cannot
measure performance.

## Trying this build

1. Extract the ZIP into a new folder. Use the existing hand/controller
   components; reinstalling them does not enable the headset's hand settings.
2. Enable hand tracking in Quest Settings and **Simultaneous Hands & Controllers**
   in Singularity. Turn off Singularity's separate Frida Server.
3. Reopen SteamVR through Virtual Desktop and press
   **Check hand/controller compatibility**. Both `hand_tracking_enabled` and
   `multimodal_hands_and_controllers_enabled` must be `true`. A check with either
   setting off refuses activation before hooks are installed.
4. Enable only **Experimental hands + controllers** and press **Start tracking**.
   Check real finger movement, controller poses/buttons and game frame time.
5. Press **Stop tracking** and wait for confirmed cleanup. Send the complete
   Activity log if there is lag or a startup failure.

The controller add-on supplies optional thumb-rest input. Uninstalling it does
not remove the hands runtime components or fix headset hand-tracking settings.

If restoration is not confirmed, keep the Hub open until its workers exit,
then restart headset Virtual Desktop and SteamVR before retrying. See
**CONTROLLER_INPUT.md** for compatibility and recovery limits.

The ZIP excludes private recordings, diagnostic probes, extracted libraries,
personal settings, installed Python environments and compiler caches.
