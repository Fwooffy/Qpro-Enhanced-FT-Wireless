# QproFaceTracking V2.1.2 - release candidate source

Face-tracking tools for a **rooted Meta Quest Pro** on Windows, based on [n0tmast3r's Qpro-Enhanced-FT](https://github.com/n0tmast3r/Qpro-Enhanced-FT). This source prepares the V2.1.2 release candidate: Virtual Desktop or Steam Link, USB or wireless ADB, NVIDIA CUDA, experimental AMD ROCm support, and CPU fallback. **The V2.1.2 candidate has not been published as a GitHub release.** See [V2.1.2 release notes](RELEASE_NOTES_V2.1.2.md) for the proposed release description.

**[Download the latest release](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/latest)** · [Beginner PDF guide](Quest_Pro_Enhanced_Face_Tracking_Guide.pdf) · [Text setup instructions](RELEASE_INSTRUCTIONS.md) · [Detailed technical notes](RELEASE_README.md) · [Community Discord](https://discord.gg/ghvuJTpRu4)

Download the **ZIP asset** from Releases and extract the entire folder. GitHub's automatically generated source archives do not include the runnable Hub, demonstration model, Android tools, or bundled private Python runtime.

## New to rooting a Quest Pro?

Start with [**Root Your Meta Quest with Singularity — a beginner guide by Fwooffy and glorpette**](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md). Fwooffy created the original guide, and glorpette made the GitHub version in the `glorpette/quest-guides` repository. It covers a Meta developer account, ADB, checking the headset's exact firmware build, Singularity, wireless debugging, and verifying root.

Before changing your headset, compare its **exact model and firmware build** with the current [Singularity documentation and releases](https://github.com/Lumince/singularity). The root guide covers several Quest models; **this face-tracking app is for Quest Pro**.

## What this candidate includes

- Receives face, brow, jaw, and blink values through the selected Virtual Desktop or Steam Link Qpro module, with optional independent eye gaze and convergence.
- Adds stereo camera tongue tracking with developer v8 as the default, an opt-in Mustachio model, and Quick refinement, Focused, and Full dataset capture modes.
- Provides individual cheek puff and suck, source-specific cheek calibration and developer baselines, eyebrow sensitivity, and stronger one-sided smirk handling.
- Carries headset camera and eye data over USB or wireless ADB. Wireless ADB requires Magisk root access for Shell / ADB Shell and a trusted private network.
- Runs the tongue model on AMD ROCm, NVIDIA CUDA, or CPU when the corresponding runtime is available. The Hub's Activity log reports the backend used.

**Experimental relative pupil dilation** has been live-tested in VRChat. It is an avatar animation estimate, not a calibrated pupil measurement.

**Steam Link** is selectable under **Streaming app** in this candidate. Its Qpro module uses OSC port **9015** with eye and face sharing enabled. Steam Link face, blink, and mouth tracking, native TongueOut, and Qpro's camera tongue override have been checked live on a Quest Pro. Follow the [Steam Link settings](RELEASE_INSTRUCTIONS.md#steam-link-settings) before installing its module.

## Requirements

- Windows 10 or 11, x64.
- A rooted Quest Pro with eye and face tracking enabled, Developer Mode on, and an authorized ADB connection.
- Magisk Superuser access granted to Shell / ADB Shell.
- Virtual Desktop or Steam Link, SteamVR, and the [latest VRCFaceTracking from Steam](https://store.steampowered.com/app/3329480/VRCFaceTracking/) for the tracking workflow. Let Steam finish updating it before installing the Qpro module.
- Internet access and free disk space for the PC runtime. The release bundles ADB, so a separate ADB installation is not needed for the Hub.

The wireless transport and AMD inference were tested on a Quest Pro with build `51503870024400340` and a Radeon RX 7900 XTX. NVIDIA CUDA hardware has also been live-tested and is functional. Eye convergence can vary by firmware; a successful root or eye-camera connection does not establish convergence support on every build. If the Hub's independent-gaze patch fails, [Singularity's Quest Pro Independent Eye Gaze Magisk module](https://github.com/Lumince/singularity) is an alternative reported working on Horizon OS v2.7. Install Magisk OverlayFS first, then the gaze module through **Singularity > Apps/Modules > Magisk repo**; use only one gaze method at a time.

## First run

1. Extract the complete release ZIP. Open `QproFaceTracking.exe` from the extracted folder, not from inside the ZIP.
2. In **First-time setup**, choose **USB cable** or **Wireless ADB (Wi-Fi)**. If you choose wireless, enter the Quest's IP and use the built-in connect or pairing controls. The Hub saves your choice and uses it for tracking.
3. Use **Install runtime**. It prepares Qpro's private Python 3.12 and tracking environment, whether another Python is installed or not. On an eligible discrete AMD GPU with Windows 11, use **Install ROCm 10.1** afterward. Its experimental Qpro environment becomes active only after GPU inference and training checks pass. NVIDIA and CPU users skip ROCm.
4. Close VRCFaceTracking and wait for its module process to exit. Choose your **Streaming app** and press its matching **Install Virtual Desktop module** or **Install Steam Link module** button; installing one removes the other Qpro source module. Reopen VRCFaceTracking afterward.
5. Independent Eye Gaze starts off. If you use the Hub's temporary gaze route, connect the rooted headset and press **Prepare gaze**. It reads your headset's model and engine details without needing a manually supplied engine file. Skip this if Singularity's gaze module is active.
6. Start the selected Virtual Desktop or Steam Link app, SteamVR, and VRCFaceTracking. Confirm ordinary face tracking works, select the features you want in the Hub, then press **Start tracking**.
7. When finished, press **Stop tracking** and wait for the Activity log to confirm cleanup.

Applying or restoring the Hub's independent gaze model briefly restarts Meta trackingservice. The headset can appear frozen and momentarily lose positional tracking while it returns; this is expected, not a headset crash. Wait for the Hub's Activity log to confirm tracking has resumed.

The Hub contains the setup controls and opens on First-time setup on the first launch of an extracted copy, then on Live tracking. The `.cmd` troubleshooting helpers are grouped in `Helpers` in the ZIP. Camera preview is off by default; **Live tracking > Your session > Show connection and camera settings > Preview tracking cameras** can show camera windows without changing tracking output, and your choice is saved. The release ZIP has the [beginner PDF guide](Quest_Pro_Enhanced_Face_Tracking_Guide.pdf) in its main folder. It covers USB, wireless pairing, the PC runtime, and tracking; the [technical notes](RELEASE_README.md) give more detail.

If Windows will not let you delete an older extracted Qpro folder, close its Hub and camera preview first. If the folder is still in use, open Task Manager > Details and end a leftover `adb.exe` process, then try deleting the old folder again. Ending `adb.exe` temporarily disconnects other Android tools using that ADB server.

## Models, data, and source

The developer **v8** tongue model remains the default. **Mustachio** is a separate, opt-in, highly experimental extension of v8 trained with one bearded and moustached wearer. Independent clean-shaven validation and broader wearer testing are pending; it may perform worse for some people. Its contrast processing cannot recover tongue detail completely hidden by hair.

Quick refinement and Focused training extend the model selected under **Live tracking > Lower-face model**. Copies made from Mustachio retain their experimental status through training, renaming, export, and import. Full dataset makes a new personal model instead.

Personal camera captures, training arrays, personal cheek profiles, saved headset addresses, and generated eye patches are not included in the release ZIP. Approved model weights and developer cheek baselines are included; raw recordings stay private. Share camera captures only with the wearer's permission. Maintainers can use the source-only [developer tongue training guide](DEVELOPER_TONGUE_TRAINING.md) to evaluate further candidates against independent captures before promoting them.

**If you used a Qpro version before V2.0, record and train a new tongue model.** Working V2.0 through V2.0.2 models can be exported and imported into V2.1.2 through **Model manager**, without retraining. Keep older exports as backups; importing a pre-V2.0 model does not replace a new capture and training run. The [beginner PDF guide](Quest_Pro_Enhanced_Face_Tracking_Guide.pdf) walks through the steps.

This repository holds the editable source. Its release build also needs larger assets distributed with the ZIP. See [contributing](CONTRIBUTING.md), [third-party notices](THIRD_PARTY_NOTICES.md), and the [license](LICENSE) before redistributing changes. The [upstream README](UPSTREAM-README.md) retains the original project's notes, including older USB-oriented instructions.

The Windows Hub is in [`qpro-hub/`](qpro-hub/), the VRCFaceTracking module in [`vrcft-gaze-bridge/`](vrcft-gaze-bridge/), and the Virtual Desktop label bridge in [`vd-label-bridge/`](vd-label-bridge/). Root-level Python and PowerShell files handle capture, calibration, training, and runtime setup. [`tests/`](tests/) contains mapping and package checks; root-level `test_*.py` files cover the Python modules. `build-release.ps1` creates the runnable ZIP, while `build-github-source.ps1` prepares the public source export. Generated builds and private captures stay out of the source export.

## Credits

- [Yeusepe / QFTPlus](https://github.com/Yeusepe/QFTPlus) documented runtime interfaces and controller sensor layouts researched for the independently authored experimental hands/controller prototype.
- [n0tmast3r](https://github.com/n0tmast3r/Qpro-Enhanced-FT) created the original Qpro-Enhanced-FT project.
- [Lumince and Singularity contributors](https://github.com/Lumince/singularity) created the headset root project used by this workflow.
- [danwillm](https://github.com/danwillm/VRCFT-SteamLink) and the [LinkFT contributors](https://github.com/ykeara/LinkFT) documented the Steam Link tracking source used by Qpro's module.
- **Fwooffy** created the original [beginner Quest root guide](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md). **[glorpette](https://github.com/glorpette/quest-guides)** made and hosts its GitHub version.

For help, share relevant **Activity** lines in the [community Discord](https://discord.gg/ghvuJTpRu4). The server is not owned by Fwooffy. This project is unaffiliated with Meta, Virtual Desktop, VRCFaceTracking, or VRChat.

## Lower-face calibration and camera cheeks

Quick refinement has 37 cards (16 tongue and 21 cheek); Full dataset has 79 cards (58 tongue and 21 cheek). Cheek cards use guided pose strengths rather than native cheek labels. The optional **Developer tongue + cheeks** model preserves developer v8 tongue weights and adds a cheek head trained on one wearer. Enable **Camera cheek puff (experimental)** after selecting a compatible combined model to use separate continuous camera strengths. Keep the original v8 model available while testing; gentle puff responses and other wearers need further validation.

Pupil image filtering can run on verified NVIDIA CUDA or AMD ROCm devices. Quality checks, contour geometry and smoothing remain on CPU. The worker processes the latest eye pair, falls back to CPU when needed, and reports timing and backend in Activity. Unused MJPEG streams are not encoded.
