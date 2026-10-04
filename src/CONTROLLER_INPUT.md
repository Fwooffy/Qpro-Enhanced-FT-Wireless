# Experimental Quest Pro hands and controller input

This test implements two independently authored paths. It does not reuse the
QFTPlus gaze code or modify Qpro's face models. These features are **off by
default**, are currently **Virtual Desktop only**, and have not been validated
on a live headset. Do not describe this build as verified simultaneous tracking.

## How the paths work

**Hands + controllers** temporarily enables optical hand data in Virtual
Desktop's headset runtime, retains the physical controller route, and sends the
31-bone finger skeleton to that controller's SteamVR skeletal input. Virtual
Desktop carries the data over its existing stream. Controller poses, buttons
and haptics still come from the physical controllers. This requires private
runtime compatibility facts as well as public Mono/OpenVR interfaces.

**Touch Pro thumb-rest input** reads the controller sensor pages through a
root-only, read-only headset helper. Qpro forwards versioned sensor packets over
ADB to a Windows SteamVR add-on, which exposes trackpad axes, touch, force and
click. It does not add another tracked controller or send finger data through
VRCFaceTracking. App bindings determine what the new inputs do.

The UI exposes Trackpad, Relative joystick, Swipe and Desktop mouse modes.
Mouse mode is an explicit choice. Trigger-slide input and automatic VRChat
bindings are not included in this first prototype.

## Compatibility

| Component | First admitted profile | Verification limit |
| --- | --- | --- |
| Headset Virtual Desktop | 1.34.22.0 | Exact version gate; live routing pending |
| PC Streamer driver | SHA-256 `ad3c99c7f7346613d4c7106fb94476be5859ca17a637a11cfc156184d466f36f` | Exact fingerprint gate; live routing pending |
| Python and Android helper | Frida 17.18.0, Windows x64 / Android ARM64 | Official downloads pinned by SHA-256 |
| Thumb-rest sensor layout | Firmware build `51503870024400340` | Experimental read-only profile; live validation pending |
| Steam Link | Not admitted | Controller identity and optical transport need separate validation |

Enable headset hand tracking and Singularity's **Simultaneous Hands &
Controllers** switch. Turn off Singularity's separate Frida Server before
starting Qpro hands. Keep controller firmware current. Body-tracking
interaction is unverified. A compatible version/fingerprint is an admission
check, not proof that the feature works on every headset.

Installing hand/controller components does not turn on headset hand tracking or
Singularity's switch. The two boolean preference readings can report inactive
before Virtual Desktop requests multimodal mode, even with the menu switches on.
Known inactive values produce startup warnings; Qpro then verifies actual optical
finger data and SteamVR skeleton updates. Simultaneous mode must still be `1`,
and missing, duplicate or unsupported values refuse startup. The check reads
preferences for the currently active headset user and never writes them.
Uninstalling the controller add-on only removes optional thumb-rest input; it
does not uninstall the hands runtime components.

## Setup and use

1. Install the normal **PC runtime** through the Hub. Close **SteamVR**.
2. Open **First-time setup > Optional hands and controllers** and press
   **Install hand/controller components**. The optional download uses a dedicated
   Qpro Python environment and checks official artifact hashes. It registers
   only Qpro's own add-on path. It does not edit global Python or
   `steamvr.vrsettings`.
3. Reopen SteamVR through Virtual Desktop. On **Live tracking > Hands and
   controllers**, press **Check hand/controller compatibility**. This reads
   versions, settings and the driver fingerprint; it does not attach or start
   a Frida helper. Thumb-rest sensor admission is checked separately on start.
4. Enable the feature(s) you want, select a **Thumb-rest mode** if relevant, and
   press **Start tracking**. Other Qpro features can remain off. Hand/controller
   input by itself does not require VRCFaceTracking's face module.
5. Confirm that optical fingers, controller poses, buttons and haptics all work.
   For thumb-rest input, check SteamVR's controller bindings for your app.
   Forwarded packets alone do not prove that SteamVR adopted the input profile.

## Stop and recovery

**Stop tracking** requests reverse-order restoration of the temporary hand
adapters. Each adapter also has a renewable lease. The supervisor remains
running while owned workers clean up so the Hub does not orphan them. Activity
distinguishes valid routed fingers from hook startup, and reports cleanup
failures explicitly. Unknown product versions and driver fingerprints fail
before attachment; runtime ABI mismatches fail before adapter activation.

The read-only sensor helper has a bounded connection lifetime. Stale or invalid
packets clear custom controller output; mouse mode releases its pressed button.
The controller add-on keeps ordinary controller identity and forwards the
original controller operations. SteamVR can cache its custom input profile.
To restore the normal profile, close SteamVR, press **Uninstall controller
add-on**, then reopen SteamVR. Uninstall keeps a recovery copy.

If hand restoration is not confirmed, keep the Hub open until its workers exit,
then restart headset Virtual Desktop and SteamVR before retrying. Qpro does not
kill a foreign Frida server, restart trackingservice, or disable Magisk modules
for these features. Use the gaze recovery controls only for a separate gaze
problem.

## Source and dependency notices

The new adapters, supervisor, packet protocol, contact logic and SteamVR add-on
were authored for this project. QFTPlus was inspected to understand the runtime
interfaces and compatibility layout; its source and binaries are not shipped in
the test ZIP. The pinned compatibility facts need revalidation when runtimes
change.

- [QFTPlus reference](https://github.com/Yeusepe/QFTPlus/tree/aff54dcc70ea87ac04f93da3063c05a5d30cd7a4)
- [Valve OpenVR skeletal input](https://github.com/ValveSoftware/openvr/wiki/Creating-a-Skeletal-Input-Driver)
- [Frida JavaScript API](https://frida.re/docs/javascript-api/)
- [Official Frida releases and source](https://github.com/frida/frida)

Valve's pinned header license is in `controller-input/third_party/openvr` in the
source repository and accompanies the add-on in the package. The optional
Frida wheel retains its upstream metadata/license; its source and server
release are linked above. No proprietary Virtual Desktop engine or driver is
distributed by this feature.
