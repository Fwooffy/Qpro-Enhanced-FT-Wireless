# Qpro Companion: experimental hands and controller input

This test implements two independently authored paths. It does not reuse the
QFTPlus gaze code or modify Qpro's face models. These features are **off by
default** and are currently **Virtual Desktop only**. A live test confirmed real
finger movement while controllers were held, but also found a substantial frame
rate drop. The native-callback revision still needs a live performance check;
do not describe it as ready for general release.

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
Mouse mode is an explicit choice. Setup can prepare Dashboard and VRChat menu
scrolling for recognized bindings; other app actions need manual assignments.
Trigger-slide input is not included.

## Compatibility

| Component | First admitted profile | Verification limit |
| --- | --- | --- |
| Headset Virtual Desktop | 1.34.22.0 | Exact version gate; sustained performance unverified |
| PC Streamer driver | Exact SHA-256 fingerprints in `hybrid/compatibility.json`; signed Streamer 1.34.23 PC driver also inspected | Private PC layout checked independently; no live test of the new PC/headset pair |
| Python and Android helper | Frida 17.18.0, Windows x64 / Android ARM64 | Official downloads pinned by SHA-256 |
| Thumb-rest sensor layout | Firmware build `51503870024400340` | Both thumbs scrolled well at 100% on one headset; Stop restored neutral output and ordinary controls; Desktop mouse felt smoother |
| Steam Link | Not admitted | Controller identity and optical transport need separate validation |

Before starting Qpro hands, enable both **Hand tracking** and **Automatically
switch between controllers and hands** in Quest **Settings > Hands & eyes**,
plus Singularity's **Simultaneous Hands & Controllers** switch. Installing
Qpro's components does not enable these menu settings. The compatibility check
does not verify the Quest auto-switch menu option; confirm it in the headset.
Turn off Singularity's separate Frida Server before use. Keep controller
firmware current. Body-tracking interaction is unverified. A compatible
version/fingerprint is an admission check, not proof that the feature works on
every headset.

The first checked headset version remains **Virtual Desktop Android 1.34.22.0**.
The signed **Streamer 1.34.23** PC driver is admitted separately because its
private hand layout was inspected and its OpenVR input interface is already
supported by the adapter. This does **not** enable Android **1.34.23.0**: that
headset build still needs managed hand-result and native caller evidence.
If the compatibility check reports an unsupported headset version, send its
Activity details to Qpro support. Reinstalling the hand components cannot add a
missing hand profile. Ordinary face tracking can still be used with Hands +
controllers turned off.

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

Controller switches, modes and the compatibility check are on the **Controllers**
sidebar page, directly below **Facial tracking**. Optional installation stays on
**First-time setup**. The bottom **Start tracking** / **Stop tracking** bar is
shared across pages and controls all selected Qpro features.

### Headset and VRChat settings

Complete the settings below before installing or starting Qpro hands:

| Where | Required setting |
| --- | --- |
| Quest **Settings > Hands & eyes** | **Hand tracking: On.** **Automatically switch between controllers and hands: On.** |
| **Singularity > AIO Tweaks** | **Simultaneous Hands & Controllers: On.** Some versions say **Simultaneous Hands Controllers**. |
| Virtual Desktop on the Quest **> Streaming** | **Forward tracking data to PC: On.** **Track controllers: On.** |
| VRChat **Main Menu > Controls** | **Avatars Use Finger Tracking: On.** |

1. On the Quest, enable both **Hand tracking** and **Automatically switch
   between controllers and hands** in **Settings > Hands & eyes**. Set the
   controllers down and check that the headset sees your empty hands. Keep the
   headset awake, connect USB or wireless ADB, and allow **Shell / ADB Shell**
   root access if Magisk asks.
