# Qpro Companion PDF guide

<!-- Concise print edition. Keep control names and behavior aligned with RELEASE_INSTRUCTIONS.md and the Hub. Each H2 begins a page; the builder checks that it fits. -->

## Your first session

Start with the three cards on **First-time setup**. Complete one step at a time; optional gaze, AMD and controller features can wait.

1. **Connect your Quest Pro.** Start with **USB cable**, approve USB debugging in the headset and allow **Shell / ADB Shell** in Magisk.
2. **Install the PC runtime.** Press **Install runtime**. Keep the Hub open while its private Python and tracking libraries download. Wait for completion in **Activity**.
3. **Install your VRCFaceTracking module.** Close VRCFaceTracking, choose **Streaming app**, install the matching Qpro module and reopen VRCFaceTracking.

> [!TIP] Check the source before adding features
> Connect through Virtual Desktop or Steam Link and confirm that mouth expressions and blinks move in VRCFaceTracking's preview. An installed module or green app status does not establish live tracking.

Once expressions move, open **Facial tracking**, choose your features and press **Start tracking**. Finish with **Stop tracking** and wait for cleanup in **Activity**.

Already using the independent-gaze Magisk module? When detected, Hub gaze stays off and disabled. Skip **Check gaze setup** and **Prepare gaze**; ordinary face tracking does not need them.

### Find your next step

