# QproFaceTracking V3.0.0

A Windows Hub for enhanced face tracking on a **rooted Meta Quest Pro**, based on [Qpro-Enhanced-FT by n0tmast3r](https://github.com/n0tmast3r/Qpro-Enhanced-FT). It supports Virtual Desktop or Steam Link, USB or wireless ADB, tongue tracking with NVIDIA CUDA, experimental AMD ROCm support, and CPU fallback. Optional features include independent eye gaze, relative pupil animation, individual cheek puff and suck, and eyebrow sensitivity.

**V3.0.0 is being prepared as a release candidate.** See the [V3.0.0 release notes](src/RELEASE_NOTES_V3.0.0.md) for the changes and experimental features. The Releases page remains the place to download published builds.

**Download the runnable ZIP from [Releases](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/latest).** Extract it before opening `QproFaceTracking.exe`. GitHub's automatic source ZIP does not include the Hub executable or packaged models and tools.

The **latest [VRCFaceTracking from Steam](https://store.steampowered.com/app/3329480/VRCFaceTracking/)** is required. If you used a Qpro version before V2.0, record and train a new tongue model. Working V2.0 or newer models can be exported and imported into V3.0.0 through **Model manager**.

The developer **v8** tongue model remains the default. **Mustachio** is a separate, opt-in, highly experimental extension of v8 trained with one bearded and moustached wearer. It has no independent clean-shaven validation yet. Quick refinement and Focused training extend the tongue model selected under **Live tracking > Lower-face model**, including Mustachio; copies made from an experimental model retain that status.

## Guides

- [Text setup instructions](src/RELEASE_INSTRUCTIONS.md)
- [Detailed release notes](src/RELEASE_README.md)
- [Quest Pro rooting guide by glorpette and Fwooffy](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md)

## Repository layout

The runnable release keeps the Hub and guide in its main folder. This GitHub repository keeps the development source, tests, and build scripts together in [`src/`](src/). Building a package requires the .NET 10 SDK, an installed VRCFaceTracking, and the binary assets from an extracted Qpro release. Older releases contain a Python installer instead of the NuGet archive required by this build, so the script downloads the pinned official Python 3.12.10 archive when needed and verifies its SHA-256 hash. For an offline build, supply `python-runtime/python.3.12.10.nupkg` in the source folder or asset root. In PowerShell, replace both example paths with paths on your PC:

```powershell
cd src
.\build-release.ps1 -VrcftInstallDir '<your VRCFaceTracking install folder>' -AssetRoot '<extracted Qpro release>\QproRuntime'
```

For help, share relevant Hub **Activity** lines in the [community Discord](https://discord.gg/ghvuJTpRu4). The server is not owned by Fwooffy. QproFaceTracking is unaffiliated with Meta, Virtual Desktop, VRCFaceTracking, and VRChat.

## Credits

- [Yeusepe / QFTPlus](https://github.com/Yeusepe/QFTPlus) documented runtime interfaces and controller sensor layouts researched for the independently authored experimental hands/controller prototype.
- [n0tmast3r](https://github.com/n0tmast3r/Qpro-Enhanced-FT) created the original Qpro-Enhanced-FT project that this edition is based on.
- Fwooffy maintains this edition and wrote its beginner face tracking guide.
- [Lumince and the Singularity contributors](https://github.com/Lumince/singularity) created the headset root project used by this workflow.
- [danwillm](https://github.com/danwillm/VRCFT-SteamLink) and the [LinkFT contributors](https://github.com/ykeara/LinkFT) documented the Steam Link tracking source used by Qpro's module.
- Fwooffy wrote the original [beginner Quest root guide](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md), and [glorpette](https://github.com/glorpette/quest-guides) made and hosts its GitHub version.

## Lower-face calibration and GPU pupil processing

Quick refinement and Full dataset include 21 cheek camera cards and train separate left/right puff outputs alongside tongue tracking. Select a combined model under **Live tracking > Lower-face model** and enable **Camera cheek puff (experimental)** to try them. Existing tongue-only models and native cheek calibration remain available. Camera cheek strengths range from 0 to 1 and need a personal capture when another face or fit differs.

Pupil image filtering can use a verified NVIDIA CUDA or AMD ROCm runtime. Geometry and smoothing still run on CPU, with automatic CPU fallback and backend/performance messages in **Activity**. The camera loop uses the latest frames and avoids encoding unused previews.
