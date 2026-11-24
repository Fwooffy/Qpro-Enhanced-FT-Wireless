# Qpro Companion source

This checkout sends Meta Quest Pro face and eye data from Virtual Desktop or Steam Link to VRChat on Windows. **Direct VRChat** is the default output. **VRCFaceTracking backup** uses the optional Qpro module. Camera and headset tools require root.

To install the public build, download its Companion ZIP asset and follow the guide included with that release. Extract the entire folder before opening `QproFaceTracking.exe`. These setup notes describe the development build.

[Latest release](https://github.com/Fwooffy/Qpro-Companion/releases/latest) · [Beginner PDF guide](Quest_Pro_Enhanced_Face_Tracking_Guide.pdf) · [Text setup instructions](RELEASE_INSTRUCTIONS.md) · [Detailed technical notes](RELEASE_README.md)

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

## Updating Companion

The Hub checks for newer stable releases at startup and shows a popup when one is available. Choose **Update** to download and verify it, then **Restart and update** to apply it in the existing folder. Your personal/imported models, recordings, saved settings and connection choices stay in place. Native forwarding resumes when the Hub reopens. Start optional features again when ready.

Automatic checks can be turned off in the update window. **Include prereleases** is off by default. The updater only offers newer versions; another V3.1.0 test build needs a manual download.

Use **Updates > Components** for private runtimes and installed optional components. Stop tracking or training first. Module recovery is available for installed backup and older Qpro modules.

If an older Hub updater rejects a direct-output package because the legacy bridge is absent, download the complete release ZIP and extract it into a new folder. Keep the old folder and export personal models before switching; do not copy only the EXE. The branded and legacy ZIP names contain the same package, so changing the filename does not resolve that check.

## New to rooting a Quest Pro?

Start with [**Root Your Meta Quest with Singularity — a beginner guide by Fwooffy and glorpette**](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md). Fwooffy created the original guide, and glorpette made the GitHub version in the `glorpette/quest-guides` repository. It covers a Meta developer account, ADB, checking the headset's exact firmware build, Singularity, wireless debugging, and verifying root.

Before changing your headset, compare its **exact model and firmware build** with the current [Singularity documentation and releases](https://github.com/Lumince/singularity). The root guide covers several Quest models; **this face-tracking app is for Quest Pro**.

## What this checkout includes

- Receives face, brow, jaw, and blink values from Virtual Desktop or Steam Link and sends supported avatar parameters directly to local VRChat over OSC, with optional independent eye gaze and convergence.
- Adds stereo camera tongue tracking with **Developer tongue model** as the fresh-setup default, the opt-in **Mustachio** model, and Quick refinement, Focused, and Full dataset capture modes.
- Provides native individual cheek puff and suck **1/0** switches, eyebrow sensitivity, and stronger one-sided smirk handling. **Start tracking** applies selected extras. **Stop tracking** restores ordinary native forwarding.
- Carries headset camera and eye data over USB or wireless ADB. Wireless ADB requires Magisk root access for Shell / ADB Shell and a trusted private network.
- Runs the tongue model on AMD ROCm, NVIDIA CUDA, or CPU when the corresponding runtime is available. The Hub's Activity log reports the backend used.

**Experimental relative pupil dilation** has been live-tested in VRChat. It is an avatar animation estimate, not a calibrated pupil measurement.

**Steam Link** is selectable under **Streaming app**. Its native OSC input uses port **9015** with eye and face sharing enabled. This source port is separate from the automatically discovered VRChat OSC output destination. Follow the [Steam Link settings](RELEASE_INSTRUCTIONS.md#steam-link-settings). Previous live tests used the VRCFaceTracking route; the new direct output still needs live headset/avatar acceptance.

## Requirements

- Windows 10 or 11, x64.
- A Quest Pro with eye and face tracking enabled. Camera features and temporary gaze need root, Developer Mode and authorized ADB; native standard face/eye output alone does not need ADB or Python.
- For camera and headset tools, grant Magisk Superuser access to Shell / ADB Shell.
- Virtual Desktop or Steam Link, SteamVR, and VRChat with **Action Menu > OSC > Enabled**. No VRCFaceTracking installation or module is required for direct output.
- For camera features, allow internet access and free disk space for the PC runtime. The release bundles ADB, so a separate ADB installation is not needed for the Hub.

The wireless transport and AMD inference were tested on a Quest Pro with build `51503870024400340` and a Radeon RX 7900 XTX. NVIDIA CUDA hardware has also been live-tested and is functional. Eye convergence can vary by firmware; a successful root or eye-camera connection does not establish convergence support on every build. If the Hub's independent-gaze patch fails, [Singularity's Quest Pro Independent Eye Gaze Magisk module](https://github.com/Lumince/singularity) is an alternative reported working on Horizon OS v2.7. Install Magisk OverlayFS first, then the gaze module through **Singularity > Apps/Modules > Magisk repo**; use only one gaze method at a time. When an active gaze Magisk module is detected, the Hub keeps its own **Independent eye gaze + convergence** off and disabled, along with **Check gaze setup** and **Prepare gaze**. Skip those steps; tongue, pupil dilation and eyebrow controls work separately. After changing the module, reboot the headset and use **Check USB connection** or **Check headset compatibility** to refresh its status. **Stop tracking** does not disable Magisk modules.

## First run

On the direct route, native face and eyes stream automatically while Companion is open, the streaming app shares data and VRChat accepts it. Direct native output can run without ADB or Python.

1. Extract the complete release ZIP. Open `QproFaceTracking.exe` from the extracted folder, not from inside the ZIP.
2. Open **First-time setup > VRChat output**. Leave **Direct to VRChat** selected and choose your **Streaming app**. Close VRCFaceTracking, open VRChat and enable **Action Menu > OSC > Enabled**. Use **Check VRChat connection** and verify that your avatar moves.
3. For camera or headset tools, open **Headset** and choose **USB cable** or **Wireless ADB (Wi-Fi)**. Enter your Quest's address if using Wi-Fi.
4. For camera tracking or training, open **PC runtime** and choose **Install runtime**. It prepares Qpro's private Python 3.12. Eligible AMD users can then try **Install ROCm 10.1** under optional AMD setup.
5. Independent eye gaze starts off. With Singularity's gaze module active, leave it off. To use the Hub's temporary gaze method, connect the rooted headset and use **Check gaze setup**, then **Prepare gaze** after its checks pass.
6. Use **Choose tracking features** to open **Facial tracking**. Select extras and press **Start tracking**. **Stop tracking** ends extras. On the direct route, close Companion to stop native forwarding.

For **VRCFaceTracking backup**, select that route, exit VRCFaceTracking and use **Install Qpro backup module** when available. Installation backs up recognized source modules. Reopen VRCFaceTracking to run the installed module. Both routes need VRChat and OSC, so check them before switching. [Backup setup and recovery](RELEASE_INSTRUCTIONS.md#optional-vrcfacetracking-backup).

Applying or restoring the Hub's independent gaze model briefly restarts Meta trackingservice. The headset can appear frozen and momentarily lose positional tracking while it returns; this is expected, not a headset crash. Wait for the Hub's Activity log to confirm tracking has resumed.

The Hub contains the setup controls and opens on First-time setup on the first launch of an extracted copy, then on Facial tracking. The `.cmd` troubleshooting helpers are grouped in `Helpers` in the ZIP. Camera preview is off by default; **Facial tracking > Your session > Show connection and camera settings > Preview tracking cameras** can show camera windows without changing tracking output, and your choice is saved. The release ZIP has the [beginner PDF guide](Quest_Pro_Enhanced_Face_Tracking_Guide.pdf) in its main folder. It covers USB, wireless pairing, the PC runtime, and tracking; the [technical notes](RELEASE_README.md) give more detail.

If Windows will not let you delete an older extracted Qpro folder, close its Hub and camera preview first. If the folder is still in use, open Task Manager > Details and end a leftover `adb.exe` process, then try deleting the old folder again. Ending `adb.exe` temporarily disconnects other Android tools using that ADB server.

**Launch with SteamVR** is off by default. To enable or disable it, start SteamVR, then check or clear **First-time setup > Launch with SteamVR** in Companion. SteamVR opens Companion on future starts. Optional features still require **Start tracking**. An update in the same folder keeps the setting. After moving the folder, open Companion from its new location and enable the option there.

## Models, data, and source

The two bundled choices are **Developer tongue model** and **Mustachio**. Developer tongue model is the default on a fresh setup. Its new clean-shaven visibility detector retains the original developer tongue direction outputs. On a separate 155-still recording after reseating the same wearer's headset, native fusion reduced visible-tongue misses from 75 to 1 with no hidden-tongue false detections. One small tongue tip during a smile was still missed. Some older bearded poses worsened; broader wearer and facial-hair checks remain pending. Start with **Tongue visibility > Weighted camera + native**; Camera only produced false detections in that check.

**Mustachio** is a separate, opt-in, highly experimental extension of the earlier developer model trained with one bearded and moustached wearer. Independent clean-shaven validation and broader wearer testing are pending; it may perform worse for some people. Its contrast processing cannot recover tongue detail completely hidden by hair.

Quick refinement and Focused training extend the model selected under **Facial tracking > Lower-face tracking > Tongue model**. Copies made from Mustachio retain their experimental status through training, renaming, export, and import. Full dataset makes a new personal model instead. Updates keep personal/imported models and selections; verified obsolete bundled choices move to the new developer model. The incoming developer model uses an unused local number if a personal model occupies its number.

Personal camera captures, training arrays, saved headset addresses, and generated eye patches are not included in the release ZIP. The two approved bundled model pairs are included; raw recordings stay private. Share camera captures only with the wearer's permission. Maintainers can use the source-only [developer tongue training guide](DEVELOPER_TONGUE_TRAINING.md) to evaluate further candidates against independent captures before promoting them.

**If you used a Qpro version before V2.0, record and train a new tongue model.** Working V2.0 or newer models can be exported and imported into V3.1.0 through **Model manager**, without retraining. Keep older exports as backups; importing a pre-V2.0 model does not replace a new capture and training run. The [beginner PDF guide](Quest_Pro_Enhanced_Face_Tracking_Guide.pdf) walks through the steps.

This repository holds the editable source. Its release build also needs larger assets distributed with the ZIP. See [contributing](CONTRIBUTING.md), [third-party notices](THIRD_PARTY_NOTICES.md), and the [license](LICENSE) before redistributing changes. The [upstream README](UPSTREAM-README.md) retains the original project's notes, including older USB-oriented instructions.

The Windows Hub is in [`qpro-hub/`](qpro-hub/), the optional VRCFaceTracking backup module in [`vrcft-gaze-bridge/`](vrcft-gaze-bridge/), and the Virtual Desktop label bridge in [`vd-label-bridge/`](vd-label-bridge/). Root-level Python and PowerShell files handle capture, calibration, training, and runtime setup. [`tests/`](tests/) contains mapping and package checks; root-level `test_*.py` files cover the Python modules. `build-release.ps1` creates the runnable ZIP, while `build-github-source.ps1` prepares the public source export. Generated builds and private captures stay out of the source export.

## Credits

- [Yeusepe / QFTPlus](https://github.com/Yeusepe/QFTPlus) documented runtime interfaces and controller sensor layouts researched for the independently authored experimental hands/controller prototype.
- [n0tmast3r](https://github.com/n0tmast3r/Qpro-Enhanced-FT) created the original Qpro-Enhanced-FT project.
- [Lumince and Singularity contributors](https://github.com/Lumince/singularity) created the headset root project used by this workflow.
- [danwillm](https://github.com/danwillm/VRCFT-SteamLink) and the [LinkFT contributors](https://github.com/ykeara/LinkFT) documented the Steam Link tracking source used by Qpro's module.
- **Fwooffy** created the original [beginner Quest root guide](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md). **[glorpette](https://github.com/glorpette/quest-guides)** made and hosts its GitHub version.

For help, share relevant **Activity** lines in the [community Discord](https://discord.gg/ghvuJTpRu4). The server is not owned by Fwooffy. This project is unaffiliated with Meta, Virtual Desktop, VRCFaceTracking, or VRChat.

## Tongue training and native cheeks

On **Personalize**, **Tongue training/refinement** offers **16** Quick, **22** Focused and **58** Full tongue cards. These teach visibility, extension and direction. Current captures do not append camera-cheek cards or train cheek outputs. Historical combined recordings and imported models remain usable for their tongue data; preserve the originals.

On **Facial tracking > Lower-face tracking**, **Individual cheek puff (1/0)** and **Individual cheek suck (1/0)** adjust the streaming app's native signals. A clear leading side reaches full strength and the other stays at zero; deliberate two-sided expressions can move both. Off passes through the original values. **Start tracking** applies selected adjustments. **Stop tracking** returns to ordinary native forwarding. These controls need no cheek calibration or camera model.

Pupil image filtering can run on verified NVIDIA CUDA or AMD ROCm devices. Quality checks, contour geometry and smoothing remain on CPU. The CPU fallback reuses filtering work when an eye image repeats exactly. Frame cadence and pupil scoring still follow the chosen profile. Activity reports the backend and timings.

Closing Companion stops Direct VRChat and requests a final OSC reset. A crash can interrupt that reset. On the backup route, stop native output in VRCFaceTracking.
