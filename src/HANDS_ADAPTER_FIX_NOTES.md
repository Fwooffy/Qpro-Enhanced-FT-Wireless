# V2.1.2 hands adapter fix test

This test build fixes the native controller validation error
`Controller ABI is ambiguous or unsupported (0 validated callers)` seen after
the hand transport connected successfully. The hands/controller prototype
remains experimental.

## Changes

- **Returning native calls:** The resolver follows the return continuation of
  direct and indirect ARM64 calls when the side flag is held in a callee-saved
  register. Previously, it treated an indirect call as the end of the path and
  missed the valid downstream controller routing.
- **Register ownership:** Paths stop if the side register or stack pointer is
  overwritten. Missing register-access information is refused. Read-only
  test/compare aliases are handled despite inconsistent decoder write metadata.
- **Validation retained:** The query ABI, caller result copies, four distinct
  held/side routing choices and uniquely resolved caller remain required.
  No fixed firmware address or reference implementation was added.
- **Earlier startup fixes retained:** ADB allocates a free PC port, preserves
  other forwards and reports worker cleanup separately from startup failures.

## Verification and limits

The failure was reproduced offline from the connected Quest Pro's controller
library. The corrected resolver finds one validated caller in that library.
Regression fixtures cover returning calls, caller-saved flags, overwritten
registers/stack pointers, missing metadata and invalid side branches.

A temporary validation-only probe passed all three adapters on the connected
headset and PC: controller selection, optical-hand metadata and SteamVR's
31-bone skeletal interface. All adapters remained idle, activation was blocked
and cleanup was confirmed. No installed components were replaced.

These checks do not prove live finger routing, its conversion ABI or behaviour
under optical loss. Test both controllers, mixed hand/controller use, buttons,
haptics, reconnect and confirmed Stop restoration before release. The version
limits in **CONTROLLER_INPUT.md** still apply. Additional gaze profiles retain
their separate live-test limits in **GAZE_ENGINE_TEST_NOTES.md**.

## Trying this build

Extract the ZIP into a new folder. Use the existing installed hand/controller
components, select Virtual Desktop and enable **Experimental hands + controllers**.
Press **Start tracking** while wearing the headset and holding your controllers.

Activity should pass all three `HANDS_STAGE` validation records, then report
`HANDS_READY` when fresh optical fingers reach at least one controller. Check
finger movement alongside controller poses and buttons. Press **Stop tracking**
and wait for `CONTROLLER_CLEANUP` with `confirmed: true`.

The package contains no proprietary controller libraries, extracted firmware,
recordings, personal profiles, installed Python environments or research probes.
