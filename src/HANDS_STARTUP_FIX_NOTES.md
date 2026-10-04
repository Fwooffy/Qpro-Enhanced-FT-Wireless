# V2.1.2 hands startup fix test

This test build addresses the hand/controller startup error
`cannot bind to 127.0.0.1:27062 (10048)` and the misleading cleanup warning
that followed it. The optional hands and controller features remain experimental.

## Changes

- **Free PC port:** ADB reserves an available local port for the hand connection.
  The headset helper continues to use port 27062. Other applications and existing
  ADB forwards can keep their PC ports.
- **Owned connection cleanup:** Qpro records the allocated port and removes its
  forward only when the selected headset, local port and remote port still match.
  An ambiguous allocation cannot be reported as confirmed cleanup.
- **Accurate cleanup results:** The parent forwards worker output live and waits
  for the final cleanup record before reporting restoration. A startup failure
  with confirmed cleanup remains a startup failure. Missing, malformed or failed
  restoration evidence still requires attention.
- **Preflight failures:** The hands worker explicitly reports when it stopped
  before starting any adapters. Compatibility checks do not enable tracking.
- **Worker ownership:** An output-reader startup failure still closes and waits
  for its registered child; it cannot leave a worker behind or claim restoration
  without proof.

## Verification and limits

Focused offline regressions cover occupied PC ports, valid and ambiguous ADB
allocation, foreign forwards, helper restoration, delayed output, startup errors,
Stop races and parent-EOF cleanup. Functional Windows subprocess checks verify
that startup errors remain visible, final output is drained and worker pipes close.
An ADB transport check also passed while SteamVR kept PC port 27062: Qpro
received a different port, removed its temporary forward and left the original
forward list and SteamVR listener intact. No tracking hooks were loaded.

The test retains the previously added gaze engine profiles for the 30 supplied
firmware builds. Those profiles passed binary and offline checks; additional live
gaze and restoration checks are still required. See **GAZE_ENGINE_TEST_NOTES.md**.

No headset settings, installed modules or SteamVR add-ons were changed while
building this package. Physical optical-finger routing still needs a live check;
passing **Check hand/controller compatibility** alone does not establish it.
Controller version limits remain listed in **CONTROLLER_INPUT.md**.

## Trying this build

Extract the ZIP into a new folder. Keep your existing installed components, then
select Virtual Desktop and use **Check hand/controller compatibility**. If it
passes, select **Experimental hands + controllers** and press **Start tracking**.
Activity should report `HANDS_TRANSPORT` with the allocated `localPort`. A later
`HANDS_READY` confirms fresh optical input reached the controller route.

Press **Stop tracking** and wait for `CONTROLLER_CLEANUP` with `confirmed: true`.
If cleanup is unconfirmed, keep the Hub open and follow **CONTROLLER_INPUT.md**.
Do not kill another application merely because it uses PC port 27062.

The ZIP contains no camera recordings, personal profiles, headset addresses,
installed Python environments, extracted firmware engines or research caches.
