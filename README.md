# QproFaceTracking — AMD/NVIDIA Wireless Edition

Experimental face-tracking tools for a **rooted Quest Pro** on Windows. This edition builds on [n0tmast3r's Qpro-Enhanced-FT](https://github.com/n0tmast3r/Qpro-Enhanced-FT) and adds wireless ADB support and an optional AMD ROCm path for tongue tracking and training. The original NVIDIA CUDA and CPU paths remain in the code.

**[Download the latest release](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/latest)** · [Detailed setup guide](RELEASE_README.md) · [Community support](https://discord.com/invite/hantnor)

Download the **ZIP asset** from Releases and extract the entire folder. GitHub's automatically generated source archives do not include the runnable Hub, demonstration model, Android tools, or bundled Python installer.

## New to rooting a Quest Pro?

Start with [**Root Your Meta Quest with Singularity — a beginner guide by Fwooffy and glorpette**](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md). Fwooffy created the original guide, and glorpette made the GitHub version in the `glorpette/quest-guides` repository. It covers a Meta developer account, ADB, checking the headset’s exact firmware build, Singularity, wireless debugging, and verifying root.

Before changing your headset, compare its **exact model and firmware build** with the current [Singularity documentation and releases](https://github.com/Lumince/singularity). The root guide covers several Quest models; **this face-tracking app is for Quest Pro**.

## What the public release does

- Preserves Virtual Desktop's normal face, brow, jaw, and blink tracking while adding optional independent eye gaze and convergence.
- Adds stereo camera tongue tracking with a demonstration model that can be personalized through a quick refinement or full capture.
- Carries headset camera and eye data over USB or wireless ADB. Wireless ADB requires Magisk root access for Shell / ADB Shell and a trusted private network.
- Runs the tongue model on AMD ROCm, NVIDIA CUDA, or CPU when the corresponding runtime is available. The Hub's Activity log reports the backend used.

**Experimental relative pupil dilation** is being tested in local builds. Check a release's notes before expecting it in the downloadable version; it is an avatar animation estimate, not a calibrated pupil measurement.

## Requirements

- Windows 10 or 11, x64.
- A rooted Quest Pro with eye and face tracking enabled, Developer Mode on, and an authorized ADB connection.
- Magisk Superuser access granted to Shell / ADB Shell.
- Virtual Desktop, SteamVR, and VRCFaceTracking for the tracking workflow.
- Internet access and free disk space for the PC runtime. The release bundles ADB, so a separate ADB installation is not needed for the Hub.

The wireless transport and AMD inference were tested on a Quest Pro with build `51503870024400340` and a Radeon RX 7900 XTX. NVIDIA support is retained but has not been live-tested in this edition. Eye convergence can vary by firmware; a successful root or eye-camera connection does not establish convergence support on every build.

## First run

1. Extract the complete release ZIP. Open `QproFaceTracking.exe` from the extracted folder, not from inside the ZIP.
2. Use the Hub’s **Set up PC runtime** action. It creates a separate Qpro Python environment. If a compatible 64-bit Python 3.12 is already installed, setup can use it as the base without replacing its packages.
3. For an AMD GPU, close the Hub and run `Install-AMD-ROCm.cmd`, then reopen the Hub. NVIDIA and CPU users skip this helper.
4. Close VRCFaceTracking, use **Install/update bridge** in the Hub, then restart VRCFaceTracking.
5. For independent gaze, connect the rooted headset and use **Prepare gaze from headset**. The Hub makes a temporary patch from your headset’s own eye model.
6. Start Virtual Desktop, SteamVR, and VRCFaceTracking. Confirm ordinary face tracking works, select the features you want in the Hub, then press **Apply and start selected**.
7. When finished, press **Stop and restore stock** and wait for the Activity log to confirm cleanup.

For cable-free sessions after rooting, use `Connect-QproWireless.cmd` (or `Pair-QproWireless.cmd` if pairing is needed), followed by `Launch-QproWireless.cmd`. The [detailed setup guide](RELEASE_README.md) explains pairing, AMD and NVIDIA setup, and troubleshooting.

## Models, data, and source

The bundled tongue model and eye profile are **developer demonstrations**, so alignment and tongue detection may differ for another wearer. Quick refinement is a practical starting point. Personal captures, trained models, saved headset addresses, and generated eye patches are not included in the release ZIP. Camera captures are sensitive: share them only with the wearer’s permission.

This repository holds the editable source. Its release build also needs larger assets distributed with the ZIP. See [contributing](CONTRIBUTING.md), [third-party notices](THIRD_PARTY_NOTICES.md), and the [license](LICENSE) before redistributing changes. The [upstream README](UPSTREAM-README.md) retains the original project’s notes, including older USB-oriented instructions.

## Credits

- [n0tmast3r](https://github.com/n0tmast3r/Qpro-Enhanced-FT) created the original Qpro-Enhanced-FT project.
- [Lumince and Singularity contributors](https://github.com/Lumince/singularity) created the headset root project used by this workflow.
- **Fwooffy** created the original [beginner Quest root guide](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md). **[glorpette](https://github.com/glorpette/quest-guides)** made and hosts its GitHub version.

This project is unaffiliated with Meta, Virtual Desktop, VRCFaceTracking, or VRChat.
