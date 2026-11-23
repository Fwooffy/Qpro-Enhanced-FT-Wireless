# Qpro Companion V3.0.4: text setup guide

> **For native streaming, use a Meta Quest Pro with face sharing enabled in Virtual Desktop or Steam Link, and enable OSC in VRChat.** Camera capture, temporary gaze and headset controller tools require root. If you need these features, check your exact firmware in [Singularity](https://github.com/Lumince/singularity) before following the [Quest Pro root guide](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md).
>
> **If you used a QproFaceTracking version before V2.0, record and train your tongue model again.** Do not import a pre-V2.0 model and assume it will work correctly. Working V2.0 or newer models can be exported and imported into V3.0.4. **Developer tongue model** is the default starting point on a fresh setup; updates keep personal/imported selections and replace retired bundled choices with the new developer model.

> **This guide previews V3.0.4.** Virtual Desktop and Steam Link are available under **Streaming app**. The two bundled tongue choices are **Developer tongue model** and **Mustachio**. Mustachio remains highly experimental; start with Developer tongue model or your existing working model.

Start with the headset's eye tracking, then set up the PC software. **Qpro Companion** is the app; its main window is called the **Hub**. **ADB** is the connection it uses to talk to your Quest. Qpro sends tracking results directly to VRChat; Virtual Desktop or Steam Link supplies native expressions.

## Supported VRChat avatars

Enable **Action Menu > OSC > Enabled**, then use **Check VRChat connection** in the Hub. OSCQuery discovers the local game and refreshes the avatar's output. If local discovery fails, Qpro checks a local HTTP fallback and verifies VRChat's identity before using it. No fixed VRChat port needs entering.

### Native face tracking

For avatars with **Use All Tracking Keys** enabled, VRChat advertises native full-face support. Qpro can then send the **102-byte `/tracking/face/v1`** format, including supported face shapes, gaze, blink and pupil values. The avatar still needs matching face animations; native support does not add missing blendshapes.

### Existing avatar parameters

On other avatars, Qpro writes only recognized, writable face parameters advertised through OSCQuery. Supported mappings include direct Unified Expressions and prefixed **v2** names, signed combined axes, separate eyes/brows, tracking-active booleans and supported binary bit suffixes (**1**, **2**, **4**, **8** and higher, plus **Negative**).

Arbitrary custom names and packing schemes are not automatically supported. VRChat-controlled **UserEyeTracking** and **UserFaceTracking** are read-only. The documented built-in gaze and blink endpoints can also serve verified VRChat clients when omitted from OSCQuery. Explicit read-only or incompatible types and custom gaze/eyelid equivalents suppress those fallbacks. Eye-only output does not supply missing face expressions.

> [!NOTE] Verify your avatar
> A discovered game is not proof of compatible parameters or moving expressions. Check **Activity** and test your current avatar. Missing animations or unsupported names need avatar changes; installing VRCFaceTracking is not a required setup step.

Keep other live face senders stopped while Companion owns the output. Prior live tests used the VRCFaceTracking route; direct output still needs live headset/avatar acceptance.

For avatar creators: [VRChat face OSC](https://creators.vrchat.com/avatars/face-tracking/osc/) and [Unified Expressions parameters](https://docs.vrcft.io/docs/tutorial-avatars/tutorial-avatars-extras/parameters).

## 1. Enable headset eye and face tracking

1. On the rooted Quest Pro, open **Settings > Hands & Eyes**. Turn on **Eye Tracking** and **Natural Facial Expressions**, then run eye calibration if offered. If these are already enabled, leave them on. Keep Developer Mode enabled.

### Optional: independent gaze through Magisk

Independent gaze is not required for ordinary face tracking, tongue tracking or pupil dilation. If you want the Magisk gaze method:

1. If **Magisk OverlayFS** is not installed yet, open **Singularity > Apps/Modules > Magisk repo** and install it. Follow any reboot prompts. If it is already enabled in Magisk, you can skip reinstalling it.
2. In the same Singularity menu, install **Quest Pro Independent Eye Gaze** if it is not installed yet. Reboot if prompted, then check in Magisk that both modules are enabled. This is the headset-side independent gaze route.
3. Once your selected headset streaming app and VRChat are ready in section 3, check that left and right gaze and convergence move correctly. The Magisk route has been reported working on **Horizon OS v2.7**, but exact firmware builds can behave differently.

**Already using the Independent Eye Gaze Magisk module?** It already provides independent gaze. The Hub checks the connected headset without changing its modules. When an active gaze module is detected, **Independent eye gaze + convergence (provided by Magisk)** stays off and disabled, along with **Check gaze setup** and **Prepare gaze**. Skip those buttons. You can still use Qpro's tongue, pupil dilation and eyebrow features separately. If a Qpro gaze session is already running when a conflict is detected, press **Stop tracking** and wait for cleanup before continuing.

**Use one independent gaze method at a time.** While the Magisk module is active, leave **Independent Eye Gaze** unchecked in the Hub. If you want to try the Hub's temporary gaze method instead, follow **Check gaze setup** and **Prepare gaze** in section 5 before turning on **Independent Eye Gaze**. Applying that method briefly freezes the headset while its tracking service restarts; wait for Activity to confirm it is ready. **Stop tracking** only reverses the Hub's changes; it does not disable a Magisk module.

Before switching from the Magisk gaze method to the Hub's method, disable the gaze module in Magisk and reboot the headset. Then use **Check USB connection** or **Check headset compatibility** to refresh detection; **Check gaze setup** stays disabled until the active module is no longer detected. A failed connection or scan does not clear a previously detected module. The same disable-and-reboot step is needed if you want ordinary headset eye tracking again. The Hub leaves Magisk modules alone.

## 2. Download and open the Hub

1. Use the supplied **QproCompanion.V3.0.4.zip**, or download the ready-to-run Companion asset from the [Releases page](https://github.com/Fwooffy/Qpro-Companion/releases).
2. In Windows File Explorer, right-click the ZIP and choose **Extract All**. Open the extracted folder. Keep `QproFaceTracking.exe`, `Helpers`, and `QproRuntime` together.
3. Double-click `QproFaceTracking.exe`. It opens on **First-time setup** the first time. You can return to that page from the menu on the left.

The executable, existing app/data folders and `.qptonguemodel` files retain their original names for update compatibility. Older updaters may look for a release asset named **QproFaceTracking.V3.0.4.zip**. For a manual install, the supplied **QproCompanion.V3.0.4.zip** is sufficient.

Native face and eye forwarding can start as soon as your streaming app shares data and VRChat accepts OSC. If you enable a camera feature, **PC runtime** may download several gigabytes. The ZIP includes ADB, and Companion manages its own Python environment.

### Open Companion with SteamVR

**Launch with SteamVR** is off by default. To enable it, start SteamVR, open Companion from the extracted folder you want to use, and check **First-time setup > Launch with SteamVR**. Read the status below the checkbox to confirm. To disable it, start SteamVR and clear the same checkbox in Companion.

On future SteamVR starts, Companion opens with its usual native forwarding on the direct route. Optional camera, face and controller features still require **Start tracking**. Updating Companion in the same folder keeps the registered path. If you move the folder, open Companion from the new location and enable the option there while SteamVR is running.

### Keep the Hub updated

The Hub checks GitHub for a newer stable release when it opens. When one is available, an **Update available** popup offers **View update** to read its details or **Later** to dismiss the notice. It waits until the Hub is active and idle, and appears once per release in that Hub session. Neither choice downloads or installs anything. After **Later**, the top-right **Update available** link still opens the checked release; a temporary network failure does not discard those details. Click **Check updates** for a manual check. You can turn off **Check automatically when the Hub opens** in the update window.

From V3.0.2, release notes in the update window display headings, bold and italic text, lists and code formatting. HTTP/HTTPS links open only when clicked. Reading the notes does not download or install an update. Older Hub versions continue to show plain notes until upgraded.

Enable **Include prereleases** in the update window to check published test releases too. This is off by default and your choice is remembered. Updates still require clicking **Update** and **Restart and update**. A release must have a newer three-part version number, such as `v3.0.1`; a different test ZIP with the same version is not offered as an update. Suffix tags such as `v3.0.1-beta.1` require a manual download.

1. Stop tracking, then click **Update** to download the release and verify its ZIP checksum and packaged files. You can cancel while the download is running; the current app stays unchanged.
2. After verification, click **Restart and update**. The Hub closes, replaces its app files in the same folder, then reopens. It keeps your models, recordings, enabled options and connection settings. Native forwarding resumes when the Hub reopens. Start optional features again when ready.
3. Open **Components** in the update window to review Qpro's private runtimes and installed optional components. Stop tracking and training before **Update components**. Manage backup modules under **First-time setup > VRChat output**.

Component updates preserve the current or legacy ROCm track. They do not change another application's Python. Custom runtime overrides need manual maintenance. The component window reports download and validation output; errors stop remaining updates.

PC runtime changes retain a recovery copy and restore it if installation or verification fails. An abruptly terminated process can leave that recovery copy for manual repair. ROCm updates use a separate environment and retain the prior verified environment until GPU training and inference checks pass. Several gigabytes of extra free space may be needed during updates.

The updater stages files in a temporary folder and keeps a backup for rollback if replacement fails. Verified obsolete bundled choices are replaced by the new developer model. Personal/imported pairs and their selections stay; if a number is occupied by one of them, the incoming developer model gets the next unused local number. If GitHub provides no ZIP checksum or the package does not support the Hub updater, use **View release on GitHub** and download the release ZIP manually. Older builds without the updater can be tested from a new extracted folder; keep the original folder and import working V2.0 or newer tongue models through **Model manager**.

If an older Hub updater rejects a direct-output package because the legacy bridge is absent, download the complete release ZIP and extract it into a new folder. Keep the old folder and export personal models before switching; do not copy only the EXE. The branded and legacy ZIP names contain the same package, so changing the filename does not resolve that check.

## 3. Get the PC apps ready

If Magisk asks whether **Shell / ADB Shell** may have root access, allow it. Keep the Quest awake while setting up the Hub.

Choose either [Virtual Desktop and its PC Streamer](https://www.vrdesktop.net/) or Steam Link for Quest-to-PC VR. Install SteamVR and VRChat on the PC. With **Direct VRChat** selected, Qpro discovers the local game and avatar inputs through **OSCQuery**. In VRChat, enable **Action Menu > OSC > Enabled**. Virtual Desktop or Steam Link supplies the native face data for both output routes.

### Virtual Desktop settings

Open **Virtual Desktop on the Quest**, connect to the PC Streamer, then open the headset app's **Streaming** tab. These options are in the headset app, not the Windows Streamer window; expand **Advanced Options** if needed.

| Setting | Use for the initial Qpro / VRChat setup |
| --- | --- |
| **Forward tracking data to PC** | **On.** Accept **Yes** when Virtual Desktop asks to share tracking data. Eye and face tracking must also be enabled in Quest settings, with permission granted to Virtual Desktop. |
| **Track controllers** | **On** when using your Touch Pro controllers, including Qpro's hands + controllers option. Turning this off prevents Virtual Desktop from adding the Quest controllers to SteamVR. |
| **Emulate Index controllers** | **Off** for the initial VRChat finger-tracking check. Virtual Desktop's native VRChat hand-tracking instructions use forwarding with Index emulation unchecked. Qpro routes optical fingers to the held controller's existing skeleton; Index emulation is not a Qpro requirement. |

These settings follow [VRCFaceTracking's Virtual Desktop setup](https://docs.vrcft.io/docs/hardware/vr/meta/quest-pro/virtual-desktop) and [Virtual Desktop's own VRChat hand-tracking release notes](https://github.com/guygodin/VirtualDesktop/releases/tag/v1.32.13). Menu wording can change between Virtual Desktop versions. Emulated Vive trackers are an optional body-tracking feature; they are not required for Qpro face tracking or fingers.

Start the PC Streamer and connect inside Virtual Desktop. Launch **SteamVR through Virtual Desktop**, then VRChat. Enable **Main Menu > Controls > Avatars Use Finger Tracking** for optical fingers. Choose **Virtual Desktop** under **Streaming app** in the Hub. Start Qpro tracking and check ordinary expressions on a supported avatar before adding camera features.

**Hands + controllers also requires Quest hand tracking, Automatically switch between controllers and hands, and Singularity's simultaneous switch.** Turn both Quest settings on under **Settings > Hands & eyes**. The Virtual Desktop options above do not enable them for you. Follow the optional hands instructions in section 5. **Virtual Desktop 1.34.22.0** remains the researched headset reference; other app version labels may attempt guarded startup, but their actual managed hand layout and native callers must pass validation. A PC driver must match a reviewed hash or an approved PE code/layout family. This is not support for every Virtual Desktop version.

If you want to stream the **Virtual Desktop picture over a USB cable**, Fwooffy's working setup used the Virtual Desktop **Beta** channel. In the Meta Horizon phone app or the headset's app library, long-press Virtual Desktop, open **Settings > Release Channels**, choose **Beta**, and update it. This changes VR video streaming. The Hub's USB or wireless choice below controls **tracking data**, which is separate. A newer beta can change a private hand layout. Keep Hands + controllers off if startup rejects that layout; a different version label alone is no longer an automatic rejection.

### Pause Windows Streamer updates

In **First-time setup**, expand **Show optional tools and recovery**, choose **Checks & Updates**, then open **Show Windows Streamer updates**. Press **Check updater status**, then **Pause PC Streamer updater** to hold the Windows updater. Windows asks for administrator approval. Stop tracking and finish any other setup action first. If the Virtual Desktop updater or installer is running, let it finish before trying again.

This adds a Companion-owned Windows Defender Firewall rule for the verified, installed **Updater.exe**. It blocks that updater's network access; it does not stop an installer already downloaded or prevent a manual installation. It does not change the Quest app, the Streamer program, its INI file, or other firewall rules. Linked or unverified installations are refused.

To undo the hold, return here and press **Restore PC Streamer updater**, then approve the Windows prompt. Restore remains available if you switch to Steam Link. It removes only Companion's rule for the current Windows user; the status explains if another user's hold remains. Restore before removing Companion from the PC.

Pausing does not downgrade Virtual Desktop, add a hand-tracking compatibility profile, or guarantee that a newer Quest app connects to an older PC Streamer. Keep **Hands + controllers** off if its preflight or startup checks reject the installed driver/layout.

### Steam Link settings

Choose **Steam Link** under **Streaming app**. In its headset **Advanced Settings**, enable **OSC**, **Share eye tracking data** and **Share face tracking data**, then set **OSC Output Port** to **9015**. This is Qpro's native input port, separate from VRChat's discovered output destination. The Hub's **Connection type** chooses USB or wireless ADB for camera data.

Stop optional features before switching the Hub's **Streaming app**. The Hub follows the new selection automatically. Do not run a second Steam Link face receiver on port 9015, or another face sender to VRChat, during direct sessions. Earlier live tests covered the VRCFaceTracking route; direct OSC output needs a new live headset/avatar check.

## 4. Connect your Quest to the Hub

For camera capture and headset tools, choose **one** connection type on **First-time setup**. If this is your first time using the Hub, USB is the easiest way to check that it works. You can switch to wireless later.

### Option A: USB cable

1. Plug the Quest Pro into the PC and put the headset on.
2. If a **USB debugging** message appears in the headset, choose **Always allow from this computer**, then **Allow**. This approval comes from the headset; the Hub cannot grant it for you.
3. In the Hub, set **Connection type** to **USB cable**. Qpro remembers the USB headset after checking that it is a Quest Pro. On later starts, leave the cable connected and keep the Quest awake; you should not normally need to unplug it. If the Hub says it can see the headset but has no root access, open Magisk on the Quest and allow **Shell / ADB Shell**. Then use **Refresh connection status** on the Facial tracking page.
4. If the USB connection stalls, stop Qpro tracking and training, then press **Check USB connection** in **First-time setup**. It retries the selected USB connection and reports whether the Quest needs debugging approval, is offline, or is missing. It does not reconnect other Android devices or change to wireless automatically. A loose cable or missing USB driver still needs to be fixed separately.

To use a different Quest Pro, stop Qpro tracking and training, press **Forget USB headset**, then connect the intended headset and press **Check USB connection** to verify and remember it. After forgetting, automatic status checks wait for that button instead of selecting the still-connected old headset again. If several USB devices are connected before a Quest Pro is remembered, disconnect the other devices for the first check. Qpro does not silently replace a remembered headset with a different one. The saved USB selection stays with this app's connection settings when the updater runs.

### Option B: Wireless ADB

1. Connect the Quest and PC to the same private Wi-Fi network. In **First-time setup > Headset**, choose **Wireless ADB (Wi-Fi)**.
2. Enter the Quest's Wi-Fi address in **Quest IP:port**. It usually looks like `192.168.1.25:5555`. The numbers before `:5555` are *your Quest's* address, so do not copy that example exactly. Press **Connect to Quest**.
3. A **Quest connected** pop-up means wireless ADB and Magisk root are ready. Activity will also say **Wireless Quest ready**. **Do not press Pair and connect.** You can unplug the USB cable, if one was attached. If a **Quest connection failed** pop-up appears, check the Quest's current IP and port, keep it awake, and read **Activity** for the exact error.
4. If the wireless connection is off but USB works, plug in USB once, open **Show wireless setup options**, then press **Enable from USB**. After the Hub reports success, unplug USB and connect to the saved Quest address.

**Pair and connect** is only for a headset that shows a **six-digit wireless debugging pairing code**. Keep the pairing message open in the headset. Open **Show wireless setup options**, then **Show pairing options** in the Hub. Put the short-lived address shown next to the code into **Pairing IP:port**, put the code into **Six-digit code**, and keep the normal Quest address in **Quest IP:port**. The two port numbers are different. For example, `192.168.1.25:5555` is a normal connection address, **not** a pairing address. Use your Quest's actual address.

If pairing says `protocol fault`, open a new pairing message on the headset and use its new code and pairing port. If **Connect to Quest** already said ready, skip pairing. Your Quest's Wi-Fi address may change after a reboot; update it in the Hub if it does. Use wireless ADB only on a trusted network.

## 5. Press the setup buttons

**Native face and eyes.** Direct VRChat is the default. Keep Companion open to forward native face and eyes automatically when the streaming app shares data, VRChat has OSC enabled and the avatar accepts the input. Close Companion to stop direct forwarding.

Use the three sections in **First-time setup**. Open **VRChat output** for native forwarding and add **Headset** and **PC runtime** for camera features.

- **VRChat output.** Leave **Direct to VRChat** selected under **Output route** and choose your **Streaming app**. Close VRCFaceTracking if it is running. Start VRChat, enable **Action Menu > OSC > Enabled**, and press **Check VRChat connection**.
- **Headset.** Choose USB or wireless ADB and complete the connection steps in section 4.
- **PC runtime.** Press **Install runtime** to prepare Qpro's private Python. **Setup progress** shows the current message. **View setup activity** opens the full log.

**Choose tracking features** opens **Facial tracking**. Inspect **Activity** for native forwarding status and check a blink and smile on your avatar.

Optional tools are below the steps under **Show optional tools and recovery**. Choose **Optional features** for gaze, AMD acceleration or hands/controllers; **Checks & Updates** for headset checks and the PC Streamer updater; or **Recovery** for runtime, module and gaze recovery. Tools start closed, and only one tool opens at a time within a group. Changing groups does not run a check or installation. These tools are not required to complete ordinary face-tracking setup.

### Optional VRCFaceTracking backup

Use a Companion package that includes backup support. Open **First-time setup > VRChat output**, select **VRCFaceTracking backup** under **Output route**, and choose your **Streaming app**. On **Facial tracking**, a failed direct check can also offer **Use VRCFaceTracking backup**.

1. Open VRCFaceTracking once if it is a new installation, then close it and let **ModuleProcess** exit.
2. Click **Install Qpro backup module**. Installation saves recognized source modules for recovery and installs the Qpro module for the selected streaming app.
3. Reopen VRCFaceTracking, run the installed Qpro module, enable OSC in VRChat and test your avatar.

To switch back, close VRCFaceTracking and select **Direct to VRChat** under **Output route**. If either route is waiting for VRChat or OSC, open VRChat, check **Action Menu > OSC > Enabled** and verify the current avatar. Switching output routes does not enable OSC.

To remove an installed Qpro module, close VRCFaceTracking and its helper. Open **Activity → Restore stock VRCFT**, review the confirmation and check Activity for the result. The button appears when Companion finds a Qpro module or recovery record. Verified backups return to free original locations. The records are kept in `%LOCALAPPDATA%\QproFaceTracking\module-recovery`.

**Check direct output in Activity.** Confirm the selected native source is available and VRChat OSCQuery has discovered supported parameters for the current avatar. Virtual Desktop shared memory has no producer timestamp: availability alone cannot prove moving face input. Verify actual avatar motion. Steam Link packets and Qpro camera overrides have expiry checks.

**Optional compatibility details:** Open **Checks & Updates > Show headset compatibility**. **Check headset compatibility** reads the Quest Pro model, exact firmware build, tracking engine and gaze setup without changing headset tracking. **Show firmware details** explains which checks passed and what remains unverified. This diagnostic is useful when something fails; it is not an extra required step for ordinary face tracking or an active Magisk gaze module. Each selected Qpro feature still checks its requirements when it starts. A recognized Hub gaze engine is not proof of live input.

**Optional independent gaze:** if the Independent Eye Gaze Magisk module is active, **skip both Check gaze setup and Prepare gaze**. Once detected, the Hub shows **Provided by Magisk**, keeps its gaze switch off and disables both setup buttons. The module already handles independent gaze. If you choose the Hub's temporary independent gaze method instead, disable the Magisk module and reboot first, then refresh detection with **Check USB connection** or **Check headset compatibility**. Under **Optional features > Show gaze setup**, use **Check gaze setup** followed by **Prepare gaze**. The popup identifies the detected gaze method and gives the next step. The Hub reads the stock eye model and tracking-engine details from that headset, then prepares a temporary model on the PC. You do not need to supply an engine file. Neither button is required when you only want tongue or pupil tracking. Prepare gaze again after a headset firmware update when using the Hub's gaze method.

Open **Recovery > Show gaze recovery tools** for the recovery buttons; the result popup also explains them. **Recover Qpro gaze** restores the previous eye-model state saved for a recorded Qpro session. **Reset legacy gaze** offers a confirmed choice of normal, nonexperimental gaze selection for an older session without a record. Neither button disables a Magisk module or uninstalls the PC module. The check reads headset settings; it does not test whether convergence works in your avatar.

The Hub checks the prepared model and headset build before restarting tracking. If the build is unsupported or changed, Activity explains why and the Hub leaves headset tracking alone. Keep the Hub's **Independent Eye Gaze** off and use ordinary eye tracking from your selected streaming app. Do not use another gaze method to bypass a failed compatibility check. Note your Quest's **exact firmware build** when asking for help. On a supported build, applying or restoring the Hub's temporary eye model restarts Meta trackingservice. The headset may briefly look frozen and lose positional tracking; this is an expected restart, not a headset crash. Wait for Activity to confirm that tracking has returned.

For a previously installed Qpro module, see [backup and module recovery](#optional-vrcfacetracking-backup).

**AMD GPU:** After **Install runtime** succeeds, press **Install ROCm 10.1** if the Hub detects one of these discrete Radeon models on Windows 11. Windows 10 22H2 (build 19045 or later) users can explicitly enable **Try ROCm on Windows 10 (experimental)** before installing. AMD's supported Windows configuration is Windows 11; Windows 10 success depends on the card and driver and is not guaranteed. The same discrete GPU, training and inference checks still must pass. For a manual attempt, run `Install-QproRocm.ps1 -AllowExperimentalWindows10`. Older Windows versions and legacy ROCm 7.2.1 on Windows 10 are not admitted. If an older verified ROCm 7.2.1 environment is already present, the button says **Upgrade to ROCm 10.1** instead:

For AMD setup, open **Show optional tools and recovery > Optional features > Show AMD setup**. The Windows 10 option is under **Show AMD compatibility details** inside that tool.

| Series | Models mapped for ROCm 10.1 |
| --- | --- |
| RX 6000 | 6950 XT, 6900 XT, 6800 XT, 6800; 6750 XT, 6700 XT, 6700; 6650 XT, 6600 XT, 6600 |
| RX 7000 | 7900 XTX, 7900 XT, 7900 GRE; 7800 XT, 7700 XT, 7700; 7600 XT, 7600 |
| RX 9000 | 9070 XT, 9070, 9070 GRE; 9060 XT, 9060 |
| Radeon PRO | AI PRO R9700, PRO W7900, PRO W7900 Dual Slot |

Qpro installs [AMD TheRock ROCm 10.1 packages](https://github.com/ROCm/TheRock/blob/main/RELEASES.md) for the detected card's [GPU target](https://rocm.docs.amd.com/en/latest/reference/gpu-specs.html). These are AMD's stable packages, while their use in Qpro remains **experimental**. AMD's package index includes Windows device packages for the [RX 6750/6700 family (gfx1031)](https://stable.repo.amd.com/rocm/whl-next/amd-torch-device-gfx1031/) and [RX 6650/6600 family (gfx1032)](https://stable.repo.amd.com/rocm/whl-next/amd-torch-device-gfx1032/). Package availability does not guarantee driver compatibility on every card. Confirm the Hub prompt and allow time for a large download. [AMD's ROCm 10.1 matrix](https://rocm.docs.amd.com/en/latest/compatibility/compatibility-matrix.html) validates Windows 11 25H2 with Adrenalin 26.10.41.05; other Windows 11 builds or drivers may fail the GPU checks. Qpro enables the new environment only after GPU training and model inference tests pass. Integrated graphics and unlisted Radeon models are not eligible. If setup fails, read the full **Activity** error.

New ROCm installations use a short, shared Qpro folder under `%LOCALAPPDATA%\QproFaceTracking\r`, with a separate folder for the card's GPU target. For example, an RX 7900 XT uses `10-gfx1100`. This avoids long library paths caused by extracting Qpro into a deeply nested folder. You normally do not need to change Windows registry settings. Qpro also keeps verified older `.venv-rocm-experimental` installations available. If this extracted copy has a **verified** `.venv-rocm` installation on a card in [AMD's Windows ROCm 7.2.1 list](https://rocm.docs.amd.com/projects/radeon-ryzen/en/docs-7.2.1/docs/compatibility/compatibilityrad/windows/windows_compatibility.html), Qpro keeps it as a fallback when ROCm 10.1 is not ready. The Hub shows the version that was actually verified, including an existing ROCm 10.0 environment. An RX 7900 XTX was previously live-tested with ROCm 7.2.1 and 10.0. The new 10.1 recipe has passed offline checks but has not been installed or live-tested in this review. Other mapped cards still need physical checks; sharing a GPU target does not prove every model or driver works.

**NVIDIA GPU or CPU:** skip the AMD button. NVIDIA CUDA hardware has been live-tested and is functional. When tracking starts, **Activity** tells you whether lower-face inference and pupil processing are using AMD, NVIDIA, or CPU.

### Optional: experimental hands and controllers (test build)

The **Controllers** sidebar page sits directly below **Facial tracking**. The shared bottom **Start tracking** / **Stop tracking** bar controls optional features. Installation stays on **First-time setup**.

**Hands + controllers** uses the headset cameras to track your real fingers while you hold the physical controllers. Controller position, buttons and haptics still come from the controllers. This feature is separate from direct face output, is off by default, and currently requires **Virtual Desktop** on a rooted Quest Pro. Steam Link is not supported for this option. A previous live test showed finger movement while holding controllers, but also caused a substantial frame-rate drop. The current revision still needs a sustained live performance check.

**Before enabling hands + controllers:** complete this checklist. These are manual menu settings; installing Qpro's components does not turn them on, and its compatibility check does not verify the Quest auto-switch menu option for you.

| Where | Required setting |
| --- | --- |
| Quest **Settings > Hands & eyes** | **Hand tracking: On.** **Automatically switch between controllers and hands: On.** |
| **Singularity > AIO Tweaks** | **Simultaneous Hands & Controllers: On.** Some versions say **Simultaneous Hands Controllers**. |
| Virtual Desktop on the Quest **> Streaming** | **Forward tracking data to PC: On.** **Track controllers: On.** Accept the tracking-data permission. |
| VRChat **Main Menu > Controls** | **Avatars Use Finger Tracking: On.** Use the SteamVR version of VRChat. |

For the initial check, keep Virtual Desktop **Emulate Index controllers** off. Turn off Singularity's separate **Frida Server** so it does not compete with Qpro's helper. Keep the Quest awake and allow **Shell / ADB Shell** root access if Magisk asks.

1. **Check the Quest settings.** In Quest **Settings > Hands & eyes**, confirm both **Hand tracking** and **Automatically switch between controllers and hands** are on. With controllers set down, check that the headset sees your empty hands.
2. **Enable Singularity's simultaneous switch.** Open **Singularity > AIO Tweaks** and turn on **Simultaneous Hands & Controllers** (some versions label it **Simultaneous Hands Controllers**). Leave it on while using this feature. Turn off Singularity's separate **Frida Server** so it does not compete with Qpro's helper. The toggle is documented in [Singularity's release notes](https://github.com/Lumince/singularity/releases/tag/v1.0.4.3); its location can vary between Singularity versions.
3. **Install Qpro's optional components once.** Install the normal **PC runtime** first, then close **SteamVR** and wait for it to exit. In **First-time setup > Show optional tools and recovery > Optional features > Show hands setup and uninstall**, press **Install hand/controller components**. Setup downloads checksum-verified Frida 17.18.0 into Qpro's separate environment and registers Qpro's controller add-on. Repeat installation only when the Hub asks you to repair or update these components.
4. **Connect and check the VR session.** In the headset's Virtual Desktop **Streaming** tab, set **Forward tracking data to PC** and **Track controllers** on, and leave **Emulate Index controllers** off for the initial VRChat finger check. Accept the tracking-data permission prompt. Connect to the PC Streamer, then launch **SteamVR through Virtual Desktop**. A USB ADB connection alone does not start this VR stream. Launch SteamVR VRChat and turn on **Main Menu > Controls > Avatars Use Finger Tracking**. Before starting Qpro hands, set controllers down and check ordinary empty-hand tracking. Tap the controllers together twice in Virtual Desktop if needed to enter hand mode. Fingers must move in VRChat before continuing. See the Virtual Desktop settings in section 3 and [VRChat's finger-tracking instructions](https://docs.vrchat.com/docs/input-20-faq).
5. **Run the read-only preflight.** Select **Virtual Desktop** under **Facial tracking > Your session > Streaming app**, then press **Controllers > Check hand/controller compatibility**. Read its explanation and next step. Green **Controller requirements ready** means the component and driver layout prechecks passed; it does not prove live optical fingers or performance. The detail states whether native hook validation is still pending. An unfamiliar version warning still requires the startup checks below. Missing components, Frida or invalid/unreadable settings remain errors.

   A PC driver with an unfamiliar full-file SHA-256 must match a conservative approved **PE code/layout-family fingerprint**. Before fixed-RVA hooks are installed, startup also validates the actual loaded image and native interfaces. A different code/layout family is rejected instead of guessing offsets.

   A different Quest app version label may attempt startup, but the adapter must prove the actual **Mono hand-result fields, types, sizes and layout**. Native query callers, controller roles, bones, input freshness, leases and confirmed cleanup still have to pass. **1.34.22.0** remains the researched headset reference; historical PC inspections of **1.34.23 beta** and the exact **1.34.25 rebuild** do not establish a new PC/headset pair works live. Reinstalling components cannot fix a rejected layout.

6. **Start and verify real fingers.** Turn on **Experimental hands + controllers**, leaving **Experimental Touch Pro thumb-rest input** off for this first check. Press **Start tracking**, hold the controllers with your hands visible to the headset cameras, and move individual fingers. Activity must progress beyond startup to `HANDS_READY`; that reports fresh optical data routed to at least one held controller. Check both hands in VRChat: fingers should follow your real fingers while controller movement and buttons still work. Ordinary fist, point or open-hand gestures from touching controller buttons do not establish that optical finger tracking works.
7. **Stop cleanly.** Press **Stop tracking** and wait for Activity to confirm controller cleanup before closing the Hub or restarting this feature. If cleanup is unconfirmed, keep the feature off, close SteamVR, restart headset Virtual Desktop and reopen SteamVR before checking again. If game FPS falls or SteamVR becomes unstable, stop tracking and keep this experimental option off.

**Troubleshooting hands + controllers:**

- **Only controller gestures move:** recheck Quest **Hand tracking** and **Automatically switch between controllers and hands**, Singularity's simultaneous switch and VRChat's **Avatars Use Finger Tracking** setting. Check ordinary empty-hand tracking first, then keep fingers in camera view and read Activity for `HANDS_READY` or the routing failure. Being connected over ADB or seeing a helper start is not a live finger check.
- **Settings look off in Activity even though their menu switches are on:** preliminary hand-tracking preferences can be inactive before Virtual Desktop requests simultaneous mode. The current version warns on those known inactive values and then checks actual optical input. Missing or unreadable settings, simultaneous mode other than `1`, an unsupported runtime, or missing live input still refuse startup. Check connection/root access and reconnect Virtual Desktop; do not edit the preferences by hand.
- **Unsupported PC code/layout family, Mono hand-result layout or controller ABI:** keep Hands + controllers off and copy the full diagnostics. Face tracking can continue without this option. A changed version label can attempt guarded startup, but an unrecognized private layout still needs review.
- **No fingers after startup or a disconnect:** keep the headset on, hold a controller, and keep the hand visible. Reconnect Virtual Desktop if its stream stopped, then use **Stop tracking** before retrying. The compatibility button checks installed versions and settings; it does not start tracking.

**Optional thumb-rest input:** **Experimental Touch Pro thumb-rest input** is separate from finger tracking. It adds SteamVR trackpad inputs from the controllers' thumb-rest sensors. The experimental **touch-pro-shared-pages-v1** profile checks the actual shared-page layout instead of requiring an exact firmware number. Newer numeric builds, including **51503870035800340**, may attempt the same read-only check without a new app release for every build. Both named pages, both sample buffers and fresh counter progression must pass before extra input is enabled. Missing or inconclusive data keeps it off. A real sensor-format change can still require a new reader profile; consistent data does not prove physical signal meaning on an unfamiliar firmware. The earlier live check is limited to build **51503870024400340**. On newer firmware, verify left/right motion, lift/contact and pressure after the check passes.

On **Controllers**, the controls sit directly below the two feature toggles. Choose **Thumb-rest mode**: **Trackpad**, **Relative joystick**, **Swipe**, or **Desktop mouse**. Choose **Apply mode to**: **Left (L)**, **Right (R)**, or **Both** (default). This selection affects only the extra thumb-rest input; normal controller buttons, movement and optical fingers remain available on both sides. Keep both controllers awake during the initial thumb-rest check, even when applying the mode to one side.

Before starting input workers for a thumb-rest session, Qpro hash-checks the installed controller add-on DLL against the one bundled with this build. An outdated or unreadable copy blocks startup. If it does not match, **close SteamVR**, reinstall the controller add-on from the current build using **First-time setup > Show optional tools and recovery > Optional features > Show hands setup and uninstall > Install hand/controller components**, then reopen SteamVR **through Virtual Desktop**. This makes the required driver acknowledgement available. Matching files on disk is only a precheck; it does not prove SteamVR loaded that driver. Received input, driver diagnostics and live checks still matter.

Trackpad follows thumb position. Relative joystick centers on the first touch, suppresses small center jitter and returns to center after release. Swipe follows thumb speed, preserves slow continued movement, then eases away after release. To assign extra game actions for these modes, open **SteamVR Settings > Controllers > Manage Controller Bindings**, select VRChat or another app, and edit your custom binding. Look for **Qpro thumb-rest surface (extra)** on the selected controller. Its extra `trackpad` source offers position, touch, force and click; choose the app's actions yourself and save. **SteamVR binding instructions** in the Hub explains the same steps and is available before a session. No premade game assignments replace your bindings. Restart SteamVR after installation if its binding editor still shows the old profile.

**SteamVR dashboard scrolling:** the add-on installer prepares an additive dashboard binding from the installed Valve default, keeping its ordinary controls. Enable thumb-rest input, select **Swipe** and press **Start tracking** to scroll the dashboard. Its dedicated scroll source is neutral in the other modes and after **Stop tracking**. If you already chose a personal dashboard binding, the installer leaves that choice intact; select the prepared **Qpro Companion thumb-rest scrolling** default in SteamVR's dashboard bindings if you want to use it. No global SteamVR binding settings are overwritten.

**VRChat menu scrolling:** installation adds the dedicated Swipe source to a copy of a recognized VRChat binding, preserving movement, gestures, buttons and other actions. Automatic selection requires an existing current binding selection that the installer can verify. Without that selection, a recognized default can be prepared and **Activity** asks you to select it manually. Unsupported bindings stay unchanged with a warning; the installer does not guess a selection key. This prepares menu scrolling only. Extra actions in VRChat and other games still need their own per-app assignments. A prepared binding does not confirm that SteamVR loaded it or that live scrolling works; test the dashboard and VRChat menus after restarting SteamVR.

Desktop mouse moves the **Windows pointer** and uses a pressure press for left-click. It sends no extra SteamVR axes or buttons and needs no SteamVR action assignment. With **Both**, independently anchored thumb movements are summed; the shared left button remains held while either selected thumb is pressing. Releasing one thumb does not cancel the other thumb's held click. Stop tracking disables this input. Confirm pointer movement and clicks in Windows when the Hub reports **Desktop mouse input received**.

**Read each result at its own scope:** green compatibility means prechecks passed; **Live optical fingers received** reports fresh optical data routed to a held controller; **Live thumb-rest input received** / `TOUCHPAD_READY` reports selected-side sensor packets sent to PC loopback. Received packets do not prove SteamVR accepted a component, your custom bindings work, or your avatar moves. Activity's `CONTROLLER_DRIVER_STATUS` separately reports applied settings, adopted sides, successful native input calls and errors. A successful input call is still not an avatar check. Verify both hands, the chosen extra actions and sustained game performance yourself.

After thumb-rest input has been enabled, **Stop tracking** also waits for a fresh driver acknowledgement: disabled settings applied, extra input neutral, no Qpro mouse press held and no input, mouse or restoration errors. A stopped reader alone cannot confirm that cleanup. This acknowledgement does not mean SteamVR unloaded the hook or profile; close SteamVR before removing the add-on. If acknowledgement or hands restoration is missing, follow the Hub's restart instructions and retain Activity output.

**Removing the add-on:** close SteamVR, press **Uninstall controller add-on** in First-time setup, then reopen SteamVR to reload its normal controller profile. Uninstall keeps a recovery copy and removes the optional thumb-rest add-on; it does not remove the installed hands runtime components or turn off Singularity's switch.

See the [experimental controller guide](CONTROLLER_INPUT.md) for compatibility and recovery details. Body-tracking interaction, sustained performance and newer runtime versions still need live checks.

## 6. Tongue training and refinement

**Train if you have no personal model or used a version before V2.0. You can import a working V2.0 or newer model into V3.0.4.** The two bundled choices are **Developer tongue model** and **Mustachio**; personal imports and refinements remain available.

1. For Quick refinement or Focused training, choose the model to extend under **Facial tracking > Lower-face tracking > Tongue model**. **Developer tongue model** is the default on a fresh setup; updates keep personal/imported selections and replace retired bundled choices with the new developer model. Full dataset makes a new personal model instead.
2. Open **Personalize**. Under **Capture and train**, start with **Quick refinement**, or press **Show focused and full captures** for the other modes.
3. Follow each pose card, hold the pose steady, and press **SPACE** once per still. Save at least the on-screen minimum for every card before pressing **ENTER**. Include side and diagonal positions; missing a pose can leave a tracking gap there. Left and right mean your own sides, like your hands.
4. Choose the completed recording in the training list and press that mode's training button. Wait for Activity to say training finished. Select the resulting model before restarting tracking.

- **Quick refinement:** press **1. Record refinement**, then **2. Train personalized copy**. Its **16 tongue cards** refine a copy of the selected model.
- **Focused diagonals + facial hair:** press **1. Record focused dataset**, then **2. Train focused copy**. Its **22 tongue cards** add diagonal positions and matched hidden/visible poses for facial-hair shadows.
- **Full dataset:** press **1. Record full dataset**, then **2. Train new personal model**. Its **58 tongue cards** record the full curriculum; take breaks as needed.

If you have facial hair or wear a bandage near the mouth, keep it as normally worn. For every visible pose, check that the tongue can be seen in both camera panels. Hold one pose per still and vary your jaw or headset position slightly between stills. Record hidden poses too, so beard shadows and smiles are not mistaken for tongue movement. Training or contrast processing cannot reveal a tongue completely covered by hair. Tongue cards need fresh native input even when expression values stay steady. Direct Steam Link capture shares Companion's input on port 9015 and works with VRChat closed. For the backup route, keep VRCFaceTracking and the matching Qpro module open so they provide native labels.

### Choosing a developer model

**Developer tongue model** adds a visibility detector trained with new clean-shaven recordings while retaining the original developer tongue direction outputs. The separate 22-card recording after reseating the same wearer's headset reduced missed visible-tongue stills from 75 to 1 across 155 stills with native TongueOut fusion, with no hidden-tongue false detections in that recording. A small tongue tip during a smile was still missed. Some older bearded poses worsened; broader wearer and facial-hair testing remain pending. This checks visibility on that wearer, not improved tongue direction or accuracy across all faces. Start with **Tongue visibility > Weighted camera + native**; **Camera only** produced false detections in this check.

**Mustachio is highly experimental and optional.** It extends the earlier developer tongue model with a focused capture from one bearded and moustached wearer. Broader facial-hair and independent-wearer tests remain pending; it may perform worse for some people. Select **Mustachio** under **Tongue model** and restart tracking to try it. Contrast processing is automatic; it does not invent hidden tongue pixels or remove hair. Quick refinement and Focused training can extend it. Those copies retain the experimental label after renaming, export and import. Full dataset starts a new personal model.

After training, **Activity** lists the weakest held-out pose cards and diagonal corners. `missed` counts visible-tongue frames marked hidden by the selected detector; `fnr` is that count divided by visible frames. These use held-out frames from the same recording, so check fresh poses or live tracking too.

Use **Model manager** to export backups and import working V2.0 or newer `.qptonguemodel` files. Older combined exports remain usable for their tongue outputs. Importing weights does not restore their original recordings. Camera recordings are personal data; share them only if you choose to.

To remove a recording, open **Show saved recordings** under its matching **Personalize** capture card. Choose **Saved recordings (including trained)**, then **Delete selected dataset…**. Confirmation permanently removes that recording, its labels/session details and prepared cache; models already trained from it remain available.

## 7. Start tracking

1. Start your selected **Virtual Desktop** or **Steam Link** app on the Quest, then SteamVR and VRChat on the PC. Steam Link users should confirm its OSC and eye/face sharing settings above.
2. In the Hub, open **Facial tracking**. **Your session** shows readiness and **Show connection and camera settings** opens camera controls. **Eyes** contains independent gaze, pupil dilation and eyebrow movement. **An active independent-gaze Magisk module keeps the Hub gaze switch off and disabled once detected.** In **Lower-face tracking**, select **Tongue model**, enable tongue output if wanted, and choose the native individual cheek switches. Open **Controllers** in the sidebar for the controller prerequisites, compatibility check and modes. Health/backend details are on **Activity > Tracking health**. If tracking is running, press **Stop tracking** before changing the model, then restart.
3. Press **Start tracking**. Tracking camera previews default off on a fresh setup; saved preview choices are kept. To show them, open **Your session > Show connection and camera settings** and enable **Preview tracking cameras** before starting. Guided training capture still opens its pose window. Open **Activity** if you want to see whether the camera connected and which device is running the tongue model.
4. When you finish, press **Stop tracking**. Wait until Activity confirms that the live processes have stopped. If you used the Hub's gaze method, also wait for its gaze recovery result: it restores the headset eye-model state recorded before that Qpro session.

Closing the Hub stops tracking cleanly, then stops the configured PC ADB server. Other Android tools using the same ADB server will disconnect and may restart it. Finish setup, capture, training, or eye-model recovery before closing.

### Processing profiles

**Activity > Tracking health > Show tracking details** reports direct output and source observations. Confirm native source input separately from VRChat discovery and visible avatar movement. A discovered game or accessible Virtual Desktop shared memory cannot establish live expressions.

Under **Your session > Show connection and camera settings**, **Processing profile** controls pupil processing work separately from the shared **Camera FPS cap**. Its selection is saved and applies the next time tracking starts.

| Profile | Tongue | Pupils |
| --- | --- | --- |
| Responsive (default) | Follow the camera FPS cap; newest frame scheduling | Follow the camera FPS cap; newest frame scheduling |
| Balanced | Same as Responsive | Up to 12 eye pairs per second |
| Low CPU | Same as Responsive | Up to 8 eye pairs per second |

The presets keep model weights, image sizes, preprocessing and tongue smoothing unchanged. Balanced and Low CPU reduce pupil processing frequency, so pupil response and warm-up can take longer. Responsive remains the default for both new and existing installations. Lower rates are explicit choices, not an automatic change based on CPU usage. Rates cannot exceed the available camera feed.

### Repair or restore the PC runtime

Under **First-time setup > Show optional tools and recovery > Recovery > Show PC runtime repair and recovery**, **Repair runtime** rebuilds the normal private CPU/NVIDIA tracking packages. **Restore saved runtime** lists retained backups, validates a selected backup before replacing the active runtime, and keeps the displaced runtime as another recovery copy. Stop tracking, capture and training before using either action. Models, recordings, enabled options and connection choices are kept. AMD ROCm uses a separate environment and its own install/update actions.

**Stop tracking** ends Companion's optional camera features and session adjustments. With **Direct VRChat**, ordinary native face and eyes continue until you close Companion. With the backup route, VRCFaceTracking owns native forwarding. Model and feature choices are saved. Magisk gaze remains active until you disable it in Magisk and reboot the headset.

### Returning to ordinary tracking

Use **Check gaze setup** in **First-time setup** for optional troubleshooting, or before choosing the Hub's temporary gaze method. It reads the headset's current eye-model selection and active gaze methods without changing them. It is not required when an Independent Eye Gaze Magisk module is enabled; leave the Hub's **Independent Eye Gaze** off and skip both gaze setup buttons.

- **An interrupted Qpro gaze session:** After stopping Qpro tracking, use **Recover Qpro gaze** in **First-time setup**. It restores a verified session's recorded previous state. A result saying no Qpro session remains does not mean every headset modification was removed.
- **An older build left experimental gaze enabled without a recovery record:** Use **Reset legacy gaze** in **First-time setup** only when you want the normal, nonexperimental headset gaze selection. Read and confirm the prompt. This cannot recover an unknown previous selection; it explicitly chooses the normal selection. It refuses active independent-gaze Magisk modules or an unverified overlay on the eye model or its tracking engine. It does not uninstall modules or overwrite headset firmware. If a gaze Magisk module is active, disable it and reboot before retrying.
- **Compare original expressions:** press **Stop tracking** to end Companion's extras and watch the streaming app's ordinary face and eyes. To restore saved VRCFaceTracking source modules, close the app and its helper, then use **Activity > Restore stock VRCFT**. The [backup instructions](#optional-vrcfacetracking-backup) explain module recovery. Disable Magisk gaze and reboot separately to compare ordinary headset gaze.

If either recovery action refuses to proceed, keep its **Activity** error and ask for help with the exact firmware build. Recovery does not add support for an unsupported eye-tracking engine.

On **Facial tracking**, open **Your session > Show connection and camera settings** for **Preview tracking cameras** and **Camera FPS cap**. Open **Lower-face tracking > Show tongue settings** for **Motion smoothing** and **Tongue visibility**. Hiding the camera windows does not turn tracking off. Stop tracking before changing these settings, then start again. In **Eyes > Show eye settings and guidance**, **Pupil response** makes avatar pupil changes stronger or weaker; start near the default and adjust slowly. Pupil values are estimates for avatar animation, **not** measured eye health data. Your VRChat avatar must support pupil animation for changes to appear.

### Pupil processing and performance

Pupil tracking automatically uses a verified NVIDIA CUDA or AMD ROCm runtime when one is available. Filtering both eye images is batched on the GPU; contour fitting, quality checks and smoothing still use the CPU. If GPU processing is unavailable or fails, Qpro reports the fallback in **Activity** and continues on CPU. **Activity > Tracking health** shows whether pupil work is running on AMD, NVIDIA or CPU.

Qpro processes the latest eye pair instead of queuing old frames. Activity reports processing time, frame age and skipped work so a slow PC is easier to diagnose. Camera previews encode only the streams somebody is viewing. Close unused browser previews if performance is poor; GPU support cannot guarantee a particular frame rate on every PC.

### Individual cheek puff and suck

On **Facial tracking > Lower-face tracking**, **Individual cheek puff (1/0)** and **Individual cheek suck (1/0)** use native streaming-app signals with **1/0** separation. A clear one-sided expression becomes full strength on that side and zero on the other. A deliberate two-sided puff or suck still moves both sides. Cheek suck uses separate left/right signals; it does not use negative cheek-puff values.

Turn a cheek switch off to send the source's original values. **Start tracking** applies selected adjustments. **Stop tracking** returns to ordinary native forwarding. Saved switch choices are kept. These cheek controls do not need camera tracking, tongue training or cheek calibration. If the source separates the sides but the avatar does not, check its parameters and blendshapes.

In **Facial tracking > Eyes**, **Adjust eyebrow movement** starts off, so brows use the selected source's original values. Turn it on, open **Show eye settings and guidance** and adjust **Eyebrow sensitivity** to amplify or soften the existing left and right inner raise, outer raise, and lower/pinch expressions. This cannot add movements the headset does not detect. The avatar needs matching brow parameters and blendshapes to show that detail. If the source has separate brow expressions but the avatar shows only a single brow motion, check the avatar's face tracking setup.

Smirk handling is built into Qpro's direct face mapping. A clear one-sided smile emphasizes the leading corner while preserving an even smile. There is no separate smirk toggle.

For tongue motion, adjust **Motion smoothing** to soften extension and retraction. The visibility hold reduces quick dropouts after a visible pose, but it cannot make a completely obscured tongue visible. Use **Focused diagonals + facial hair** to record positions that still need better coverage.

If Independent Eye Gaze fails to start or stops unexpectedly, the Hub turns it off automatically and remembers that choice. Tongue and pupil tracking continue if selected. Check **Activity** for the recovery result before trying gaze again; select **Independent Eye Gaze** manually to retry.

## Common problems

| What you see | What to try |
| --- | --- |
| “Root access unavailable” | Keep the Quest awake. In Magisk, allow **Shell / ADB Shell**, then refresh the connection. |
| USB Quest is offline or the connection stalls | Keep the headset awake. Stop Qpro tracking and training, then use **Check USB connection** in **First-time setup**. If USB debugging is unauthorized, allow it inside the headset and choose **Always allow from this computer**. Check the cable and Windows USB driver if the device is still missing. |
| A different USB Quest Pro is connected | Stop Qpro tracking and training, then use **Forget USB headset**. Connect the intended Quest Pro and press **Check USB connection** to verify and remember it; temporarily disconnect other USB Android devices for its first check. |
| Waiting for VRChat or OSC | Open VRChat, enable **Action Menu > OSC > Enabled**, and use a supported avatar. Both routes need a working OSC destination. Use **Check VRChat connection** for the direct route. |
| Direct output paused while VRCFaceTracking is open | Close VRCFaceTracking and its module helper, then check **Activity**. |
| **Quest connected** pop-up, followed by a pairing error | You are already connected. Skip **Pair and connect** and start tracking. |
| **Quest connection failed** pop-up | Check the Quest's current Wi-Fi IP and port, wake the headset, and read **Activity** for the specific error. |
| `protocol fault` during pairing | Use a fresh six-digit code and the **temporary pairing port** shown on the Quest, not the usual `:5555` port. |
| Wireless stopped working after a reboot | Check the Quest's current Wi-Fi address. Wireless ADB may need to be enabled again. |
| AMD installer says PC runtime is missing | Finish **Install runtime** first, then return to **Install ROCm 10.1**. |
| ROCm setup reports a missing `torchgen` or a failed Python import | Use **Verify / repair ROCm 10.1** or **Repair ROCm 10.1** in First-time setup. Qpro checks and repairs its separate AMD environment before testing the GPU. Keep the full **Activity** error if repair fails; an import error alone does not mean your card is unsupported. |
| **Install runtime** stays on “Starting PC runtime setup” | Keep the Hub open and read **Activity**. It now shows the PowerShell launch, current setup phase, and a still-running message every 15 seconds when output stops. If PowerShell never prints its first script message, check Windows Security for a blocked `powershell.exe`, then share the Activity lines, including the PowerShell PID. |
| **Install runtime** ends with code 1 | Read the explanation and first error in **Activity**. A missing native DLL, `VCRUNTIME`, `WinError 126` or `WinError 1114` calls for installing or repairing [Microsoft's latest x64 Visual C++ Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170#latest-supported-redistributable-version), then retrying. `ModuleNotFoundError` means a Python package or app file is missing; follow Activity's runtime repair or complete-ZIP instructions instead. Code 1 has other causes too, so share the full error if it repeats. Do not delete the whole `%LOCALAPPDATA%\QproFaceTracking` folder just because the exit code is 1. |
| Steam Link source selected but no face or training labels arrive | Enable OSC and eye/face sharing in Steam Link; set **OSC Output Port** to **9015**. Stop competing receivers on that port, reconnect the source and retry. |
| Selected native source does not match | Stop optional tracking and select the correct **Streaming app**. Direct VRChat follows the selected app. If you use the backup route, install and run its matching Qpro module. |
| Both avatar cheeks move together | Confirm the individual cheek switch and a running Qpro session. Check native left/right source values and the avatar's supported cheek parameters/blendshapes. |
| AMD Ryzen integrated graphics appears during ROCm setup | Qpro checks for one of the exact discrete Radeon models in the ROCm 10.1 table above. An integrated GPU alone is not enough. Check that Windows and the AMD driver can see the discrete card; otherwise use **Install runtime** for NVIDIA CUDA or CPU. |
| Independent gaze or convergence fails but ordinary face tracking works | If you use the Magisk gaze method, keep Hub gaze off and check that module's firmware requirements; the Hub's gaze setup is not required. For the Hub's temporary method, use **Check gaze setup** and read Activity for the exact firmware build and next step. Keep Hub gaze off if its checks fail. Use only one gaze method. |
| Tongue disappears in some positions | Check the selected model and tongue output state in **Activity**. A stale result means the frame arrived too late; check the selected GPU runtime and reduce other camera work. Hidden output means the visibility gate did not accept that pose. If fresh poses still fail, keep the log and record those positions for refinement when possible. |
| Training says the prepared tongue cache uses an older mapping | Retry training in the Hub to rebuild prepared data from the saved capture. Keep the original recordings. If an older recording has an ambiguous streaming source, copy its diagnostic and identify whether it was recorded in Virtual Desktop or Steam Link before preparing it again. Existing trained models remain usable. |
| Facial hair or a bandage makes tongue training unreliable | Record a personal dataset with the same face appearance you use for tracking. Capture hidden and visible tongue poses, then check the trained model in Facial tracking. If native visibility flickers while the cameras still see your tongue, try **Camera only** under **Show tongue settings > Tongue visibility**. This may help but is not guaranteed for every face or headset fit. |
| Pupils seem too jumpy | Stop tracking, open **Eyes > Show eye settings and guidance**, lower **Pupil response**, then start again. Hold your gaze steady while tracking warms up. |

If Windows will not let you delete an older extracted Qpro folder, close its Hub and camera preview first. If the folder is still in use, open **Task Manager > Details**, end a leftover `adb.exe` process, then try again. Ending `adb.exe` temporarily disconnects any other Android tools using that ADB server.

If the log moves while you are reading, use **Activity > Pause display**. Tracking and log collection continue. **Resume display** brings in the retained messages while preserving your reading position where possible; **Jump to latest** resumes the display and follows new messages at the bottom. The log keeps a bounded recent history, so old messages can expire during a long pause. Copying or saving diagnostics includes retained arrivals while the display was paused.

If you need help, open **Activity** and press **Copy diagnostics** or **Save log…**. Include the error, your Quest's exact firmware build, whether you used USB or wireless, and which release ZIP you downloaded. You can ask in the [community Discord](https://discord.gg/ghvuJTpRu4). The server is not owned by Fwooffy.

Copy/save masks common Windows user-folder paths, named headset identifiers and local network addresses while keeping useful error, version and GPU details. It does not attach recordings or models, and nothing is uploaded automatically. Review the text before posting; unfamiliar paths, arbitrary log text and unlabelled address-like version numbers can still need manual removal.

The Steam Link source follows the OSC mapping documented by [danwillm](https://github.com/danwillm/VRCFT-SteamLink) and [LinkFT](https://github.com/ykeara/LinkFT). Neither third-party module is required for Qpro direct output.