| Topic | Jump to the guide page |
| --- | --- |
| Required apps and download | [Before you start](#before-you-start) |
| Streaming app settings | [Virtual Desktop and Steam Link](#virtual-desktop-and-steam-link) |
| Connect and install | [USB cable](#usb-cable), [Wireless ADB](#wireless-adb), [Runtime and Qpro module](#runtime-and-qpro-module) |
| Features and tongue training | [Facial tracking](#facial-tracking), [Cheek puff and cheek suck](#cheek-puff-and-cheek-suck), [Personalize](#personalize) |
| Optional setup and help | [Independent eye gaze](#independent-eye-gaze), [AMD ROCm](#amd-rocm), [Controllers](#controllers), [Setup problems](#setup-problems) |

Read only the optional feature pages you need. The top-right **Contents** link returns here; PDF bookmarks open any topic directly.

## Before you start

**Qpro Companion** is the app; its main window is the **Hub**. **ADB** connects it to your Quest. The **Qpro module** supplies face tracking to VRCFaceTracking.

### On the Quest Pro

- A **rooted Meta Quest Pro** with Developer Mode enabled. If it is not rooted, first check exact firmware support in [Singularity](https://github.com/Lumince/singularity) and follow [Fwooffy and glorpette's root guide](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md).
- In **Settings > Hands & Eyes**, enable **Eye Tracking** and **Natural Facial Expressions**. Run eye calibration if offered. Allow your streaming app to use eye and face data.
- Keep the headset awake. Approve USB debugging and Magisk **Shell / ADB Shell** root access when prompted.

### On the PC

Install **SteamVR**, the [latest VRCFaceTracking from Steam](https://store.steampowered.com/app/3329480/VRCFaceTracking/) and your streaming app's PC software. Setup needs internet access and several gigabytes of free space.

1. Download **QproCompanion.V3.0.0.zip** from [Releases](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases) when published, or use the supplied release candidate ZIP.
2. Right-click the ZIP in File Explorer and choose **Extract All**. Keep `QproFaceTracking.exe`, `Helpers` and `QproRuntime` together.
3. Open `QproFaceTracking.exe`. For OneDrive, first choose **Always keep on this device** for the whole extracted folder.

> [!NOTE] Independent gaze is optional
> You do not need a gaze Magisk module or the Hub's gaze preparation for ordinary face tracking, tongue tracking or pupil dilation.

ADB and private Python are supplied. For update compatibility, the executable, app/data folders and `.qptonguemodel` format keep their old names. The legacy **QproFaceTracking.V3.0.0.zip** is the same package for older updaters; use either ZIP. GitHub's **Source code** archives are not the app.

## Virtual Desktop and Steam Link

Choose one **Streaming app** in the Hub, then install its matching Qpro module.

### Virtual Desktop

Connect the headset app to the [PC Streamer](https://www.vrdesktop.net/). Open **Streaming** in Virtual Desktop **on the headset**, expanding **Advanced Options** if needed.

| Setting | Initial Qpro / VRChat setup |
| --- | --- |
| Forward tracking data to PC | **On.** Accept **Yes** when asked to share tracking data. |
| Track controllers | **On** for Touch Pro, including Qpro hands + controllers. |
| Emulate Index controllers | **Off** for the initial VRChat optical-finger check. |

Start the PC Streamer and connect before opening VRCFaceTracking. Launch **SteamVR through Virtual Desktop**. For fingers, enable VRChat **Main Menu > Controls > Avatars Use Finger Tracking**. Emulated Vive trackers are optional body tracking, not a face-tracking requirement.

For **Hands + controllers**, also enable Quest **Hand tracking** and **Automatically switch between controllers and hands**, plus Singularity's simultaneous switch. See [the complete prerequisites](#controllers).

### Steam Link

In the headset's **Advanced Settings**, enable **OSC**, **Share eye tracking data** and **Share face tracking data**. Set **OSC Output Port** to **9015**. Use **Install Steam Link module** with VRCFaceTracking closed.

> [!WARNING] Use one source module
> Installing one Qpro source module removes the other Qpro source module. Remove separate Virtual Desktop, LinkFT or Steam Link face modules through VRCFaceTracking; competing modules can occupy the same tracking slots or OSC port.

**USB cable** and **Wireless ADB (Wi-Fi)** choose the Qpro tracking-data connection, not the VR picture stream. The [full text guide](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/blob/main/src/RELEASE_INSTRUCTIONS.md) also covers the optional Virtual Desktop USB-video beta setup.

## USB cable

USB is the easiest first connection. Leave the cable attached and the headset awake while setting up.

1. Connect the Quest Pro to the PC and put it on.
2. In **USB debugging**, choose **Always allow from this computer**, then **Allow**. The Hub cannot approve this headset permission for you.
3. On **First-time setup**, set **Connection type** to **USB cable**. Qpro verifies the Quest Pro and remembers it.
4. If root access is unavailable, allow **Shell / ADB Shell** in Magisk. Open **Facial tracking > Your session > Show connection and camera settings**, then **Refresh connection status**.

> [!TIP] You should not normally need to unplug USB
> The USB selection stays in the app's settings. After reopening the Hub, keep the Quest awake and connected. If it stalls, stop tracking and training, then press **Reconnect USB** in **First-time setup**.

**Reconnect USB** first verifies an already working connection. It does not reset a healthy USB headset; offline recovery is attempted only when needed.

| Result | Next step |
| --- | --- |
| Unauthorized | Wear the headset and approve its USB debugging prompt. |
| Offline or stalled | Stop tracking and training, then use **Reconnect USB**. |
| Missing device | Check the cable, USB port and Windows USB driver. |
| A different Quest is remembered | Use **Forget USB headset**, connect the intended Quest Pro, then **Reconnect USB**. |

Qpro does not silently replace a remembered headset. If several Android USB devices are attached during the first check, temporarily disconnect the others.

Closing the Hub stops its live processes and then the configured PC ADB server. Other Android tools using that server disconnect too and may restart it. Finish setup, capture, training and gaze recovery before closing.

## Wireless ADB

Keep the Quest and PC on the same trusted private Wi-Fi network. You can start with USB, then switch to wireless.

1. On **First-time setup**, choose **Wireless ADB (Wi-Fi)** under **Connection type**.
2. Enter the Quest's address in **Quest IP:port**, then press **Connect to Quest**. For example, `192.168.1.25:5555` uses the usual port, but you must use your own Quest's IP.
3. **Quest connected** and **Wireless Quest ready** mean ADB and root are ready. You can unplug USB if it was attached.
4. If wireless is off but USB works, connect USB once and press **Enable from USB**. After success, unplug USB and connect to the saved address.

> [!TIP] Ready means you can skip pairing
> If **Connect to Quest** succeeds, do not press **Pair and connect**. Pairing is only for a headset showing a six-digit wireless debugging pairing code.

### When pairing is required

Open **Show pairing options**. Keep the headset's pairing prompt open while entering:

| Hub field | What to enter |
| --- | --- |
| Quest IP:port | The normal connection address, usually ending in `:5555`. |
| Pairing IP:port | The short-lived address beside the pairing code. Its port is different. |
| Six-digit code | The current code from that same headset prompt. |

A `protocol fault` usually needs a fresh code and temporary port. After rebooting, recheck the Wi-Fi IP and whether wireless ADB needs enabling again. **Activity** gives the exact failure.

## Runtime and Qpro module

After the Quest connects, complete the remaining **First-time setup** cards.

### Install the PC runtime

Press **Install runtime** and keep the Hub open. **Activity** reports PowerShell startup, the current phase, downloads and still-running messages when output pauses. PyTorch is a large download.

Qpro uses private Python and works with or without an existing Python installation. You do not need to uninstall another app's Python. NVIDIA CUDA or CPU uses the normal runtime; AMD acceleration is a separate optional step.

Use **Repair runtime** to rebuild damaged tracking packages. **Restore saved runtime** checks saved backups before replacing the current runtime, and keeps a recovery copy. Both actions require tracking, capture and training to be stopped. They preserve models, recordings and Facial tracking choices; AMD ROCm has its own separate environment.

### Install your VRCFaceTracking module

1. **Close VRCFaceTracking** and wait for its module process to exit.
2. Choose **Virtual Desktop** or **Steam Link** under **Streaming app**.
3. Press **Install Virtual Desktop module** or **Install Steam Link module**. Only the selected source's button is available. Wait for completion in **Activity**.
4. Reopen VRCFaceTracking with the source connected. Check that ordinary expressions move in its preview.

Its card is **Qpro Companion — Virtual Desktop** or **Qpro Companion — Steam Link**, using the Hub's release version. Older QproFaceTracking cards remain recognized.

> [!NOTE] What a module check proves
> **Installed · input unchecked** checks files. Tracking details also show a module heartbeat and recent applied output. Virtual Desktop's native shared memory has no timestamp, so its input age is unknown. Confirm expressions in VRCFaceTracking's preview; heartbeat does not prove avatar movement.

Junctions, symlinks and Windows Cloud Files are supported. For OneDrive, keep the whole app available offline. Recovery copies go under `%LOCALAPPDATA%\QproFaceTracking\module-recovery`; **Activity** gives the exact path.

For a damaged/wrong-source module, follow **Activity**, close VRCFaceTracking and reinstall. File-access/verification errors need their specific diagnosis; they do not by themselves establish card damage. **Check headset compatibility** and **Show firmware details** are optional diagnostics.

## Facial tracking

Start the streaming app, SteamVR and VRCFaceTracking. Confirm live face input, then open **Facial tracking**.

| Section | What it controls |
| --- | --- |
| Your session | Compact readiness status, **Streaming app**, and **Show connection and camera settings** for camera options. |
| Eyes | **Independent eye gaze + convergence**, **Experimental relative pupil dilation (eye cameras)** and **Adjust eyebrow movement**. |
| Lower-face tracking | **Tongue model**, tongue output and the native individual cheek puff/suck switches. |

Open **Controllers** below **Facial tracking** for controller options. Health and **Show tracking details** are on **Activity > Tracking health**. The bottom **Start tracking** / **Stop tracking** bar controls all selected features.

1. Choose your features. For tongue output, select **Developer tongue model** or your personal **Tongue model** and enable **Experimental tongue tracking**. Selecting a model alone does not enable its output. With Magisk gaze active, keep Hub gaze off.
2. Press **Start tracking**. **Activity** confirms requested outputs, the loaded model and GPU or CPU backend.
3. Use **Stop tracking** before changing a model or camera setting, then start again.

### Camera windows are optional

**Preview tracking cameras** defaults off on a fresh setup. Find it beside **Camera FPS cap** under **Your session > Show connection and camera settings**. Saved choices persist. Hiding a preview does not stop tracking; guided capture still opens its pose window.

**Processing profile** defaults to **Responsive (default)**, keeping the current tracking cadence. **Balanced** limits pupil work to 12 eye pairs per second; **Low CPU** limits it to 8. Tongue keeps the camera cadence in every preset. Lower pupil rates mean slower response and warm-up. Choices are saved for the next start.

> [!NOTE] Stop restores native cheeks
> Tongue and pupil outputs stop. Individual cheek puff/suck also stop, restoring native cheeks. Eyebrow and built-in smirk adjustments can remain through the installed module; Magisk modules stay active.

Wait for cleanup in **Activity** before closing. The tongue model runs on the PC, not on the headset.

## Eyes

Ordinary eye tracking comes from the streaming app. Enable only the extra features you want.

### Independent eye gaze + convergence

This starts off. When an active gaze Magisk module is detected, it reads **provided by Magisk** and stays off and disabled. Skip **Check gaze setup** and **Prepare gaze**. Pupil, eyebrow and tongue controls work separately. The next page covers the temporary Hub method.

### Experimental relative pupil dilation (eye cameras)

Open **Show eye settings and guidance** for **Pupil response**. Start near the default and change it slowly. Hold your gaze steady while tracking warms up. Your avatar must support pupil animation.

With a verified NVIDIA CUDA or AMD ROCm runtime, both eye images are filtered on the GPU. Contour fitting, quality checks and smoothing still use CPU. An unavailable or failed GPU is reported in **Activity**, with CPU fallback.

> [!WARNING] Animation estimates, not eye-health measurements
> GPU processing does not guarantee a particular FPS or eliminate all CPU work. Pupil values are estimates for avatar animation.

### Adjust eyebrow movement

This starts off, passing native brow values. Turn it on and use **Eyebrow sensitivity** in **Show eye settings and guidance** to soften or amplify existing left/right inner raise, outer raise and lower/pinch movements. It applies immediately through the installed module; **Start tracking** is not required and **Stop tracking** does not disable it.

Sensitivity cannot invent movement the headset did not detect. If VRCFaceTracking shows separate brows but the avatar does not, check its parameters and blendshapes.

For poor performance, close unused camera/browser previews. Inspect **Activity** for backend, processing time, frame age and skipped work. Qpro processes the latest eye pair rather than queuing old frames.

## Independent eye gaze

Choose one method. Independent gaze is optional; tongue and pupils do not require it.

| Method | What to do in the Hub |
| --- | --- |
| Enabled gaze Magisk module | When detected, Hub gaze stays off and disabled. Skip **Check gaze setup** and **Prepare gaze**; those buttons are disabled too. |
| Temporary Hub method | Disable the gaze Magisk module and reboot first. Use **Check gaze setup**, then **Prepare gaze**. Enable Hub gaze only if its checks pass. |

The Magisk route lists **Quest Pro Independent Eye Gaze** and **Magisk OverlayFS** in Singularity **Apps/Modules > Magisk repo**. Follow its firmware requirements and reboot prompts. After changing the module, reboot, then use **Reconnect USB** or **Check headset compatibility** to refresh detection. A failed check is not proof that no module exists.

For the Hub route, open **First-time setup > Optional independent gaze > Show gaze setup**. The Hub reads that headset's eye model and engine and prepares a temporary model on the PC. **You do not need to supply an engine file.** A build number alone does not establish engine compatibility.

> [!WARNING] Wait for the service restart
> Applying/restoring Hub gaze briefly freezes the headset and can interrupt positional tracking while Meta trackingservice restarts. Wait for **Activity** to confirm recovery. Do not combine methods to bypass a failed compatibility check.

### Show gaze recovery tools

On the optional gaze card, open **Show gaze setup**, then **Show gaze recovery tools**:

- **Recover Qpro gaze** restores the previous state recorded for a verified Qpro session. It does not remove unrelated modifications.
- **Reset legacy gaze** offers a confirmed normal gaze selection for an older session without a record. It refuses active gaze Magisk modules or unverified overlays; it does not overwrite firmware.

**Stop tracking** reverses recorded Hub changes, not Magisk modules. To return from Magisk gaze to ordinary gaze, disable that module and reboot.

## Cheek puff and cheek suck

The switches in **Facial tracking > Lower-face tracking** adjust your streaming app's native cheek signals. They do not use a camera model or require cheek training.

### Individual cheek puff (1/0)

Enable it for **1/0** separation. A clear one-sided puff becomes full strength on that side and zero on the other. A deliberate two-cheek puff still moves both.

### Individual cheek suck (1/0)

Enable it for the same **1/0** separation of the source's separate left/right cheek-suck signals. It does **not** use negative cheek-puff values. A deliberate two-cheek suck still moves both.

1. Connect your streaming app and confirm that expressions move in VRCFaceTracking's preview.
2. Turn on the individual cheek switches you want, then press **Start tracking**.
3. Try your own left cheek, right cheek, then both. Compare VRCFaceTracking's preview with your avatar.

> [!TIP] Turn a switch off for native values
> A disabled switch passes through that source's original puff or suck values. **Stop tracking**, closing the Hub or a Hub crash restores native cheeks. Saved choices remain for next time.

Native cheek adjustments alone do not need camera tracking, a tongue model or calibration. Keep other camera features off if you only want these cheek adjustments.

If the preview separates the sides but the avatar does not, check its cheek parameters and blendshapes. Small false native cheek values can still appear during other expressions; the module cannot guarantee perfect cheek detection for every face.

## Personalize

Open **Personalize** in the sidebar; its page heading is **Tongue training/refinement**. Record, train, then select the result.

| Capture mode | What it records |
| --- | --- |
| Quick refinement | **16 tongue cards** for a copy of the selected model. |
| Focused diagonals + facial hair | **22 tongue cards**, about **15-30 minutes**. Diagonals and matched hidden/visible poses. |
| Full dataset | **58 tongue cards** for a new personal model. Take breaks. |

1. For Quick or Focused, first choose the model to extend under **Facial tracking > Lower-face tracking > Tongue model**. Full creates a new personal model instead.
2. Quick uses **1. Record refinement** and **2. Train personalized copy**. **Show focused and full captures** reveals the other modes.
3. Hold each pose. Press **SPACE** once per still; save at least the displayed minimum before **ENTER** advances.
4. Under **Recording to train**, choose your completed recording and press that mode's training button. Wait for completion in **Activity**.
5. Select the result under **Tongue model**, then stop and restart tracking.

> [!TIP] Capture varied stills, not a moving pose
> Hold one pose per still. Vary jaw/headset position slightly between stills. Include hidden tongue, visible extension strengths and diagonals. Capture smiles and other hidden-tongue poses too, so they are not mistaken for tongue movement.

Tongue cards need fresh VRCFaceTracking input, even while values remain steady. Held-out training checks use the same recording; a fresh recording or live test still matters.

## Models and facial hair

**Developer tongue model** is the default on a fresh setup. Updates keep personal/imported selections; retired bundled choices switch to the new developer model. It adds a visibility detector trained with clean-shaven recordings while retaining the original developer tongue direction outputs.

A separate recording after reseating the same wearer's headset showed fewer missed detections with native fusion. One small tongue tip during a smile was still missed. Some older bearded poses worsened; broader wearer and facial-hair testing remain pending. Start with **Tongue visibility > Weighted camera + native**; **Camera only** can produce false detections.

**Developer tongue model** and **Mustachio** are the two bundled choices. Personal imports and refinements remain available. Local model numbers can change during updates or imports; choose the friendly name under **Tongue model**.

### Mustachio

**Mustachio · highly experimental · v9** combines the earlier developer tongue model with a focused capture from one bearded and moustached wearer. Broader facial-hair and independent clean-shaven tests are pending; it may work worse for some people.

Select it on **Facial tracking**, then restart. Contrast processing is automatic. Quick/Focused can extend it, keeping the experimental label after export/import or renaming. Full makes a new personal model.

> [!WARNING] Hair cannot be removed by training
> Keep facial hair/bandages as normally worn. Check both camera views during visible poses and record hidden poses too. Contrast processing cannot reveal a completely covered tongue or invent hidden pixels.

### Model manager

Export backups and import working **V2.0 or newer** `.qptonguemodel` files. Pre-V2.0 models need recapture/retraining. Exports contain weights, not camera recordings. You can refine an imported model with **new** recordings; exports cannot reconstruct its original captured poses.

**Activity** names the loaded model. Use **Lower-face tracking > Show camera settings > Motion smoothing** to soften extension/retraction. Start with **Tongue visibility > Weighted camera + native**. Use **Camera only** only after checking for false detections and obscured poses.

### Delete a saved recording

On **Personalize**, open **Show saved recordings** under its capture card. Choose **Saved recordings (including trained)** and press **Delete selected dataset…**. Confirm carefully: recordings, labels, session details and prepared cache are permanently removed. Already-trained models remain in **Model manager**.

## AMD ROCm

Optional AMD acceleration comes after **Install runtime**. NVIDIA/CPU users skip this step.

Open **First-time setup > Optional AMD ROCm acceleration > Show AMD setup**, then **Install ROCm 10.1** for an eligible discrete Radeon. Large GPU-specific downloads go into a separate Qpro environment.

| Series | Models mapped for ROCm 10.1 |
| --- | --- |
| RX 6000 | 6950 XT, 6900 XT, 6800 XT, 6800; 6750 XT, 6700 XT, 6700; 6650 XT, 6600 XT, 6600. |
| RX 7000 | 7900 XTX, 7900 XT, 7900 GRE; 7800 XT, 7700 XT, 7700; 7600 XT, 7600. |
| RX 9000 | 9070 XT, 9070, 9070 GRE; 9060 XT, 9060. |
| Radeon PRO | AI PRO R9700, PRO W7900, PRO W7900 Dual Slot. |

> [!WARNING] Mapping is not a hardware guarantee
> The new runtime is enabled only after **GPU training and inference checks pass**. Integrated graphics and unlisted cards are ineligible. Use in Qpro remains experimental; every mapped card/driver still needs physical validation.

AMD's supported Windows configuration is **Windows 11**. Windows 10 22H2, build 19045+, needs **Try ROCm on Windows 10 (experimental)** under **Show AMD compatibility details**; success is not guaranteed. Older Windows and legacy ROCm 7.2.1 on Windows 10 are not admitted.

Short paths under `%LOCALAPPDATA%\QproFaceTracking\r` avoid long library paths without registry changes. Verified older environments remain until a replacement passes; eligible legacy installations can remain fallbacks.

The 10.1 recipe passed offline checks, not a live installation in this review. See [AMD TheRock releases](https://github.com/ROCm/TheRock/blob/main/RELEASES.md), [AMD's compatibility matrix](https://rocm.docs.amd.com/en/latest/compatibility/compatibility-matrix.html) and [the full text guide](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/blob/main/src/RELEASE_INSTRUCTIONS.md) for exact driver/package details.

## Updates

The Hub checks for a newer stable release when it opens. **Update available** offers **View update** for details or **Later** to dismiss. It waits until the Hub is active and idle, and appears once per release per session. Neither choice installs anything.

1. Use **View update** or the top-right **Update available** link to read release notes. After **Later**, that link keeps the checked details, including after a temporary network failure. **Check updates** checks manually.
2. Stop tracking, then click **Update**. The ZIP checksum and packaged files are verified. Cancelling the download leaves the current app unchanged.
3. Click **Restart and update**. App files are replaced in the **same folder**, then the Hub reopens. Start tracking yourself when ready.
4. Open **Components**, review its changes and click **Update components** for installed modules/private runtimes that need updating.

> [!TIP] Your setup stays with you
> Personal models, recordings, enabled options and connections are preserved. Retired bundled choices switch to the new developer model. A personal pair keeps its number and selection; the new model uses another number if needed. Updates do not start tracking automatically.

### Update choices

- Turn **Check automatically when the Hub opens** off for manual checks.
- **Include prereleases (test builds)** is off by default and remembered. A release needs a newer three-part version, such as `v3.0.1`. Same-version ZIPs are not offered; suffix tags such as `v3.0.1-beta.1` need manual download.
- Without a checksum or a compatible package, use **View release on GitHub** for manual download.

### Components and recovery

Close VRCFaceTracking and wait for its module process to exit before a module update. Stop tracking/training before any component update. Reopen VRCFaceTracking afterward.

Updates retain the streaming app and ROCm track. They do not enable optional components you never installed or change another app's Python. Errors stop remaining updates. Recovery copies are kept; ROCm retains its verified environment until new GPU checks pass. Allow extra disk space.

## Controllers

Open **Controllers** in the sidebar for the hand/controller compatibility check, feature switches and modes. Installation stays on **First-time setup**; the shared bottom bar starts/stops selected features.

**Experimental hands + controllers** tracks real fingers while holding Touch Pro. Pose, buttons and haptics still come from controllers. It is **Virtual Desktop only**, separate from the face module and off by default.

### Turn on these settings first

| Where | Required setting |
| --- | --- |
| Quest **Settings > Hands & eyes** | **Hand tracking: On.** **Automatically switch between controllers and hands: On.** |
| **Singularity > AIO Tweaks** | **Simultaneous Hands & Controllers: On.** Some versions say **Simultaneous Hands Controllers**. |
| Virtual Desktop on the Quest **> Streaming** | **Forward tracking data to PC: On.** **Track controllers: On.** Accept the tracking-data permission. |
| VRChat **Main Menu > Controls** | **Avatars Use Finger Tracking: On.** Use the SteamVR version of VRChat. |

For the initial check, keep Virtual Desktop **Emulate Index controllers** off. Turn off Singularity's separate **Frida Server** so it does not compete with Qpro's helper.

> [!WARNING] Auto-switch is a separate prerequisite
> Quest hand tracking and Singularity's simultaneous switch do not replace **Automatically switch between controllers and hands**. Qpro does not enable or verify that menu option for you; check it in the headset.

Before starting Qpro hands, connect Virtual Desktop and verify ordinary empty-hand tracking with controllers set down. If needed, tap the controllers together twice to enter hand mode. Continue only when fingers move in VRChat.

Next: [install and start the feature](#set-up-hands-controllers). USB ADB alone does not start a Virtual Desktop VR session.

## Set up hands + controllers

First complete [the prerequisite settings](#controllers). Keep all those switches enabled while using this feature.

### Install once

1. Finish **Install runtime**. **Close SteamVR** and wait for it to exit.
2. Open **First-time setup > Optional hands and controllers > Show hands setup and uninstall**, then press **Install hand/controller components**. Keep the Hub open until **Activity** reports completion.
3. Connect through Virtual Desktop, then launch SteamVR from its menu. Reopen VRChat.

Installation downloads verified Frida 17.18.0 into Qpro's private environment and registers its add-on. Repeat only when the Hub asks for repair or update.

For thumb-rest sessions, Qpro hash-checks the installed add-on DLL against this build before input workers start. If it differs, **close SteamVR**, reinstall the controller add-on from the current build, then reopen SteamVR through Virtual Desktop. Matching files on disk is a precheck, not proof that SteamVR loaded that driver.

### Start each session

1. Choose **Virtual Desktop** under **Facial tracking > Your session > Streaming app**. Open **Controllers** and press **Check hand/controller compatibility**.
2. Green **Hand/controller prechecks passed** means versions/settings passed; it does not confirm live input or performance. Red is blocked; yellow is unchecked/waiting. Follow the result's next step.
3. Turn on **Experimental hands + controllers** and press **Start tracking**. Leave **Experimental Touch Pro thumb-rest input** off for this first finger check; it is separate.
4. Hold the controllers with your hands visible to the headset cameras. Move individual fingers and check **both** hands in VRChat while movement and buttons still work.

> [!WARNING] Experimental performance
> A live test showed optical fingers with held controllers but caused a substantial FPS drop. The current revision still needs a sustained check. If the game lags or SteamVR becomes unstable, stop and leave this option off.

**Only controller gestures?** Recheck Quest **Automatically switch between controllers and hands**, hand tracking, Singularity's simultaneous switch and VRChat finger tracking. Verify ordinary empty-hand tracking, then retry with fingers in camera view.

Finish with **Stop tracking** and wait for confirmed cleanup. [The next page](#hands-checks-and-thumb-rest-input) explains readiness, compatibility failures and removal.

## Hands checks and thumb-rest input

### Current compatibility boundary

**Headset Virtual Desktop 1.34.22.0** and checked PC drivers, including **1.34.23 beta PC driver**, are admitted. **Headset 1.34.23.0 is not admitted.**

Unsupported versions or layouts block startup; reinstalling cannot add support. Follow Activity for settings warnings; do not edit headset preferences by hand.

### Confirm and stop

**Live optical fingers received** / `HANDS_READY` confirms fresh optical data routed to **at least one** held controller. Check **both** real hands in VRChat; button gestures do not prove optical fingers.

**Thumb-rest input received** / `TOUCHPAD_READY` means selected-side sensor packets reached PC loopback. `CONTROLLER_DRIVER_STATUS` reports settings, adopted sides, input calls and errors. Neither proves bindings or avatar movement.

**Stop tracking:** wait for confirmed hands restoration and fresh disabled/neutral driver output, without held mouse presses or errors. Reader exit alone is insufficient. If cleanup is unconfirmed, keep input off and follow Activity's recovery steps.

### Experimental Touch Pro thumb-rest input

Sensor profile **51503870024400340** has a live check on one headset; other firmware remains unverified. Directly below the two feature switches, choose **Thumb-rest mode** and **Apply mode to**: **Left (L)**, **Right (R)** or **Both**. This affects extra inputs only; keep both controllers awake for the initial check.

| Thumb-rest mode | What it does |
| --- | --- |
| Trackpad | Follows your thumb's position. |
| Relative joystick | Centers on first touch; lift your thumb to reset. |
| Swipe | Scrolls with movement speed and a short glide on release. |
| Desktop mouse | Moves the Windows pointer; pressing the rest gives a left-click. With Both, movement is summed and either thumb can hold the click. |

The **Swipe sensitivity** slider appears only in Swipe mode: **25%–200%**, in 25% steps, with **100%** as the default. Lower scrolls more slowly; higher scrolls faster. **Stop tracking**, adjust, then **Start tracking**. This is a sensitivity preference, not sensor calibration.

## SteamVR scrolling and controller recovery

The installed add-on registers extra controls when SteamVR discovers the controllers, before Qpro tracking starts. Their values stay neutral while tracking is off. Reopen SteamVR through Virtual Desktop after installing the add-on.

### Extra application actions

Assign **Qpro thumb-rest surface (extra)** to additional game actions in SteamVR's binding editor. **SteamVR binding instructions** in Qpro explains this. No premade game assignments replace your bindings; actions depend on each application's support.

### Dashboard and VRChat menus

**Dashboard scrolling:** setup adds a scroll source, preserving ordinary controls. Personal dashboard choices stay selected. Use **Swipe** during tracking; other modes and Stop send neutral scroll. If needed, select **Qpro Companion thumb-rest scrolling** manually as Activity explains.

**VRChat menu scrolling:** setup extends a recognized binding. Automatic selection requires an existing binding selection the installer can verify; otherwise select the prepared binding manually. Preparation is not proof of live scrolling. Unrecognized formats or ambiguous choices stay unchanged with a warning.

On the checked headset, **Both / 100%** scrolled well in Dashboard and VRChat, with ordinary buttons working. Desktop mouse movement also felt smoother. Stop cleared scrolling and restored neutral output without errors or remaining workers. Other firmware and app bindings need their own checks.

### Stop or remove the add-on

Press **Stop tracking** and wait for Activity to confirm cleanup. Keep the app open if restoration is unconfirmed. Desktop mouse should release its click, and ordinary controls should keep working.

To remove thumb-rest input, close SteamVR. Open **First-time setup > Optional hands and controllers**, expand **Show hands setup and uninstall**, then press **Uninstall controller add-on**. Reopen SteamVR afterward. Recovery copies stay; hands components and Singularity are unchanged. See [the controller guide](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/blob/main/src/CONTROLLER_INPUT.md).

## Return to ordinary tracking

Stopping a session, removing a PC module and disabling headset gaze are different actions.

| What you want | What to do |
| --- | --- |
| Native cheek puff/suck | **Stop tracking**. Disable the individual cheek switches for native values during a session too. |
| Native eyebrows | Turn **Adjust eyebrow movement** off. |
| Remove Qpro's whole face mapping | Stop, close VRCFaceTracking and **Uninstall Qpro module**. Reopen with exactly one official source module. |
| Restore a recorded Hub gaze session | Stop, then **Recover Qpro gaze** and read its result. |
| Remove Magisk independent gaze | Disable that gaze module in Magisk and reboot. |

### Uninstall Qpro module

On **First-time setup**, open **Show module uninstall** on **Install your VRCFaceTracking module**. Press **Uninstall Qpro module**, confirm and wait for **Setup step complete** before reopening VRCFaceTracking.

Verified Qpro modules are removed. An official Virtual Desktop module saved by an older installer can be restored if verified; otherwise install the official source module through VRCFaceTracking. Models/recordings remain saved.

> [!NOTE] The tongue model stays on the PC
> It outputs only through a running Qpro camera process and module. Persistent eyebrow/smirk mapping belongs to the installed PC module; Magisk gaze is a separate headset modification.

If recovery refuses, retain the error and exact firmware build. **Reset legacy gaze** chooses normal gaze for an older unrecorded session; it cannot restore an unknown state or support a new engine.

## Setup problems

Read the first specific error in **Activity**. An exit code is a result, not the cause.

| What you see | Next step |
| --- | --- |
| Install runtime seems stuck | Keep the Hub open. Look for PowerShell PID, phase and still-running messages. With no script message, check Windows Security for blocked PowerShell and send the log. |
| Code 1 with VCRUNTIME, native DLL or WinError 126/1114 | Install/repair [Microsoft's latest x64 Visual C++ Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170#latest-supported-redistributable-version), then retry. |
| ModuleNotFoundError | Follow the missing-package/file repair message. Extract the whole named release ZIP. It is not automatically a Visual C++ issue. |
| Module missing or wrong source | Close VRCFaceTracking and its module process, select **Streaming app**, install its Qpro module and reopen. |
| No Steam Link expressions | Check OSC, eye/face sharing and **9015**. Remove competing source modules and restart VRCFaceTracking. |
| ROCm missing torchgen or another import | Use **Verify / repair ROCm 10.1** or **Repair ROCm 10.1**. An import failure alone does not mean an unsupported GPU. |
| Integrated AMD GPU appears | Qpro requires a mapped **discrete** Radeon. Check its driver and Windows detection; otherwise use CPU/NVIDIA runtime. |

> [!CAUTION] Do not delete the whole Qpro data folder for code 1
> `%LOCALAPPDATA%\QproFaceTracking` can contain runtime and recovery data. Use the specific repair action and keep the complete error.

USB failures use [the USB checklist](#usb-cable). Pairing `protocol fault` needs a fresh six-digit code and temporary port, not `:5555`.

## Tracking problems and support

Check source, module, camera, model and avatar before retraining.

| Symptom | What to check |
| --- | --- |
| Tongue drops out or loses accuracy | Check loaded model, output state and freshness in **Activity**. Stale frames need performance attention; fresh hidden poses may need refinement. Try a known working model. |
| Both avatar cheeks move together | Confirm the individual cheek switch and a running Qpro session. If VRCFaceTracking separates the sides, check the avatar's cheek parameters/blendshapes. |
| Pupils jump or lag | Lower **Pupil response**, check backend/frame age, close unused previews and reduce other camera work. |
| Hands lag or only button gestures | Stop if FPS drops. Recheck [the required switches](#controllers), including Quest auto-switch, exact Virtual Desktop version and `HANDS_READY`; inspect both real fingers. |

### Send useful diagnostics

Open **Activity**, then **Copy diagnostics** or **Save log…**. Include the first error and nearby lines, exact Quest build, GPU/driver, USB/wireless, streaming app and ZIP/version. Recordings are personal data; share only if you choose to.

Use [GitHub Issues](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/issues) or [community Discord](https://discord.gg/ghvuJTpRu4), which is not owned by Fwooffy. Green means normal, yellow a warning and red an error; include the explanation, not just its colour.

### Credits and full instructions

Based on [Qpro-Enhanced-FT by n0tmast3r](https://github.com/n0tmast3r/Qpro-Enhanced-FT/releases). Fwooffy maintains this edition and wrote the original root guide; glorpette made and hosts its GitHub version. Steam Link mapping references [danwillm](https://github.com/danwillm/VRCFT-SteamLink) and [LinkFT](https://github.com/ykeara/LinkFT). More detail: [the text setup guide](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/blob/main/src/RELEASE_INSTRUCTIONS.md).