2. Open **Singularity > AIO Tweaks** and enable **Simultaneous Hands &
   Controllers**. Some versions label it **Simultaneous Hands Controllers**;
   its location can vary. Leave it enabled while using Qpro hands. Turn off
   Singularity's separate **Frida Server** before starting Qpro's helper.
   Singularity introduced these options in its
   [v1.0.4.3 release](https://github.com/Lumince/singularity/releases/tag/v1.0.4.3).
3. Connect the headset to its PC Streamer inside **Virtual Desktop**. Run
   **SteamVR through Virtual Desktop** and use the SteamVR version of VRChat.
   ADB provides Qpro's setup/control connection; it does not replace Virtual
   Desktop's VR stream. Set the headset app's Streaming options as listed below.
4. In VRChat, enable **Main Menu > Controls > Avatars Use Finger Tracking**.
   This lets SteamVR skeletal input animate the avatar's fingers. See
   [VRChat's Input 2.0 FAQ](https://docs.vrchat.com/docs/input-20-faq).
5. Before starting Qpro hands, set controllers down and verify ordinary
   empty-hand tracking. Tap the controllers together twice in Virtual Desktop
   if needed to enter hand mode. Continue only when fingers move in VRChat.

### Virtual Desktop settings for the initial finger check

Open **Virtual Desktop on the Quest > Streaming**; expand **Advanced Options**
if needed. These are headset-app options, not options in the Windows Streamer
window.

| Setting | Initial setting |
| --- | --- |
| **Forward tracking data to PC** | **On**; accept **Yes** at the tracking-data permission prompt. |
| **Track controllers** | **On** so the held Touch Pro controllers remain present in SteamVR. |
| **Emulate Index controllers** | **Off** for the initial VRChat optical-finger check. |

Virtual Desktop's [own VRChat hand-tracking instructions](https://github.com/guygodin/VirtualDesktop/releases/tag/v1.32.13)
specify forwarding on and Index emulation unchecked. Qpro routes optical fingers
to the held physical controller's existing skeletal input; it does not require
Index emulation. The [VRCFaceTracking Virtual Desktop guide](https://docs.vrcft.io/docs/hardware/vr/meta/quest-pro/virtual-desktop)
also documents forwarding and its consent prompt, and explains that disabling
**Track controllers** removes Quest controllers from SteamVR.

Keep Quest hand tracking, **Automatically switch between controllers and
hands**, and Singularity's simultaneous switch enabled too.
Emulated Vive trackers are optional body tracking, not a requirement for Qpro
fingers. Start the PC Streamer and connect through Virtual Desktop, then launch
SteamVR through Virtual Desktop. These settings do not bypass Qpro's exact
version and driver checks; newer headset builds still need their own profile.

### Install once, then start a session

1. Install the normal **PC runtime** through the Hub. Close **SteamVR** and
   wait for it to exit before installing optional components.
2. Open **First-time setup > Optional hands and controllers > Show hands setup
   and uninstall**, then press **Install hand/controller components**. The
   optional download uses a dedicated Qpro Python environment and checks
   official artifact hashes. It registers only Qpro's own add-on path and does
   not edit global Python or `steamvr.vrsettings`. Repeat installation when the
   Hub asks for a repair or update; it is not a step for every session.
3. Reopen SteamVR through Virtual Desktop. Select **Virtual Desktop** as the
   **Facial tracking > Your session > Streaming app**. On **Controllers**, press
   **Check hand/controller compatibility**. This reads versions, current-user
   settings and the driver fingerprint; it does not attach or start a Frida
   helper. An unsupported headset version or driver needs a checked profile,
   not another components installation. Thumb-rest sensor admission is checked
   separately on start.
4. Turn on **Experimental hands + controllers** and press **Start tracking**.
   Leave **Experimental Touch Pro thumb-rest input** off for the first finger
   check. Other Qpro features can remain off. Hands + controllers alone does
   not require VRCFaceTracking's face module.
5. Wear the headset, hold the controllers and move individual fingers where the
   headset cameras can see them. Activity should report `HANDS_READY`: fresh
   optical data reached at least one held controller's SteamVR skeletal input.
   Check both hands in VRChat, along with controller position, buttons and
   haptics. Fist/point/open gestures from touching buttons are ordinary
   controller gestures; they do not prove optical fingers are working.
6. Press **Stop tracking** when finished and wait for confirmed cleanup. If FPS
   falls or SteamVR becomes unstable, stop and leave the experimental option
   off. Do not assume a successful compatibility check guarantees acceptable
   performance.

### Troubleshooting

- **Only controller gestures:** recheck Quest **Hand tracking** and
  **Automatically switch between controllers and hands**, Singularity's
  simultaneous switch and VRChat's **Avatars Use Finger Tracking** setting.
  Verify ordinary empty-hand tracking first, then keep fingers visible and
  inspect Activity for `HANDS_READY` or routing errors.
- **A preliminary setting is false while the switches are on:** the current
  code warns on known inactive hand/multimodal boolean preferences and proceeds
  to verify live optical input. Simultaneous mode must still be `1`. Missing,
  unreadable, duplicate or unsupported values refuse startup. Recheck ADB/root
  access and reconnect Virtual Desktop; do not edit the preferences by hand.
- **Unsupported headset version, driver or controller ABI:** keep hands off and
  copy the full diagnostics. Components installation cannot supply a missing
  adapter profile. Ordinary face tracking can still run separately.
- **No live routing or a disconnect:** keep the headset on, hold a controller
  and put your hand in camera view. Reconnect Virtual Desktop if its stream
  stopped, then stop Qpro before retrying. Helper startup is not optical
  readiness, and `HANDS_READY` for one side does not verify both hands.

**Thumb-rest input is separate.** Enable **Experimental Touch Pro thumb-rest
input** only after checking its firmware compatibility, then choose a
**Thumb-rest mode** and **Apply mode to**: **Left (L)**, **Right (R)** or **Both**.
The side selection applies only to extra thumb-rest inputs; normal controller
inputs and optical fingers remain available on both sides. Trackpad follows
thumb position; Relative joystick centers on the first touch; Swipe follows
movement speed with release glide. Set extra game actions in SteamVR yourself;
the menu-scrolling helpers below preserve those actions. Forwarded packets alone
do not prove SteamVR adopted the input profile. Desktop mouse mode can move the
Windows pointer instead of extra SteamVR axes. With Both selected, either thumb
can move the pointer and either held thumb-rest press keeps left-click down.

For **Swipe**, the **Swipe sensitivity** slider appears below the mode and side
controls. Choose **25%–200%** in 25% steps; **100%** is the default. Lower values
scroll more slowly; higher values scroll faster. Press
**Stop tracking**, adjust it, then **Start tracking**. This is a sensitivity
preference, not sensor calibration; it cannot correct missed sensor samples.

The installed add-on registers its extra controls when SteamVR discovers the
controllers, so the binding editor can show them before Qpro tracking starts.
Their values stay neutral until tracking starts with the feature enabled, and
return to neutral on Stop, stale input or an unselected side.

### SteamVR dashboard scrolling

The add-on installer extends the installed Valve dashboard default with a
separate thumb-rest scroll source while retaining its ordinary controls. Turn
on **Experimental Touch Pro thumb-rest input**, select **Swipe**, choose the
controller side and press **Start tracking**. The dedicated source scrolls only
in Swipe mode during tracking; it is neutral in other modes and after Stop.
Keep both controllers awake during the initial thumb-rest check, even with
**Left (L)** or **Right (R)** selected.

Setup preserves personal binding choices and ordinary controller actions. It
can select a prepared binding only when it recognizes the installed format and
can verify SteamVR's current controller binding key. Otherwise, **Activity**
explains how to select the prepared **Qpro Companion thumb-rest scrolling**
binding manually. Unsupported or ambiguous choices stay unchanged with a warning.
Preparation alone does not confirm that SteamVR loaded the binding.

### VRChat menu scrolling

The installer adds the dedicated Swipe source to a copy of a recognized VRChat
binding. It preserves movement, gestures, buttons and other actions. It can
select that copy automatically only when it can verify SteamVR's current
controller binding key. Without one, a recognized default can be prepared and
**Activity** asks you to select it in SteamVR's VRChat controller bindings.
Unsupported bindings stay unchanged with a warning.

This prepares VRChat menu scrolling, not universal game bindings. Assign extra
actions separately to **Qpro thumb-rest surface (extra)** for each app. Reopen
SteamVR through Virtual Desktop after installation, select **Swipe**, then
start tracking and test both dashboard and VRChat menus. Other modes, Stop and
stale input send neutral scroll. Live checks on the admitted profile confirmed
scrolling in both Dashboard and VRChat. With **Both** and **100%** selected, both
thumbs scrolled well and ordinary buttons still worked on the checked headset.
Stop cleared scrolling, with neutral output and ordinary controls confirmed.
Desktop mouse movement also felt smoother in the live check. Other firmware
and app bindings need their own checks after installation.

## Stop and recovery

**Stop tracking** requests reverse-order restoration of the temporary hand
adapters. Each adapter also has a renewable lease. The supervisor remains
running while owned workers clean up so the Hub does not orphan them. Activity
distinguishes valid routed fingers from hook startup, and reports cleanup
failures explicitly. Unknown product versions and driver fingerprints fail
before attachment; runtime ABI mismatches fail before adapter activation.

Frequent SteamVR skeleton and pose callbacks run in native code. The headset's
controller query and idle Update gate also run natively; managed finger
conversion retains its existing error handling. Stop disables new mutable work,
waits for accepted callbacks, then restores the temporary state. Native code and
callback memory remain retained until Frida finishes unloading the script.
An incomplete drain or failed restoration is reported as a cleanup failure.

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
