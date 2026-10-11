# QproFaceTracking PDF companion

<!-- Concise print edition. Keep control names and behavior aligned with RELEASE_INSTRUCTIONS.md and the Hub. Each H2 begins a page; the builder checks that it fits. -->

## Your first session

Use these three cards on **First-time setup**. Optional features can wait until ordinary face tracking works.

1. **Connect your Quest Pro.** Start with **USB cable**, approve USB debugging in the headset and allow **Shell / ADB Shell** in Magisk.
2. **Install the PC runtime.** Press **Install runtime**. Keep the Hub open while its private Python and tracking libraries download. Wait for completion in **Activity**.
3. **Install your VRCFaceTracking module.** Close VRCFaceTracking, choose **Streaming app**, install the matching Qpro module and reopen VRCFaceTracking.

> [!TIP] Check the source before adding features
> Connect through Virtual Desktop or Steam Link and confirm that mouth expressions and blinks move in VRCFaceTracking's preview. An installed module or green app status does not establish live tracking.

Then open **Live tracking**, choose your features and press **Start tracking**. Finish with **Stop tracking** and wait for cleanup in **Activity**.

### Find your next step

| Topic | Jump to the guide page |
| --- | --- |
| Required apps and download | [Before you start](#before-you-start) |
| Streaming app settings | [Virtual Desktop and Steam Link](#virtual-desktop-and-steam-link) |
| Connect and install | [USB cable](#usb-cable), [Wireless ADB](#wireless-adb), [Runtime and Qpro module](#runtime-and-qpro-module) |
| Features and calibration | [Live tracking](#live-tracking), [Cheek puff and cheek suck](#cheek-puff-and-cheek-suck), [Personalize](#personalize) |
| Optional setup and help | [Independent eye gaze](#independent-eye-gaze), [AMD ROCm](#amd-rocm), [Hands and controllers](#hands-and-controllers), [Setup problems](#setup-problems) |

## Before you start

The **Hub** is `QproFaceTracking.exe`. **ADB** connects it to your Quest. The **Qpro module** supplies your selected face-tracking source to VRCFaceTracking.

### On the Quest Pro

- A **rooted Meta Quest Pro** with Developer Mode enabled. If it is not rooted, first check exact firmware support in [Singularity](https://github.com/Lumince/singularity) and follow [Fwooffy and glorpette's root guide](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md).
- In **Settings > Hands & Eyes**, enable **Eye Tracking** and **Natural Facial Expressions**. Run eye calibration if offered. Allow your streaming app to use eye and face data.
- Keep the headset awake. Approve USB debugging and Magisk **Shell / ADB Shell** root access when prompted.

### On the PC

Install **SteamVR**, the [latest VRCFaceTracking from Steam](https://store.steampowered.com/app/3329480/VRCFaceTracking/) and your streaming app's PC software. Setup needs internet access and several gigabytes of free space.

1. Download **QproFaceTracking.V3.0.0.zip** from [Releases](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases) when published, or use the supplied release candidate ZIP.
2. Right-click the ZIP in File Explorer and choose **Extract All**. Keep `QproFaceTracking.exe`, `Helpers` and `QproRuntime` together.
3. Open `QproFaceTracking.exe`. For OneDrive, first choose **Always keep on this device** for the whole extracted folder.

> [!NOTE] Independent gaze is optional
> You do not need a gaze Magisk module or the Hub's gaze preparation for ordinary face tracking, tongue tracking or pupil dilation.

ADB is bundled. **Install runtime** supplies private Python, so no global Python installation is needed. GitHub's automatic **Source code** archives are not the ready-to-run app.

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
4. If root access is unavailable, allow **Shell / ADB Shell** in Magisk. Open **Live tracking > Your session > Show connection and camera settings**, then **Refresh connection status**.

> [!TIP] You should not normally need to unplug USB
> The USB selection stays in the app's settings. After reopening the Hub, keep the Quest awake and connected. If it stalls, stop tracking and training, then press **Reconnect USB** in **First-time setup**.

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

### Install your VRCFaceTracking module

1. **Close VRCFaceTracking** and wait for its module process to exit.
2. Choose **Virtual Desktop** or **Steam Link** under **Streaming app**.
3. Press **Install Virtual Desktop module** or **Install Steam Link module**. Only the selected source's button is available. Wait for completion in **Activity**.
4. Reopen VRCFaceTracking with the source connected. Check that ordinary expressions move in its preview.

> [!NOTE] What a module check proves
> **Installed · input unchecked** means the installed folder, module card and DLL match the app. **VRCFaceTracking: Running** only means the app is open. Neither replaces a live expression check.

Junctions, symlinks and Windows Cloud Files are supported. For OneDrive, keep the whole app available offline. Recovery copies go under `%LOCALAPPDATA%\QproFaceTracking\module-recovery`; **Activity** gives the exact path.

If the module is damaged or from the wrong source, follow **Activity**, close VRCFaceTracking and reinstall it. **Check headset compatibility** and **Show firmware details** are optional diagnostics, not requirements for ordinary tracking.

## Live tracking

Start the streaming app, SteamVR and VRCFaceTracking. Confirm live face input, then open **Live tracking**.

| Section | What it controls |
| --- | --- |
| Your session | **Streaming app**, connection/readiness status and **Show connection and camera settings**. |
| Eyes | **Independent eye gaze + convergence**, **Experimental relative pupil dilation (eye cameras)** and **Adjust eyebrow movement**. |
| Lower-face tracking | **Lower-face model**, **Experimental tongue tracking**, camera cheeks and native cheek controls. |
| Hands and controllers | Optional experimental fingers and thumb-rest input. |

1. Choose your features. For tongue output, select the bundled v8 demo or your personal **Lower-face model** and enable **Experimental tongue tracking**. Selecting a model alone does not enable its output. With Magisk gaze active, keep Hub gaze off.
2. Press **Start tracking**. **Activity** confirms requested outputs, the loaded model and GPU or CPU backend.
3. Use **Stop tracking** before changing a model or camera setting, then start again.

### Camera windows are optional

**Preview tracking cameras** defaults off on a fresh setup. Find it beside **Camera FPS cap** under **Your session > Show connection and camera settings**. Saved choices persist. Hiding a preview does not stop tracking; guided capture still opens its pose window.

> [!NOTE] Stop restores native cheeks
> Tongue, camera cheek and pupil outputs stop. Individual cheek puff/suck also stop, restoring native cheeks. Eyebrow and built-in smirk adjustments can remain through the installed module; Magisk modules stay active.

Wait for cleanup in **Activity** before closing. The tongue model runs on the PC, not on the headset.

## Eyes

Ordinary eye tracking comes from the streaming app. Enable only the extra features you want.

### Independent eye gaze + convergence

This starts off. If the **Quest Pro Independent Eye Gaze** Magisk module is enabled, leave it off and skip **Check gaze setup** and **Prepare gaze**. The next page covers the temporary Hub method.

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

Choose one method. Independent gaze is optional; tongue, camera cheeks and pupils do not require it.

| Method | What to do in the Hub |
| --- | --- |
| Enabled gaze Magisk module | Keep **Independent eye gaze + convergence** off. Skip **Check gaze setup** and **Prepare gaze**. |
| Temporary Hub method | Disable the gaze Magisk module and reboot first. Use **Check gaze setup**, then **Prepare gaze**. Enable Hub gaze only if its checks pass. |

The Magisk route lists **Quest Pro Independent Eye Gaze** and **Magisk OverlayFS** in Singularity **Apps/Modules > Magisk repo**. Follow its exact firmware requirements and reboot prompts.

For the Hub route, open **First-time setup > Optional independent gaze > Show gaze setup**. The Hub reads that headset's eye model and engine and prepares a temporary model on the PC. **You do not need to supply an engine file.** A build number alone does not establish engine compatibility.

> [!WARNING] Wait for the service restart
> Applying/restoring Hub gaze briefly freezes the headset and can interrupt positional tracking while Meta trackingservice restarts. Wait for **Activity** to confirm recovery. Do not combine methods to bypass a failed compatibility check.

### Show gaze recovery tools

On the optional gaze card, open **Show gaze setup**, then **Show gaze recovery tools**:

- **Recover Qpro gaze** restores the previous state recorded for a verified Qpro session. It does not remove unrelated modifications.
- **Reset legacy gaze** offers a confirmed normal gaze selection for an older session without a record. It refuses active gaze Magisk modules or unverified overlays; it does not overwrite firmware.

**Stop tracking** reverses recorded Hub changes, not Magisk modules. To return from Magisk gaze to ordinary gaze, disable that module and reboot.

## Cheek puff and cheek suck

These controls in **Live tracking > Lower-face tracking** adjust the streaming app's cheek signals. They are separate from camera cheek prediction.

### Individual cheek puff

It starts on with **1/0**. Choose **Cheek puff style**:

| Style | Response |
| --- | --- |
| Calibrated | Smooth 0-1 strength, including light puffs. Uses the source's personal profile or its bundled baseline. |
| 1/0 | A clear one-sided puff becomes 1 on that side and 0 on the other. Deliberate two-cheek puffs still move both. |
| Balanced | Gentler separation that keeps changes in strength without a calibration profile. |

Developer baselines were measured on one Quest Pro through both streaming apps. They are starting points, not universal fits. Use **Calibrate cheek puff** for a weak or overly strong response.

### Individual cheek suck

This uses separate left/right cheek-suck signals, **not negative puff values**. It starts on with **Strong individual (1/0)**. Choose **Balanced** for a softer response, or turn it off for native values.

> [!TIP] Native cheeks when tracking is off
> Adjustments apply only after **Start tracking**. **Stop tracking**, closing the Hub or a Hub crash restores native cheeks. Saved styles/calibration remain for next time. Native cheek adjustments alone do not need camera tracking.

With camera cheeks off, turning **Individual cheek puff** off passes original puff values. If VRCFaceTracking separates the sides but the avatar does not, check its cheek parameters and blendshapes.

## Calibrate cheek puff

This short three-pose calibration changes native cheek response. It does not train cameras or require tongue recording.

1. Wear the headset with your streaming app connected. Its matching Qpro module must be tracking in VRCFaceTracking. Qpro camera tracking need not be running.
2. On **Live tracking > Lower-face tracking**, press **Calibrate cheek puff** beside **Cheek puff style**. Wait for **Live cheek feed is ready**.
3. Relax both cheeks. Press **Capture relaxed cheeks** and hold for three seconds.
4. Puff only your own **left** cheek. Keep the right relaxed, press **Capture left cheek** and hold for three seconds. Relax afterward.
5. Puff only your own **right** cheek. Keep the left relaxed, press **Capture right cheek** and hold for three seconds.

> [!NOTE] Use your own left and right
> Left is the side of your left hand. Keep your jaw comfortable and avoid smiling. Follow the written pose label rather than treating the camera image as a mirror.

### Saving and retrying

After all three poses pass, the window saves your profile, selects **Calibrated** and enables **Individual cheek puff**. Its input is the module's **Balanced** cheek strengths. Press **Start tracking** to apply your response.

Repeat weak, unstable or two-cheek samples. Fix a paused/wrong-source feed first. Closing before completion keeps the existing profile. Virtual Desktop and Steam Link have **separate personal profiles**.

Earlier three-pose profiles still work. Four-pose test profiles stay saved but are not used; the bundled baseline is used until a new three-pose calibration passes. Close VRCFaceTracking before updating its module.

## Camera cheek puff

Camera cheeks predict separate strengths from mouth images. This is an experimental alternative to native cheek adjustment.

### Select a combined model

The bundled **Developer tongue + cheeks · highly experimental · v13** is an optional developer v8 copy with cheek outputs trained on one wearer. Its original tongue weights are unchanged. Another face or fit may need personal recording.

1. Choose a combined model under **Live tracking > Lower-face tracking > Lower-face model**.
2. Enable **Camera cheek puff (experimental)**. Selecting the model alone does not enable cheek output.
3. Stop and restart tracking. Read the cheek-source message and **Activity** to confirm output ON and the intended model.

> [!WARNING] Native calibration does not train camera cheeks
> **Calibrate cheek puff** only calibrates the source's native values. Camera cheeks do not use that profile or **Cheek puff style**; their strengths come from the model.

### Train a combined copy

**Quick refinement** and **Full dataset** include cheek cards. For cheeks alone, open **Personalize > Experimental tongue + cheeks model > Show advanced cheek training**. Choose **Parent tongue model**, press **1. Record cheek camera poses**, select the recording under **Completed cheek camera dataset**, then press **2. Train tongue + cheeks copy**.

This freezes the tongue model while learning cheeks; the original stays saved. Check the new copy with fresh relaxed, left, right, both and gentle puffs before relying on it.

Camera cheeks can run with tongue off. Turning them off or losing their feed returns to the selected native cheek style while Qpro runs. **Stop tracking** restores original cheeks.

## Personalize

Open **Personalize** in the sidebar; its page heading is **Lower-face calibration**. Record, train, then select the result.

| Capture mode | What it records |
| --- | --- |
| Quick refinement | **37 cards**, about **15-30 minutes**: 16 tongue cards plus 21 cheek camera cards. |
| Focused diagonals + facial hair | **22 tongue-only cards**, about **15-30 minutes**. Diagonals and matched hidden/visible poses. |
| Full dataset | **79 cards**, about **60-120 minutes**: 58 tongue plus 21 cheek cards. Take breaks. |

1. For Quick or Focused, first choose the model to extend under **Live tracking > Lower-face tracking > Lower-face model**. Full creates a new personal model instead.
2. Quick uses **1. Record refinement** and **2. Train personalized copy**. **Show focused and full captures** reveals the other modes.
3. Hold each pose. Press **SPACE** once per still; save at least the displayed minimum before **ENTER** advances.
4. Under **Recording to train**, choose your completed recording and press that mode's training button. Wait for completion in **Activity**.
5. Select the result under **Lower-face model**, then stop and restart tracking.

> [!TIP] Capture varied stills, not a moving pose
> Hold one pose per still. Vary jaw/headset position slightly between stills. Include hidden tongue, diagonals, gentle puffs and non-puff expressions. Cheek labels use the requested pose, so a weak native signal does not block them.

Tongue cards need fresh VRCFaceTracking input, even while values remain steady. Held-out training checks use the same recording; a fresh recording or live test still matters.

## Models and facial hair

The bundled starting model is **Developer-trained tongue model · v8 demo**. Model names can change after renaming/import; choose your intended entry under **Lower-face model**.

### Mustachio

**Mustachio · highly experimental · v9** combines developer v8 with a focused capture from one bearded and moustached wearer. Broader facial-hair and independent clean-shaven tests are pending; it may work worse for some people.

Select it on **Live tracking**, then restart. Contrast processing is automatic. Quick/Focused can extend it, keeping the experimental label after export/import or renaming. Full makes a new personal model.

> [!WARNING] Hair cannot be removed by training
> Keep facial hair/bandages as normally worn. Check both camera views during visible poses and record hidden poses too. Contrast processing cannot reveal a completely covered tongue or invent hidden pixels.

### Model manager

Export backups and import working **V2.0 or newer** `.qptonguemodel` files. Pre-V2.0 models need recapture/retraining. Exports contain weights, not camera recordings. You can refine an imported model with **new** recordings; exports cannot reconstruct its original captured poses.

**Activity** names the loaded model. Use **Lower-face tracking > Show camera settings > Motion smoothing** to soften extension/retraction. **Tongue visibility > Camera only** can help native-gate flicker but is not a guarantee for obscured poses.

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

The Hub checks for a newer stable release when it opens. Installation waits for your click.

1. Click **Check updates** or **Update available** at the top right to read release notes.
2. Stop tracking, then click **Update**. The ZIP checksum and packaged files are verified. Cancelling the download leaves the current app unchanged.
3. Click **Restart and update**. App files are replaced in the **same folder**, then the Hub reopens. Start tracking yourself when ready.
4. Open **Components**, review its changes and click **Update components** for installed modules/private runtimes that need updating.

> [!TIP] Your setup stays with you
> Models, recordings, enabled options and connections are preserved. A colliding bundled model does not replace your existing pair. Updates do not start tracking automatically.

### Update choices

- Turn **Check automatically when the Hub opens** off for manual checks.
- **Include prereleases (test builds)** is off by default and remembered. A release needs a newer three-part version, such as `v3.0.1`. Same-version ZIPs are not offered; suffix tags such as `v3.0.1-beta.1` need manual download.
- Without a checksum or a compatible package, use **View release on GitHub** for manual download.

### Components and recovery

Close VRCFaceTracking and wait for its module process to exit before a module update. Stop tracking/training before any component update. Reopen VRCFaceTracking afterward.

Updates retain the streaming app and ROCm track. They do not enable optional components you never installed or change another app's Python. Errors stop remaining updates. Recovery copies are kept; ROCm retains its verified environment until new GPU checks pass. Allow extra disk space.

## Hands and controllers

**Experimental hands + controllers** tracks real fingers while holding Touch Pro. Pose, buttons and haptics still come from controllers. It is **Virtual Desktop only**, separate from the face module and off by default.

1. Enable Quest hand tracking in **Settings > Hands & Eyes** on the checked firmware. With controllers down, check that empty hands track.
2. In **Singularity > AIO Tweaks**, enable **Simultaneous Hands & Controllers** (some versions say **Simultaneous Hands Controllers**). Leave it on. Disable Singularity's separate **Frida Server** to avoid a competing helper.
3. Finish **Install runtime**. **Close SteamVR** and wait for exit. Open **First-time setup > Optional hands and controllers > Show hands setup and uninstall**, then **Install hand/controller components**.
4. In headset Virtual Desktop **Streaming**, enable **Forward tracking data to PC** and **Track controllers**; leave **Emulate Index controllers** off for the first check. Accept the tracking permission.
5. Connect through Virtual Desktop and launch SteamVR from its menu. Use SteamVR VRChat with **Main Menu > Controls > Avatars Use Finger Tracking** on.
6. Use **Live tracking > Hands and controllers > Check hand/controller compatibility**. If admitted, enable **Experimental hands + controllers** and press **Start tracking**. Leave thumb-rest input off initially.

> [!WARNING] Experimental performance
> A live test showed optical fingers with held controllers but caused a substantial FPS drop. The current revision still needs a sustained check. If the game lags or SteamVR becomes unstable, stop and leave this option off.

Installation downloads verified Frida 17.18.0 into Qpro's private environment and registers its add-on. It does not enable your Quest, Singularity or VRChat switches. USB ADB alone does not start a Virtual Desktop VR session.

## Hands checks and thumb-rest input

A version check is the first gate. Moving real fingers with both controllers is the live check.

### Current compatibility boundary

The admitted headset version is **Virtual Desktop 1.34.22.0**, with an exact checked PC driver. The original and inspected **1.34.23 beta PC driver** are admitted separately. **Headset Virtual Desktop 1.34.23.0 is not yet admitted.**

Unsupported versions, unreadable settings or controller layouts refuse startup. Reinstallation cannot add a missing profile. Preliminary hand preferences can appear inactive before simultaneous mode starts; known inactive values warn, then Qpro requires fresh optical input. Do not edit preferences by hand.

### Confirm and stop

`HANDS_READY` in **Activity** reports optical data routed to **at least one** held controller. Check **both** hands: individual fingers should move while pose/buttons work. Controller-button fist, point and open gestures are not optical proof.

Press **Stop tracking** and wait for confirmed cleanup. If restoration is unconfirmed, keep the feature off, close SteamVR, restart headset Virtual Desktop and reopen SteamVR. Keep the log.

### Experimental Touch Pro thumb-rest input

This separate option adds SteamVR trackpad input. Its first sensor profile is limited to build **51503870024400340** and has not been tested live. Open **Show hand and controller details** for **Thumb-rest mode**: **Trackpad**, **Relative joystick**, **Swipe** or **Desktop mouse**. Mouse mode moves the Windows pointer. Configure SteamVR bindings for the app.

To remove it, close SteamVR, press **Uninstall controller add-on** on **First-time setup**, then reopen SteamVR. A recovery copy stays. This removes the thumb-rest add-on, not the hands runtime or Singularity's switch. See [the controller guide](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/blob/main/src/CONTROLLER_INPUT.md).

## Return to ordinary tracking

Stopping a session, removing a PC module and disabling headset gaze are different actions.

| What you want | What to do |
| --- | --- |
| Native cheek puff/suck | **Stop tracking**. With camera cheeks off, disable the individual cheek switches for native values during a session too. |
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
| Both camera cheeks work but one side does not | Confirm a combined model and **Camera cheek puff (experimental)**. Restart and check output ON. Another face or fit may need camera cheek recording. |
| Native cheek calibration waits/rejects | Check source and **Live cheek feed is ready**. Keep the headset on, avoid smiling and repeat weak/unstable samples. |
| Pupils jump or lag | Lower **Pupil response**, check backend/frame age, close unused previews and reduce other camera work. |
| Hands lag or only button gestures | Stop if FPS drops. Recheck required switches, exact Virtual Desktop version and `HANDS_READY`; inspect both real fingers. |

### Send useful diagnostics

Open **Activity**, then **Copy diagnostics** or **Save log…**. Include the first error and nearby lines, exact Quest build, GPU/driver, USB/wireless, streaming app and ZIP/version. Recordings are personal data; share only if you choose to.

Use [GitHub Issues](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/issues) or [community Discord](https://discord.gg/ghvuJTpRu4), which is not owned by Fwooffy. Green means normal, yellow a warning and red an error; include the explanation, not just its colour.

### Credits and full instructions

Based on [Qpro-Enhanced-FT by n0tmast3r](https://github.com/n0tmast3r/Qpro-Enhanced-FT/releases). Fwooffy maintains this edition and wrote the original root guide; glorpette made and hosts its GitHub version. Steam Link mapping references [danwillm](https://github.com/danwillm/VRCFT-SteamLink) and [LinkFT](https://github.com/ykeara/LinkFT). More detail: [the text setup guide](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/blob/main/src/RELEASE_INSTRUCTIONS.md).
