# V2.1.2 hands startup check fix test

This build corrects a startup refusal when the headset's hand switches were on,
but its preference readings reported hand tracking and multimodal hands as
inactive. Reapplying both values over USB verified them as enabled briefly;
later reads reported them inactive again. No tracking adapters were activated
during that diagnostic. The readings are not treated as proof that a menu
switch is off.

## Changes

- **Active headset user:** Read all three hand preferences in one
  `oculuspreferences --getc` request. The old check mixed calling-user and
  active-user reads. Activity identifies the preference source explicitly.
- **Startup state:** Recognized inactive values for `hand_tracking_enabled`
  and `multimodal_hands_and_controllers_enabled` produce warnings. They no
  longer prevent the Virtual Desktop adapter from requesting its temporary
  multimodal mode.
- **Configuration errors:** Simultaneous mode must still be `1`. Missing,
  duplicate or unsupported preference values remain startup errors.
- **Live readiness:** Exact version/fingerprint checks, all three adapter
  validations, fresh same-side optical fingers and accepted SteamVR skeleton
  updates are still required. A warning is not proof that hands are working.
- **Performance fixes retained:** One bulk skeleton read, complete finite-value
  validation, one saved-original observer and early unrelated-device matching.

## Verification and limits

Offline checks cover active-user reads, known inactive states, invalid or
ambiguous values, strict readiness and cleanup. Read-only headset checks verify
the preference readings and the revised admission result. Sustained game
performance and functional hand routing in this new build still need testing.

The check does not enable system settings, start a Frida helper or activate an
adapter. Start requests temporary Virtual Desktop multimodal mode; Stop retains
the existing restoration and cleanup rules. If optical input does not become
ready, startup times out and restores the adapters.

## Trying this build

1. Extract the ZIP into a new folder. Existing hand/controller components can
   be reused.
2. Keep Quest hand tracking and Singularity's **Simultaneous Hands & Controllers**
   switched on, with Singularity's separate Frida Server off.
3. Reopen SteamVR through Virtual Desktop. Check compatibility, then enable only
   **Experimental hands + controllers** and press **Start tracking**.
4. A warning about an inactive boolean can appear before startup. Check for
   `HANDS_READY` and real finger movement while controller poses and buttons work.
5. Check game frame time, press **Stop tracking**, and wait for confirmed cleanup.
   Send the complete Activity log if input does not become ready or lag remains.

See **CONTROLLER_INPUT.md** for compatibility and recovery limits. This is an
experimental test ZIP; it excludes private recordings, probes, extracted
libraries, personal settings, installed environments and compiler caches.
