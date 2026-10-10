# V3.0.0 gaze engine compatibility test

This ZIP adds native gaze profiles for the 30 supplied Quest Pro firmware
builds. It is a **test build**: the additional profiles have been checked
against firmware binaries, but their live tracking and restoration still need
headset testing.

## Changes

- **Automatic engine selection:** Prepare gaze and live tracking use one shared
  catalog of 28 exact engine identities covering all 30 build IDs. Two pairs of
  builds contain identical engines. Selection requires both file size and the
  complete SHA-256; a firmware ID or matching file size alone cannot enable a
  hook. Qpro reads the engine and stock eye model from the connected headset.
  Users do not need to supply an engine file.
- **Previously rejected V2.4 engine:** Added the reviewed profile for build
  `51412760034600340`, engine size `44,198,016` and SHA-256
  `96fdebc377b475df55d59f7added04c5014c4069aa1d636cf3d6f15d8fe27f1e`.
  This addresses that exact unsupported-engine preparation error; it does not
  admit every engine described as Horizon OS V2.4.
- **Relocated gaze layouts:** Added the successful-output hook locations for
  the older engines and the relocated 207 engine. Each profile retains the
  inspected register ownership and vector layout for that specific binary.
- **Detector output ownership:** On newer epilogue layouts, an output must match
  a valid entry on the same thread, eye tag and EyeData pointer. Missing, stale,
  nested or invalid events cannot publish an old vector as a new eye sample.
  Gaze angles and convergence smoothing are unchanged.
- **Activity diagnostics:** Preparation reports whether a profile is an
  existing reference or newly checked firmware layout. Tracking reports the
  selected profile and whether its detector ownership guard is active.
- **Reader cleanup:** Failed root-shell startup and trace cleanup still close
  Qpro's shell. Lost events and faulted vector fetches discard pending eye
  pairs instead of combining them with a later sample.
- **Complete packaging:** Included the shared engine catalog and ownership
  helper in the runtime and source build lists.

## Verification and limits

All 30 build IDs have binary-derived size/hash/layout checks. The additional
firmware extractions passed compressed-operation and reconstructed ODM hashes,
and their experimental eye-model graph patch checks passed. These checks cover
the successful gaze detector path, its caller and required math dependencies;
they do not establish physical eye-tag behaviour or complete engine equivalence.
OTA signatures were not authenticated.

Offline tests cover automatic selection, all 30 preparation/manifest
round trips, unknown-engine refusal, stale-call rejection, valid vector
preservation and reader cleanup. The exact previously reported engine now
passes the preparation identity check. Two catalog entries retain existing
reference layouts; the new ownership guard itself also needs a live check.

Before release, check left/right gaze, near/far convergence, blinks, temporary
headset tracking restart, Stop tracking and interrupted-session recovery on
the added firmware families. Unknown engine hashes remain refused. No headset
installation or firmware change was performed while creating this ZIP.

## Trying this build

Extract it into a new folder and follow the included PDF or
**Docs/RELEASE_INSTRUCTIONS.md**. Use **Check gaze setup**, then **Prepare gaze**
when the popup recommends the Hub method. Select **Independent Eye Gaze** on
**Live tracking** and press **Start tracking**. The headset can temporarily
freeze while its tracking service restarts. Press **Stop tracking** and wait
for Activity to confirm recovery.

**Use one independent gaze method at a time.** While an independent-gaze Magisk
module is active, leave **Independent Eye Gaze** unchecked in the Hub.
**Stop tracking** only reverses the Hub's changes; it does not disable a Magisk
module. **Recover Qpro gaze** handles a verified interrupted Hub session;
**Reset legacy gaze** requires confirmation and refuses conflicting overlays.

The ZIP also retains the earlier optional hands/controllers prototype, off by
default. Its separate compatibility limits are described in
**Docs/CONTROLLER_INPUT.md**. The gaze catalog does not expand controller
firmware support.

The package contains no extracted firmware engines, stock or patched headset
eye models, camera recordings, personal profiles, headset addresses, installed
Python environments or research caches.
