# Qpro Companion V3.0.0: text setup guide

> **Before you start: you need a rooted Meta Quest Pro and the latest VRCFaceTracking from Steam.** This program will not work on an unrooted headset or a different Quest model. If you still need to root your Quest Pro, use [Fwooffy and glorpette's beginner root guide](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md). Check that your headset's exact software version is supported by [Singularity](https://github.com/Lumince/singularity) before following a root guide. [Install or update VRCFaceTracking through Steam](https://store.steampowered.com/app/3329480/VRCFaceTracking/) before using Qpro's module.
>
> **If you used a QproFaceTracking version before V2.0, record and train your tongue model again.** Do not import a pre-V2.0 model and assume it will work correctly. Working V2.0 or newer models can be exported and imported into V3.0.0. **Developer tongue model** is the default starting point on a fresh setup; updates keep personal/imported selections and replace retired bundled choices with the new developer model.

> **This guide covers the V3.0.0 release candidate.** Virtual Desktop and Steam Link are available under **Streaming app**. The two bundled tongue choices are **Developer tongue model** and **Mustachio**. Mustachio remains highly experimental; start with Developer tongue model or your existing working model.

Start with the headset's eye tracking, then set up the PC software. **Qpro Companion** is the app; its main window is called the **Hub**. **ADB** is the connection it uses to talk to your Quest. The **Qpro module** sends tracking results to VRCFaceTracking.

## 1. Enable headset eye and face tracking

1. On the rooted Quest Pro, open **Settings > Hands & Eyes**. Turn on **Eye Tracking** and **Natural Facial Expressions**, then run eye calibration if offered. If these are already enabled, leave them on. Keep Developer Mode enabled.

### Optional: independent gaze through Magisk

Independent gaze is not required for ordinary face tracking, tongue tracking or pupil dilation. If you want the Magisk gaze method:

1. If **Magisk OverlayFS** is not installed yet, open **Singularity > Apps/Modules > Magisk repo** and install it. Follow any reboot prompts. If it is already enabled in Magisk, you can skip reinstalling it.
2. In the same Singularity menu, install **Quest Pro Independent Eye Gaze** if it is not installed yet. Reboot if prompted, then check in Magisk that both modules are enabled. This is the headset-side independent gaze route.
3. Once your selected headset streaming app and VRCFaceTracking are installed in section 3, check that left and right gaze and convergence move correctly. The Magisk route has been reported working on **Horizon OS v2.7**, but exact firmware builds can behave differently.

**Already using the Independent Eye Gaze Magisk module?** It already provides independent gaze. The Hub checks the connected headset without changing its modules. When an active gaze module is detected, **Independent eye gaze + convergence (provided by Magisk)** stays off and disabled, along with **Check gaze setup** and **Prepare gaze**. Skip those buttons. You can still use Qpro's tongue, pupil dilation and eyebrow features separately. If a Qpro gaze session is already running when a conflict is detected, press **Stop tracking** and wait for cleanup before continuing.

**Use one independent gaze method at a time.** While the Magisk module is active, leave **Independent Eye Gaze** unchecked in the Hub. If you want to try the Hub's temporary gaze method instead, follow **Check gaze setup** and **Prepare gaze** in section 5 before turning on **Independent Eye Gaze**. Applying that method briefly freezes the headset while its tracking service restarts; wait for Activity to confirm it is ready. **Stop tracking** only reverses the Hub's changes; it does not disable a Magisk module.

Before switching from the Magisk gaze method to the Hub's method, disable the gaze module in Magisk and reboot the headset. Then use **Reconnect USB** or **Check headset compatibility** to refresh detection; **Check gaze setup** stays disabled until the active module is no longer detected. A failed connection or scan does not clear a previously detected module. The same disable-and-reboot step is needed if you want ordinary headset eye tracking again. The Hub leaves Magisk modules alone.

## 2. Download and open the Hub

1. Download **QproCompanion.V3.0.0.zip** when it is published on [this project's Releases page](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases), or use the supplied release candidate ZIP. Choose the named Qpro Companion ZIP, **not** GitHub's “Source code” ZIP.
2. In Windows File Explorer, right-click the ZIP and choose **Extract All**. Open the extracted folder. Keep `QproFaceTracking.exe`, `Helpers`, and `QproRuntime` together.
3. Double-click `QproFaceTracking.exe`. It opens on **First-time setup** the first time. You can return to that page from the menu on the left.

Qpro Companion is the new public name. `QproFaceTracking.exe`, existing app/data folder names and the `.qptonguemodel` format are retained for update compatibility. The legacy-named **QproFaceTracking.V3.0.0.zip** asset contains the same bytes for older updaters; use either ZIP, not both.

You need an internet connection and several gigabytes of free space for setup. The ZIP already includes ADB. The Hub installs the Python parts it needs, so you do not have to install ADB or Python separately for this program.

### Keep the Hub updated

The Hub checks GitHub for a newer stable release when it opens. When one is available, an **Update available** popup offers **View update** to read its details or **Later** to dismiss the notice. It waits until the Hub is active and idle, and appears once per release in that Hub session. Neither choice downloads or installs anything. After **Later**, the top-right **Update available** link still opens the checked release; a temporary network failure does not discard those details. Click **Check updates** for a manual check. You can turn off **Check automatically when the Hub opens** in the update window.

Enable **Include prereleases** in the update window to check published test releases too. This is off by default and your choice is remembered. Updates still require clicking **Update** and **Restart and update**. A release must have a newer three-part version number, such as `v3.0.1`; a different test ZIP with the same version is not offered as an update. Suffix tags such as `v3.0.1-beta.1` require a manual download.

1. Stop tracking, then click **Update** to download the release and verify its ZIP checksum and packaged files. You can cancel while the download is running; the current app stays unchanged.
2. After verification, click **Restart and update**. The Hub closes, replaces its app files in the same folder, then reopens. It keeps your models, recordings, enabled options and connection settings. Press **Start tracking** when you are ready; tracking does not start automatically after the restart.
3. Open **Components** in the update window. It checks the installed Qpro module and Qpro's private Python, PyTorch and ROCm environments against the new release. Click **Update components** to apply the listed changes. Close VRCFaceTracking and wait for its module process to exit before updating its module; stop Qpro tracking and training before updating any component. Reopen VRCFaceTracking after its module changes.

Component updates preserve the installed module's streaming app and the current or legacy ROCm track. They do not install optional components you have never enabled or change another application's Python. Older installations without a completed recipe record are offered a one-time verify/update. Custom runtime overrides are left for manual maintenance in **First-time setup**. The component window shows download and validation output; an error stops the remaining updates and explains what needs attention.

PC runtime changes retain a recovery copy and restore it if installation or verification fails. An abruptly terminated process can leave that recovery copy for manual repair. ROCm updates use a separate environment and retain the prior verified environment until GPU training and inference checks pass. Several gigabytes of extra free space may be needed during updates.

The updater stages files in a temporary folder and keeps a backup for rollback if replacement fails. Verified obsolete bundled choices are replaced by the new developer model. Personal/imported pairs and their selections stay; if a number is occupied by one of them, the incoming developer model gets the next unused local number. If GitHub provides no ZIP checksum or the package does not support the Hub updater, use **View release on GitHub** and download the release ZIP manually. Older builds without the updater can be tested from a new extracted folder; keep the original folder and import working V2.0 or newer tongue models through **Model manager**.

## 3. Get the PC apps ready

If Magisk asks whether **Shell / ADB Shell** may have root access, allow it. Keep the Quest awake while setting up the Hub.

Choose either [Virtual Desktop and its PC Streamer](https://www.vrdesktop.net/) or Steam Link for your Quest-to-PC VR connection. Install [SteamVR](https://store.steampowered.com/app/250820/SteamVR/) and the [latest VRCFaceTracking from Steam](https://store.steampowered.com/app/3329480/VRCFaceTracking/) on the PC. **The current Steam VRCFaceTracking is required for either source.** Let Steam finish updating it before installing Qpro's module. After installing the matching Qpro module in section 5, try ordinary face and blink tracking before turning on Qpro's extra features.

### Virtual Desktop settings

Open **Virtual Desktop on the Quest**, connect to the PC Streamer, then open the headset app's **Streaming** tab. These options are in the headset app, not the Windows Streamer window; expand **Advanced Options** if needed.

| Setting | Use for the initial Qpro / VRChat setup |
| --- | --- |
| **Forward tracking data to PC** | **On.** Accept **Yes** when Virtual Desktop asks to share tracking data. Eye and face tracking must also be enabled in Quest settings, with permission granted to Virtual Desktop. |
| **Track controllers** | **On** when using your Touch Pro controllers, including Qpro's hands + controllers option. Turning this off prevents Virtual Desktop from adding the Quest controllers to SteamVR. |
| **Emulate Index controllers** | **Off** for the initial VRChat finger-tracking check. Virtual Desktop's native VRChat hand-tracking instructions use forwarding with Index emulation unchecked. Qpro routes optical fingers to the held controller's existing skeleton; Index emulation is not a Qpro requirement. |

These settings follow [VRCFaceTracking's Virtual Desktop setup](https://docs.vrcft.io/docs/hardware/vr/meta/quest-pro/virtual-desktop) and [Virtual Desktop's own VRChat hand-tracking release notes](https://github.com/guygodin/VirtualDesktop/releases/tag/v1.32.13). Menu wording can change between Virtual Desktop versions. Emulated Vive trackers are an optional body-tracking feature; they are not required for Qpro face tracking or fingers.

Start the PC Streamer and connect inside Virtual Desktop **before** opening VRCFaceTracking. Launch **SteamVR through Virtual Desktop**, then the SteamVR version of VRChat. In VRChat, turn on **Main Menu > Controls > Avatars Use Finger Tracking** if using optical fingers. In the Hub choose **Virtual Desktop** as the **Streaming app**, then install its matching Qpro module with VRCFaceTracking closed. Reopen VRCFaceTracking and check that ordinary face expressions move before enabling Qpro camera features.

**Hands + controllers also requires Quest hand tracking, Automatically switch between controllers and hands, and Singularity's simultaneous switch.** Turn both Quest settings on under **Settings > Hands & eyes**. The Virtual Desktop options above do not enable them for you. Follow the optional hands instructions in section 5; its compatibility check still limits the current experimental headset profile to **Virtual Desktop 1.34.22.0**. Other headset versions may work for ordinary face tracking without supporting Qpro hands.

If you want to stream the **Virtual Desktop picture over a USB cable**, Fwooffy's working setup used the Virtual Desktop **Beta** channel. In the Meta Horizon phone app or the headset's app library, long-press Virtual Desktop, open **Settings > Release Channels**, choose **Beta**, and update it. This changes VR video streaming. The Hub's USB or wireless choice below controls **tracking data**, which is separate. A newer beta can fall outside the checked hands profile; keep Hands + controllers off if its compatibility check rejects that version.

### Steam Link settings

You can choose **Steam Link** under **Streaming app** instead of Virtual Desktop. SteamVR and the latest VRCFaceTracking are still required. In Steam Link on the headset, open **Advanced Settings**, turn on **OSC**, **Share eye tracking data**, and **Share face tracking data**, then set **OSC Output Port** to **9015**. These are Steam Link's tracking settings; the Hub's **Connection type** still chooses USB or wireless ADB for Qpro's headset camera stream.

Close VRCFaceTracking and wait for its module process to exit before switching sources. In **First-time setup**, choose **Steam Link** and press **Install Steam Link module**; reopen VRCFaceTracking afterward. Installing this Qpro module removes the Qpro Virtual Desktop module. A separate LinkFT or Steam Link VRCFaceTracking module still needs to be removed through VRCFaceTracking because it competes for the same OSC port and eye/face slots. Steam Link face, blink, and mouth tracking worked after a headset restart, Qpro's camera tongue override moved in VRChat, and native Steam TongueOut worked with VRCFaceTracking running on Steam Link. To return to Virtual Desktop, choose it in the Hub, press **Install Virtual Desktop module**, then reopen VRCFaceTracking.

## 4. Connect your Quest to the Hub

Choose **one** connection type on **First-time setup**. If this is your first time using the Hub, USB is the easiest way to check that it works. You can switch to wireless later.

### Option A: USB cable

1. Plug the Quest Pro into the PC and put the headset on.
2. If a **USB debugging** message appears in the headset, choose **Always allow from this computer**, then **Allow**. This approval comes from the headset; the Hub cannot grant it for you.
3. In the Hub, set **Connection type** to **USB cable**. Qpro remembers the USB headset after checking that it is a Quest Pro. On later starts, leave the cable connected and keep the Quest awake; you should not normally need to unplug it. If the Hub says it can see the headset but has no root access, open Magisk on the Quest and allow **Shell / ADB Shell**. Then use **Refresh connection status** on the Facial tracking page.
4. If the USB connection stalls, stop Qpro tracking and training, then press **Reconnect USB** in **First-time setup**. It retries the selected USB connection and reports whether the Quest needs debugging approval, is offline, or is missing. It does not reconnect other Android devices or change to wireless automatically. A loose cable or missing USB driver still needs to be fixed separately.

To use a different Quest Pro, stop Qpro tracking and training, press **Forget USB headset**, then connect the intended headset and press **Reconnect USB** to verify and remember it. After forgetting, automatic status checks wait for that button instead of selecting the still-connected old headset again. If several USB devices are connected before a Quest Pro is remembered, disconnect the other devices for the first check. Qpro does not silently replace a remembered headset with a different one. The saved USB selection stays with this app's connection settings when the updater runs.

### Option B: Wireless ADB

1. Connect the Quest and PC to the same private Wi-Fi network. On **First-time setup**, choose **Wireless ADB (Wi-Fi)**.
2. Enter the Quest's Wi-Fi address in **Quest IP:port**. It usually looks like `192.168.1.25:5555`. The numbers before `:5555` are *your Quest's* address, so do not copy that example exactly. Press **Connect to Quest**.
3. A **Quest connected** pop-up means wireless ADB and Magisk root are ready. Activity will also say **Wireless Quest ready**. **Do not press Pair and connect.** You can unplug the USB cable, if one was attached. If a **Quest connection failed** pop-up appears, check the Quest's current IP and port, keep it awake, and read **Activity** for the exact error.
4. If the wireless connection is off but USB works, plug in USB once and press **Enable from USB**. After the Hub reports success, unplug USB and connect to the saved Quest address.

**Pair and connect** is only for a headset that shows a **six-digit wireless debugging pairing code**. Keep the pairing message open in the headset. Press **Show pairing options** in the Hub. Put the short-lived address shown next to the code into **Pairing IP:port**, put the code into **Six-digit code**, and keep the normal Quest address in **Quest IP:port**. The two port numbers are different. For example, `192.168.1.25:5555` is a normal connection address, **not** a pairing address. Use your Quest's actual address.

If pairing says `protocol fault`, open a new pairing message on the headset and use its new code and pairing port. If **Connect to Quest** already said ready, skip pairing. Your Quest's Wi-Fi address may change after a reboot; update it in the Hub if it does. Use wireless ADB only on a trusted network.

## 5. Press the setup buttons

On **First-time setup**, work down the three numbered cards:

1. **Connect your Quest Pro:** choose USB or wireless ADB and complete the connection steps in section 4.
2. **Install the PC runtime:** press **Install runtime** and wait for Activity to confirm completion. This prepares Qpro's private Python and downloads the PC parts needed for tracking. The Hub stays at your current place on the page while it runs. You do not need to install, repair, or remove another Python installation.
3. **Install your VRCFaceTracking module:** close VRCFaceTracking and wait for its module process to exit. Choose your **Streaming app**, press the matching **Install Virtual Desktop module** or **Install Steam Link module** button, wait for completion, then reopen VRCFaceTracking. Only the selected app's install button is available. Installing one Qpro module uninstalls the other Qpro source module. In VRCFaceTracking it appears as **Qpro Companion — Virtual Desktop** or **Qpro Companion — Steam Link**, with the Hub's release version. Older QproFaceTracking cards remain recognized, including names with a plain hyphen. Remove any separate Virtual Desktop or Steam Link source module through VRCFaceTracking before installing Qpro's module.

Module installation supports local app-folder junctions, directory symlinks and Windows Cloud Files. For OneDrive, choose **Always keep on this device** for the entire extracted Qpro folder. New module recovery copies are kept in `%LOCALAPPDATA%\QproFaceTracking\module-recovery`; Activity gives the exact folder after installation.

The **Qpro module** status checks the selected source's installed folder, module card and DLL against this app. **Installed · input unchecked** confirms the files match; it does not prove VRCFaceTracking has loaded them or is receiving expressions. **VRCFaceTracking: Running** only means that app is open. Check its preview for moving face expressions. If the Hub reports a different source, conflicting copy, damaged card or module update, open **Activity** for the reason, close VRCFaceTracking and install the matching module again. Restart VRCFaceTracking after a source change. A file-access or verification error needs its specific Activity diagnosis; it is not evidence by itself that the card needs reinstalling.

**Optional compatibility details:** **Check headset compatibility** reads the Quest Pro model, exact firmware build, tracking engine and gaze setup without changing headset tracking. **Show firmware details** explains which checks passed and what remains unverified. This diagnostic is useful when something fails; it is not an extra required step for ordinary face tracking or an active Magisk gaze module. Each selected Qpro feature still checks its requirements when it starts. A recognized Hub gaze engine is not proof of live input.

**Optional independent gaze:** if the Independent Eye Gaze Magisk module is active, **skip both Check gaze setup and Prepare gaze**. Once detected, the Hub shows **Provided by Magisk**, keeps its gaze switch off and disables both setup buttons. The module already handles independent gaze. If you choose the Hub's temporary independent gaze method instead, disable the Magisk module and reboot first, then refresh detection with **Reconnect USB** or **Check headset compatibility**. Use **Check gaze setup** followed by **Prepare gaze**. The popup identifies the detected gaze method and gives the next step. The Hub reads the stock eye model and tracking-engine details from that headset, then prepares a temporary model on the PC. You do not need to supply an engine file. Neither button is required when you only want tongue or pupil tracking. Prepare gaze again after a headset firmware update when using the Hub's gaze method.

The same card and result popup explain both recovery buttons: **Recover Qpro gaze** restores the previous eye-model state saved for a recorded Qpro session. **Reset legacy gaze** offers a confirmed choice of normal, nonexperimental gaze selection for an older session without a record. Neither button disables a Magisk module or uninstalls the PC module. The check reads headset settings; it does not test whether convergence works in your avatar.

The Hub checks the prepared model and headset build before restarting tracking. If the build is unsupported or changed, Activity explains why and the Hub leaves headset tracking alone. Keep the Hub's **Independent Eye Gaze** off and use ordinary eye tracking from your selected streaming app. Do not use another gaze method to bypass a failed compatibility check. Note your Quest's **exact firmware build** when asking for help. On a supported build, applying or restoring the Hub's temporary eye model restarts Meta trackingservice. The headset may briefly look frozen and lose positional tracking; this is an expected restart, not a headset crash. Wait for Activity to confirm that tracking has returned.

To remove Qpro's VRCFaceTracking add-on later, first press **Stop tracking**. Close VRCFaceTracking, wait for its module process to exit, open **Show module uninstall** on the **Install your VRCFaceTracking module** card and press **Uninstall Qpro module**. Confirm the prompt, wait for **Setup step complete**, then restart VRCFaceTracking with exactly one official module for your selected streaming app. The Hub removes verified Qpro modules and can restore a verified official Virtual Desktop module saved by an older installer. If no official module was restored, install the official Virtual Desktop or Steam Link module you need. Your recordings and models stay saved. If an older workaround copied a Qpro DLL into another module's folder, Qpro removes the verified copy and leaves that folder's other files and metadata alone; repair or remove its old card through VRCFaceTracking if needed. Removing the PC module does not change headset gaze settings or disable a Magisk module.

**AMD GPU:** After **Install runtime** succeeds, press **Install ROCm 10.1** if the Hub detects one of these discrete Radeon models on Windows 11. Windows 10 22H2 (build 19045 or later) users can explicitly enable **Try ROCm on Windows 10 (experimental)** before installing. AMD's supported Windows configuration is Windows 11; Windows 10 success depends on the card and driver and is not guaranteed. The same discrete GPU, training and inference checks still must pass. For a manual attempt, run `Install-QproRocm.ps1 -AllowExperimentalWindows10`. Older Windows versions and legacy ROCm 7.2.1 on Windows 10 are not admitted. If an older verified ROCm 7.2.1 environment is already present, the button says **Upgrade to ROCm 10.1** instead:

For the Windows 10 option, open **Show AMD compatibility details** on the **Optional AMD ROCm acceleration** card.

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

The **Controllers** sidebar page sits directly below **Facial tracking**. The shared bottom **Start tracking** / **Stop tracking** bar controls all selected features; optional installation stays on **First-time setup**.

**Hands + controllers** uses the headset cameras to track your real fingers while you hold the physical controllers. Controller position, buttons and haptics still come from the controllers. This feature is separate from VRCFaceTracking's face module, is off by default, and currently requires **Virtual Desktop** on a rooted Quest Pro. Steam Link is not supported for this option. A previous live test showed finger movement while holding controllers, but also caused a substantial frame-rate drop. The current revision still needs a sustained live performance check.

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
3. **Install Qpro's optional components once.** Install the normal **PC runtime** first, then close **SteamVR** and wait for it to exit. In **First-time setup > Optional hands and controllers**, open **Show hands setup and uninstall** and press **Install hand/controller components**. Setup downloads checksum-verified Frida 17.18.0 into Qpro's separate environment and registers Qpro's controller add-on. Repeat installation only when the Hub asks you to repair or update these components.
4. **Connect and check the VR session.** In the headset's Virtual Desktop **Streaming** tab, set **Forward tracking data to PC** and **Track controllers** on, and leave **Emulate Index controllers** off for the initial VRChat finger check. Accept the tracking-data permission prompt. Connect to the PC Streamer, then launch **SteamVR through Virtual Desktop**. A USB ADB connection alone does not start this VR stream. Launch SteamVR VRChat and turn on **Main Menu > Controls > Avatars Use Finger Tracking**. Before starting Qpro hands, set controllers down and check ordinary empty-hand tracking. Tap the controllers together twice in Virtual Desktop if needed to enter hand mode. Fingers must move in VRChat before continuing. See the Virtual Desktop settings in section 3 and [VRChat's finger-tracking instructions](https://docs.vrchat.com/docs/input-20-faq).
5. **Check this exact Virtual Desktop installation.** Select **Virtual Desktop** under **Facial tracking > Your session > Streaming app**. In **Controllers**, press **Check hand/controller compatibility**. The result gives a title, explanation and next step: yellow means unchecked or waiting, red means blocked, and green **Hand/controller prechecks passed** means the checked versions and settings passed. It does not start tracking or confirm real fingers or game performance. The admitted headset version is **Virtual Desktop 1.34.22.0**, with an exact checked PC Streamer driver. The original PC driver and the inspected **1.34.23** beta PC driver are admitted separately. Headset Virtual Desktop **1.34.23.0** still needs a checked hand layout. If your version or driver is unsupported, leave the option off and send its Activity details; reinstalling components cannot add a missing compatibility profile.
6. **Start and verify real fingers.** Turn on **Experimental hands + controllers**, leaving **Experimental Touch Pro thumb-rest input** off for this first check. Press **Start tracking**, hold the controllers with your hands visible to the headset cameras, and move individual fingers. Activity must progress beyond startup to `HANDS_READY`; that reports fresh optical data routed to at least one held controller. Check both hands in VRChat: fingers should follow your real fingers while controller movement and buttons still work. Ordinary fist, point or open-hand gestures from touching controller buttons do not establish that optical finger tracking works.
7. **Stop cleanly.** Press **Stop tracking** and wait for Activity to confirm controller cleanup before closing the Hub or restarting this feature. If cleanup is unconfirmed, keep the feature off, close SteamVR, restart headset Virtual Desktop and reopen SteamVR before checking again. If game FPS falls or SteamVR becomes unstable, stop tracking and keep this experimental option off.

**Troubleshooting hands + controllers:**

- **Only controller gestures move:** recheck Quest **Hand tracking** and **Automatically switch between controllers and hands**, Singularity's simultaneous switch and VRChat's **Avatars Use Finger Tracking** setting. Check ordinary empty-hand tracking first, then keep fingers in camera view and read Activity for `HANDS_READY` or the routing failure. Being connected over ADB or seeing a helper start is not a live finger check.
- **Settings look off in Activity even though their menu switches are on:** preliminary hand-tracking preferences can be inactive before Virtual Desktop requests simultaneous mode. The current version warns on those known inactive values and then checks actual optical input. Missing or unreadable settings, simultaneous mode other than `1`, an unsupported runtime, or missing live input still refuse startup. Check connection/root access and reconnect Virtual Desktop; do not edit the preferences by hand.
- **Unsupported version, driver or controller ABI:** keep Hands + controllers off and copy the full diagnostics. Face tracking can continue without this option. New Virtual Desktop versions need their own checked profile.
- **No fingers after startup or a disconnect:** keep the headset on, hold a controller, and keep the hand visible. Reconnect Virtual Desktop if its stream stopped, then use **Stop tracking** before retrying. The compatibility button checks installed versions and settings; it does not start tracking.

**Optional thumb-rest input:** **Experimental Touch Pro thumb-rest input** is separate from finger tracking. It adds SteamVR trackpad inputs from the controllers' thumb-rest sensors. Its first sensor profile is limited to firmware build **51503870024400340** and remains unvalidated on a live headset. On **Controllers**, the controls sit directly below the two feature toggles. Choose **Thumb-rest mode**: **Trackpad**, **Relative joystick**, **Swipe**, or **Desktop mouse**. Choose **Apply mode to**: **Left (L)**, **Right (R)**, or **Both** (default). This selection affects only the extra thumb-rest input; normal controller buttons, movement and optical fingers remain available on both sides. Keep both controllers awake during the initial thumb-rest check, even when applying the mode to one side.

Before starting input workers for a thumb-rest session, Qpro hash-checks the installed controller add-on DLL against the one bundled with this build. An outdated or unreadable copy blocks startup. If it does not match, **close SteamVR**, reinstall the controller add-on from the current build using **First-time setup > Optional hands and controllers > Install hand/controller components**, then reopen SteamVR **through Virtual Desktop**. This makes the required driver acknowledgement available. Matching files on disk is only a precheck; it does not prove SteamVR loaded that driver. Received input, driver diagnostics and live checks still matter.

Trackpad follows thumb position. Relative joystick centers on the first touch, suppresses small center jitter and returns to center after release. Swipe follows thumb speed, preserves slow continued movement, then eases away after release. To assign extra game actions for these modes, open **SteamVR Settings > Controllers > Manage Controller Bindings**, select VRChat or another app, and edit your custom binding. Look for **Qpro thumb-rest surface (extra)** on the selected controller. Its extra `trackpad` source offers position, touch, force and click; choose the app's actions yourself and save. **SteamVR binding instructions** in the Hub explains the same steps and is available before a session. No premade game assignments replace your bindings. Restart SteamVR after installation if its binding editor still shows the old profile.

**SteamVR dashboard scrolling:** the add-on installer prepares an additive dashboard binding from the installed Valve default, keeping its ordinary controls. Enable thumb-rest input, select **Swipe** and press **Start tracking** to scroll the dashboard. Its dedicated scroll source is neutral in the other modes and after **Stop tracking**. If you already chose a personal dashboard binding, the installer leaves that choice intact; select the prepared **Qpro Companion thumb-rest scrolling** default in SteamVR's dashboard bindings if you want to use it. No global SteamVR binding settings are overwritten.

**VRChat menu scrolling:** installation adds the dedicated Swipe source to a copy of a recognized VRChat binding, preserving movement, gestures, buttons and other actions. Automatic selection requires an existing current binding selection that the installer can verify. Without that selection, a recognized default can be prepared and **Activity** asks you to select it manually. Unsupported bindings stay unchanged with a warning; the installer does not guess a selection key. This prepares menu scrolling only. Extra actions in VRChat and other games still need their own per-app assignments. A prepared binding does not confirm that SteamVR loaded it or that live scrolling works; test the dashboard and VRChat menus after restarting SteamVR.

Desktop mouse moves the **Windows pointer** and uses a pressure press for left-click. It sends no extra SteamVR axes or buttons and needs no SteamVR action assignment. With **Both**, independently anchored thumb movements are summed; the shared left button remains held while either selected thumb is pressing. Releasing one thumb does not cancel the other thumb's held click. Stop tracking disables this input. Confirm pointer movement and clicks in Windows when the Hub reports **Desktop mouse input received**.

**Read each result at its own scope:** green compatibility means prechecks passed; **Live optical fingers received** reports fresh optical data routed to a held controller; **Live thumb-rest input received** / `TOUCHPAD_READY` reports selected-side sensor packets sent to PC loopback. Received packets do not prove SteamVR accepted a component, your custom bindings work, or your avatar moves. Activity's `CONTROLLER_DRIVER_STATUS` separately reports applied settings, adopted sides, successful native input calls and errors. A successful input call is still not an avatar check. Verify both hands, the chosen extra actions and sustained game performance yourself.

After thumb-rest input has been enabled, **Stop tracking** also waits for a fresh driver acknowledgement: disabled settings applied, extra input neutral, no Qpro mouse press held and no input, mouse or restoration errors. A stopped reader alone cannot confirm that cleanup. This acknowledgement does not mean SteamVR unloaded the hook or profile; close SteamVR before removing the add-on. If acknowledgement or hands restoration is missing, follow the Hub's restart instructions and retain Activity output.

**Removing the add-on:** close SteamVR, press **Uninstall controller add-on** in First-time setup, then reopen SteamVR to reload its normal controller profile. Uninstall keeps a recovery copy and removes the optional thumb-rest add-on; it does not remove the installed hands runtime components or turn off Singularity's switch.

See [the experimental controller guide](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/blob/main/src/CONTROLLER_INPUT.md) for compatibility and recovery details. Body-tracking interaction, sustained performance and newer runtime versions still need live checks.

## 6. Tongue training and refinement

**Train if you have no personal model or used a version before V2.0. You can import a working V2.0 or newer model into V3.0.0.** The two bundled choices are **Developer tongue model** and **Mustachio**; personal imports and refinements remain available.

1. For Quick refinement or Focused training, choose the model to extend under **Facial tracking > Lower-face tracking > Tongue model**. **Developer tongue model** is the default on a fresh setup; updates keep personal/imported selections and replace retired bundled choices with the new developer model. Full dataset makes a new personal model instead.
2. Open **Personalize**, whose page heading is **Tongue training/refinement**. Start with **Quick refinement**, or press **Show focused and full captures** for the other modes.
3. Follow each pose card, hold the pose steady, and press **SPACE** once per still. Save at least the on-screen minimum for every card before pressing **ENTER**. Include side and diagonal positions; missing a pose can leave a tracking gap there. Left and right mean your own sides, like your hands.
4. Choose the completed recording in the training list and press that mode's training button. Wait for Activity to say training finished. Select the resulting model before restarting tracking.

- **Quick refinement:** press **1. Record refinement**, then **2. Train personalized copy**. Its **16 tongue cards** refine a copy of the selected model.
- **Focused diagonals + facial hair:** press **1. Record focused dataset**, then **2. Train focused copy**. Its **22 tongue cards** add diagonal positions and matched hidden/visible poses for facial-hair shadows.
- **Full dataset:** press **1. Record full dataset**, then **2. Train new personal model**. Its **58 tongue cards** record the full curriculum; take breaks as needed.

If you have facial hair or wear a bandage near the mouth, keep it as normally worn. For every visible pose, check that the tongue can be seen in both camera panels. Hold one pose per still and vary your jaw or headset position slightly between stills. Record hidden poses too, so beard shadows and smiles are not mistaken for tongue movement. Training or contrast processing cannot reveal a tongue completely covered by hair. Tongue cards need VRCFaceTracking to keep sending fresh input even while expression values stay steady.

### Choosing a developer model

**Developer tongue model** adds a visibility detector trained with new clean-shaven recordings while retaining the original developer tongue direction outputs. The separate 22-card recording after reseating the same wearer's headset reduced missed visible-tongue stills from 75 to 1 across 155 stills with native TongueOut fusion, with no hidden-tongue false detections in that recording. A small tongue tip during a smile was still missed. Some older bearded poses worsened; broader wearer and facial-hair testing remain pending. This checks visibility on that wearer, not improved tongue direction or accuracy across all faces. Start with **Tongue visibility > Weighted camera + native**; **Camera only** produced false detections in this check.

**Mustachio is highly experimental and optional.** It extends the earlier developer tongue model with a focused capture from one bearded and moustached wearer. Broader facial-hair and independent-wearer tests remain pending; it may perform worse for some people. Select **Mustachio** under **Tongue model** and restart tracking to try it. Contrast processing is automatic; it does not invent hidden tongue pixels or remove hair. Quick refinement and Focused training can extend it. Those copies retain the experimental label after renaming, export and import. Full dataset starts a new personal model.

After training, **Activity** lists the weakest held-out pose cards and diagonal corners. `missed` counts visible-tongue frames marked hidden by the selected detector; `fnr` is that count divided by visible frames. These use held-out frames from the same recording, so check fresh poses or live tracking too.

Use **Model manager** to export backups and import working V2.0 or newer `.qptonguemodel` files. Older combined exports remain usable for their tongue outputs. Importing weights does not restore their original recordings. Camera recordings are personal data; share them only if you choose to.

To remove a recording, open **Show saved recordings** under its matching **Personalize** capture card. Choose **Saved recordings (including trained)**, then **Delete selected dataset…**. Confirmation permanently removes that recording, its labels/session details and prepared cache; models already trained from it remain available.

## 7. Start tracking

1. Start your selected **Virtual Desktop** or **Steam Link** app on the Quest, then SteamVR and VRCFaceTracking on the PC. Steam Link users should confirm its OSC and eye/face sharing settings above.
2. In the Hub, open **Facial tracking**. **Your session** shows readiness and **Show connection and camera settings** opens camera controls. **Eyes** contains independent gaze, pupil dilation and eyebrow movement. **An active independent-gaze Magisk module keeps the Hub gaze switch off and disabled once detected.** In **Lower-face tracking**, select **Tongue model**, enable tongue output if wanted, and choose the native individual cheek switches. Open **Controllers** in the sidebar for the controller prerequisites, compatibility check and modes. Health/backend details are on **Activity > Tracking health**. If tracking is running, press **Stop tracking** before changing the model, then restart.
3. Press **Start tracking**. Tracking camera previews default off on a fresh setup; saved preview choices are kept. To show them, open **Your session > Show connection and camera settings** and enable **Preview tracking cameras** before starting. Guided training capture still opens its pose window. Open **Activity** if you want to see whether the camera connected and which device is running the tongue model.
4. When you finish, press **Stop tracking**. Wait until Activity confirms that the live processes have stopped. If you used the Hub's gaze method, also wait for its gaze recovery result: it restores the headset eye-model state recorded before that Qpro session.

Closing the Hub stops tracking cleanly, then stops the configured PC ADB server. Other Android tools using the same ADB server will disconnect and may restart it. Finish setup, capture, training, or eye-model recovery before closing.

### Processing profiles

**Activity > Tracking health** also shows a **Module output** observation. Its heartbeat must match the selected source, installed module and running process. It expires when the module stops publishing. **Module receiving** means the module recently observed and applied a timed source or Qpro override. **Module loaded: native source available · age unknown** means Virtual Desktop shared memory is accessible but has no producer timestamp; freshness cannot be inferred from it. Steam Link packets and Qpro overrides have their own expiry checks. Confirm live expressions in VRCFaceTracking's preview and your avatar.

Under **Your session > Show connection and camera settings**, **Processing profile** controls pupil processing work separately from the shared **Camera FPS cap**. Its selection is saved and applies the next time tracking starts.

| Profile | Tongue | Pupils |
| --- | --- | --- |
| Responsive (default) | Follow the camera FPS cap; newest frame scheduling | Follow the camera FPS cap; newest frame scheduling |
| Balanced | Same as Responsive | Up to 12 eye pairs per second |
| Low CPU | Same as Responsive | Up to 8 eye pairs per second |

The presets keep model weights, image sizes, preprocessing and tongue smoothing unchanged. Balanced and Low CPU reduce pupil processing frequency, so pupil response and warm-up can take longer. Responsive remains the default for both new and existing installations. Lower rates are explicit choices, not an automatic change based on CPU usage. Rates cannot exceed the available camera feed.

### Repair or restore the PC runtime

In **First-time setup**, **Repair runtime** rebuilds the normal private CPU/NVIDIA tracking packages. **Restore saved runtime** lists retained backups, validates a selected backup before replacing the active runtime, and keeps the displaced runtime as another recovery copy. Stop tracking, capture and training before using either action. Models, recordings, enabled options and connection choices are kept. AMD ROCm uses a separate environment and its own install/update actions.

**Stopping is different from uninstalling.** Tongue and pupil output stop when Qpro tracking stops. The PC tongue model is not installed on the headset. **Individual cheek puff (1/0)** and **Individual cheek suck (1/0)** apply only while Qpro tracking runs; **Stop tracking** or closing the Hub restores the streaming app's original cheek values. Your selected cheek switches stay saved for the next session. You can start a session with just these cheek adjustments selected; no camera or Python process is needed. **Adjust eyebrow movement** and the module's built-in smirk adjustment remain active through the installed module. Turn the eyebrow switch off for native eyebrows, or uninstall the Qpro module for the official module's complete mapping. Magisk gaze modules remain active until you disable them in Magisk and reboot.

### Returning to ordinary tracking

Use **Check gaze setup** in **First-time setup** for optional troubleshooting, or before choosing the Hub's temporary gaze method. It reads the headset's current eye-model selection and active gaze methods without changing them. It is not required when an Independent Eye Gaze Magisk module is enabled; leave the Hub's **Independent Eye Gaze** off and skip both gaze setup buttons.

- **An interrupted Qpro gaze session:** After stopping Qpro tracking, use **Recover Qpro gaze** in **First-time setup**. It restores a verified session's recorded previous state. A result saying no Qpro session remains does not mean every headset modification was removed.
- **An older build left experimental gaze enabled without a recovery record:** Use **Reset legacy gaze** in **First-time setup** only when you want the normal, nonexperimental headset gaze selection. Read and confirm the prompt. This cannot recover an unknown previous selection; it explicitly chooses the normal selection. It refuses active independent-gaze Magisk modules or an unverified overlay on the eye model or its tracking engine. It does not uninstall modules or overwrite headset firmware. If a gaze Magisk module is active, disable it and reboot before retrying.
- **Compare the streaming app's original face tracking:** Stop Qpro tracking, close VRCFaceTracking and wait for its module process to exit. Use **Uninstall Qpro module**, then reopen VRCFaceTracking with exactly one official module for Virtual Desktop or Steam Link. To compare ordinary headset eye tracking too, disable any independent-gaze Magisk module and reboot the headset.

If either recovery action refuses to proceed, keep its **Activity** error and ask for help with the exact firmware build. Recovery does not add support for an unsupported eye-tracking engine.

On **Facial tracking**, open **Your session > Show connection and camera settings** for **Preview tracking cameras** and **Camera FPS cap**. Open **Lower-face tracking > Show camera settings** for **Motion smoothing** and **Tongue visibility**. Hiding the camera windows does not turn tracking off. Stop tracking before changing these settings, then start again. In **Eyes > Show eye settings and guidance**, **Pupil response** makes avatar pupil changes stronger or weaker; start near the default and adjust slowly. Pupil values are estimates for avatar animation, **not** measured eye health data. Your VRChat avatar must support pupil animation for changes to appear.

### Pupil processing and performance

Pupil tracking automatically uses a verified NVIDIA CUDA or AMD ROCm runtime when one is available. Filtering both eye images is batched on the GPU; contour fitting, quality checks and smoothing still use the CPU. If GPU processing is unavailable or fails, Qpro reports the fallback in **Activity** and continues on CPU. **Activity > Tracking health** shows whether pupil work is running on AMD, NVIDIA or CPU.

Qpro processes the latest eye pair instead of queuing old frames. Activity reports processing time, frame age and skipped work so a slow PC is easier to diagnose. Camera previews encode only the streams somebody is viewing. Close unused browser previews if performance is poor; GPU support cannot guarantee a particular frame rate on every PC.

### Individual cheek puff and suck

On **Facial tracking > Lower-face tracking**, **Individual cheek puff (1/0)** and **Individual cheek suck (1/0)** use native streaming-app signals with **1/0** separation. A clear one-sided expression becomes full strength on that side and zero on the other. A deliberate two-sided puff or suck still moves both sides. Cheek suck uses separate left/right signals; it does not use negative cheek-puff values.

Turn a switch off for that feature's original Virtual Desktop or Steam Link values. **Start tracking** applies the selected adjustments. **Stop tracking**, closing the Hub or a Hub crash restores native cheeks; the selected switches stay saved. These cheek controls do not need camera tracking, tongue training or cheek calibration. If VRCFaceTracking separates the sides but the avatar does not, check its parameters and blendshapes.

In **Facial tracking > Eyes**, **Adjust eyebrow movement** starts off, so brows use the selected source's original values. Turn it on, open **Show eye settings and guidance** and adjust **Eyebrow sensitivity** to amplify or soften the existing left and right inner raise, outer raise, and lower/pinch expressions. This cannot add movements the headset does not detect. The avatar needs matching brow parameters and blendshapes to show that detail. If the expressions move in VRCFaceTracking's preview but the avatar shows only a single brow motion, check the avatar's face tracking setup.

Smirk handling is built into both Qpro modules. A clear one-sided smile emphasizes its leading corner and suppresses the weaker side; an even smile still moves both sides. There is no separate smirk toggle in the Hub.

For tongue motion, adjust **Motion smoothing** to soften extension and retraction. The visibility hold reduces quick dropouts after a visible pose, but it cannot make a completely obscured tongue visible. Use **Focused diagonals + facial hair** to record positions that still need better coverage.

If Independent Eye Gaze fails to start or stops unexpectedly, the Hub turns it off automatically and remembers that choice. Tongue and pupil tracking continue if selected. Check **Activity** for the recovery result before trying gaze again; select **Independent Eye Gaze** manually to retry.

## Common problems

| What you see | What to try |
| --- | --- |
| “Root access unavailable” | Keep the Quest awake. In Magisk, allow **Shell / ADB Shell**, then refresh the connection. |
| USB Quest is offline or the connection stalls | Keep the headset awake. Stop Qpro tracking and training, then use **Reconnect USB** in **First-time setup**. If USB debugging is unauthorized, allow it inside the headset and choose **Always allow from this computer**. Check the cable and Windows USB driver if the device is still missing. |
| A different USB Quest Pro is connected | Stop Qpro tracking and training, then use **Forget USB headset**. Connect the intended Quest Pro and press **Reconnect USB** to verify and remember it; temporarily disconnect other USB Android devices for its first check. |
| VRCFaceTracking is running but the Qpro module needs attention | Open **Activity** for the module check's reason. Close VRCFaceTracking and wait for its module process to exit, then install the matching Qpro module. The running app status does not verify the installed module or live face input. |
| **Quest connected** pop-up, followed by a pairing error | You are already connected. Skip **Pair and connect** and start tracking. |
| **Quest connection failed** pop-up | Check the Quest's current Wi-Fi IP and port, wake the headset, and read **Activity** for the specific error. |
| `protocol fault` during pairing | Use a fresh six-digit code and the **temporary pairing port** shown on the Quest, not the usual `:5555` port. |
| Wireless stopped working after a reboot | Check the Quest's current Wi-Fi address. Wireless ADB may need to be enabled again. |
| AMD installer says PC runtime is missing | Finish **Install runtime** first, then return to **Install ROCm 10.1**. |
| ROCm setup reports a missing `torchgen` or a failed Python import | Use **Verify / repair ROCm 10.1** or **Repair ROCm 10.1** in First-time setup. Qpro checks and repairs its separate AMD environment before testing the GPU. Keep the full **Activity** error if repair fails; an import error alone does not mean your card is unsupported. |
| **Install runtime** stays on “Starting PC runtime setup” | Keep the Hub open and read **Activity**. It now shows the PowerShell launch, current setup phase, and a still-running message every 15 seconds when output stops. If PowerShell never prints its first script message, check Windows Security for a blocked `powershell.exe`, then share the Activity lines, including the PowerShell PID. |
| **Install runtime** ends with code 1 | Read the explanation and first error in **Activity**. A missing native DLL, `VCRUNTIME`, `WinError 126` or `WinError 1114` calls for installing or repairing [Microsoft's latest x64 Visual C++ Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170#latest-supported-redistributable-version), then retrying. `ModuleNotFoundError` means a Python package or app file is missing; follow Activity's runtime repair or complete-ZIP instructions instead. Code 1 has other causes too, so share the full error if it repeats. Do not delete the whole `%LOCALAPPDATA%\QproFaceTracking` folder just because the exit code is 1. |
| Steam Link source selected but no face or training labels arrive | In Steam Link **Advanced Settings**, turn on **OSC**, **Share eye tracking data**, and **Share face tracking data**; set **OSC Output Port** to **9015**. Close VRCFaceTracking and wait for its module process to exit, then press **Install Steam Link module** in the Hub. Reopen VRCFaceTracking. Remove other Steam Link VRCFaceTracking modules that would use the same port. |
| Launcher says the tracking source does not match | Close VRCFaceTracking and wait for its module process to exit. Choose the intended **Streaming app** on **First-time setup**, install its Qpro module, reopen VRCFaceTracking, then retry. |
| Both avatar cheeks move together | Confirm the **Individual cheek puff (1/0)** switch and a running Qpro session. Compare the left/right values in VRCFaceTracking's preview. If they separate there, check the avatar's cheek parameters and blendshapes. |
| AMD Ryzen integrated graphics appears during ROCm setup | Qpro checks for one of the exact discrete Radeon models in the ROCm 10.1 table above. An integrated GPU alone is not enough. Check that Windows and the AMD driver can see the discrete card; otherwise use **Install runtime** for NVIDIA CUDA or CPU. |
| Independent gaze or convergence fails but ordinary face tracking works | If you use the Magisk gaze method, keep Hub gaze off and check that module's firmware requirements; the Hub's gaze setup is not required. For the Hub's temporary method, use **Check gaze setup** and read Activity for the exact firmware build and next step. Keep Hub gaze off if its checks fail. Use only one gaze method. |
| Tongue disappears in some positions | Check the selected model and tongue output state in **Activity**. A stale result means the frame arrived too late; check the selected GPU runtime and reduce other camera work. Hidden output means the visibility gate did not accept that pose. If fresh poses still fail, keep the log and record those positions for refinement when possible. |
| Training says the prepared tongue cache uses an older mapping | Retry training in the Hub to rebuild prepared data from the saved capture. Keep the original recordings. If an older recording has an ambiguous streaming source, copy its diagnostic and identify whether it was recorded in Virtual Desktop or Steam Link before preparing it again. Existing trained models remain usable. |
| Facial hair or a bandage makes tongue training unreliable | Record a personal dataset with the same face appearance you use for tracking. Capture hidden and visible tongue poses, then check the trained model in Facial tracking. If native visibility flickers while the cameras still see your tongue, try **Camera only** under **Show camera settings > Tongue visibility**. This may help but is not guaranteed for every face or headset fit. |
| Pupils seem too jumpy | Stop tracking, open **Eyes > Show eye settings and guidance**, lower **Pupil response**, then start again. Hold your gaze steady while tracking warms up. |

If Windows will not let you delete an older extracted Qpro folder, close its Hub and camera preview first. If the folder is still in use, open **Task Manager > Details**, end a leftover `adb.exe` process, then try again. Ending `adb.exe` temporarily disconnects any other Android tools using that ADB server.

If you need help, open **Activity** and press **Copy diagnostics** or **Save log…**. Include the error, your Quest's exact firmware build, whether you used USB or wireless, and which release ZIP you downloaded. You can ask in the [community Discord](https://discord.gg/ghvuJTpRu4). The server is not owned by Fwooffy.

The Steam Link source follows the OSC mapping documented by [danwillm](https://github.com/danwillm/VRCFT-SteamLink) and the [LinkFT project](https://github.com/ykeara/LinkFT). Qpro does not require either third-party module alongside its own module.
