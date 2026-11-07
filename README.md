# Qpro Companion V3.0.1

A Windows companion for face tracking and optional controller features on a **rooted Meta Quest Pro**, based on [Qpro-Enhanced-FT by n0tmast3r](https://github.com/n0tmast3r/Qpro-Enhanced-FT). It supports Virtual Desktop or Steam Link, USB or wireless ADB, tongue tracking with NVIDIA CUDA, experimental AMD ROCm support, and CPU fallback. Optional features include independent eye gaze, relative pupil animation, individual cheek puff and suck, eyebrow sensitivity, and experimental Virtual Desktop hands + controllers and Touch Pro thumb-rest input.

**V3.0.0 is released.** See the [published release notes](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/tag/v3.0.0) for the changes and experimental features.

**[Download QproCompanion.V3.0.0.zip](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/releases/download/v3.0.0/QproCompanion.V3.0.0.zip).** Extract it before opening `QproFaceTracking.exe`. GitHub's automatic source ZIP does not include the Hub executable or packaged models and tools.

Qpro Companion is the new public name. The executable, existing app/data folders and `.qptonguemodel` format retain their QproFaceTracking names for update compatibility.

The **latest [VRCFaceTracking from Steam](https://store.steampowered.com/app/3329480/VRCFaceTracking/)** is required. If you used a Qpro version before V2.0, record and train a new tongue model. Working V2.0 or newer models can be exported and imported into V3.0.1 through **Model manager**.

**Developer tongue model** is the default and includes the accepted v15 visibility improvement from new clean-shaven recordings while preserving the original direction outputs. **Mustachio** is a separate, opt-in, highly experimental v9 extension trained with one bearded and moustached wearer. Broader wearer and facial-hair validation remain pending. Quick refinement and Focused training extend the model selected under **Facial tracking > Lower-face tracking > Tongue model**, including Mustachio; copies made from an experimental model retain that status. Full dataset trains a new personal model.

An active independent-gaze Magisk module supplies gaze itself. When detected, the Hub keeps **Independent eye gaze + convergence** off and disabled; skip **Check gaze setup** and **Prepare gaze**. Tongue, pupil dilation and eyebrow controls still work separately. After changing the Magisk module, reboot the headset and use **Reconnect USB** or **Check headset compatibility** to refresh its status.

## Updating Companion

The Hub checks for newer stable releases at startup and shows a popup when one is available. Choose **Update** to download and verify it, then **Restart and update** to apply it in the existing folder. Your personal/imported models, recordings, saved settings and connection choices stay in place. Tracking does not restart automatically.

Automatic checks can be turned off in the update window. **Include prereleases** is off by default. The updater only offers newer versions; another V3.0.1 test build needs a manual download.

Use **Updates > Components** separately for installed Qpro modules and private Python/PyTorch/ROCm runtimes. Stop tracking or training first. To replace the Qpro VRCFaceTracking module, close VRCFaceTracking and wait for its module helper to exit.

## Guides

- [Text setup instructions](src/RELEASE_INSTRUCTIONS.md)
- [Technical notes](src/RELEASE_README.md)
- [Quest Pro rooting guide by glorpette and Fwooffy](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md)

## Repository layout

The runnable release keeps the Hub and guide in its main folder. Development source, tests and build scripts are in [`src/`](src/). Building a package requires the **.NET 10 SDK**, **portable Zig 0.15.2**, an installed **VRCFaceTracking**, and binary assets from an extracted Qpro release. Source archives do not contain the runnable tools or model weights.

Build the native controller stage from the current source first; packaging verifies its source, resource and binary hashes. For a release containing both bundled models, supply separate approved **Developer tongue model** and **Mustachio** folders. Each must contain exactly one model's gate checkpoint, direction checkpoint and sanitized metadata; do not use a folder containing personal captures or several model pairs. `AssetRoot` alone does not select or copy these models.

Older release assets contain a Python installer instead of the required NuGet archive. The build downloads pinned official Python 3.12.10 when needed and verifies its SHA-256. For an offline build, supply `python-runtime/python.3.12.10.nupkg` in the source folder or asset root. Replace the example paths below with paths on your PC:

```powershell
cd src
.\controller-input\build-native.ps1 -Zig 'C:\path\to\zig.exe'
$releaseBuild = @{
    VrcftInstallDir = '<your VRCFaceTracking install folder>'
    AssetRoot = '<extracted Qpro release>\QproRuntime'
    ControllerInputAssetRoot = '.\artifacts\controller-native-build'
    DeveloperModelRoot = '<approved Developer tongue model folder>'
    ExperimentalTongueModelRoot = '<approved Mustachio model folder>'
}
.\build-release.ps1 @releaseBuild
```

For help, share relevant Hub **Activity** lines in the [community Discord](https://discord.gg/ghvuJTpRu4). The server is not owned by Fwooffy. Qpro Companion is unaffiliated with Meta, Virtual Desktop, VRCFaceTracking, and VRChat.

## Credits

- [Yeusepe / QFTPlus](https://github.com/Yeusepe/QFTPlus) documented runtime interfaces and controller sensor layouts researched for the independently authored experimental hands/controller prototype.
- [n0tmast3r](https://github.com/n0tmast3r/Qpro-Enhanced-FT) created the original Qpro-Enhanced-FT project that this edition is based on.
- Fwooffy maintains this edition and wrote its beginner face tracking guide.
- [Lumince and the Singularity contributors](https://github.com/Lumince/singularity) created the headset root project used by this workflow.
- [danwillm](https://github.com/danwillm/VRCFT-SteamLink) and the [LinkFT contributors](https://github.com/ykeara/LinkFT) documented the Steam Link tracking source used by Qpro's module.
- Fwooffy wrote the original [beginner Quest root guide](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md), and [glorpette](https://github.com/glorpette/quest-guides) made and hosts its GitHub version.

## Tongue tracking, native cheeks and pupil processing

The two bundled choices are **Developer tongue model** and **Mustachio**. Personal imports and refinements remain available. Camera-based cheek prediction and cheek calibration are not included. Native **Individual cheek puff (1/0)** and **Individual cheek suck (1/0)** controls separate the stronger side during a Qpro tracking session; turning them off, pressing Stop tracking or closing the Hub restores the streaming app's original cheek values.

Pupil image filtering can use a verified NVIDIA CUDA or AMD ROCm runtime. Geometry and smoothing still run on CPU, with automatic CPU fallback and backend/performance messages in **Activity**. The camera loop uses the latest frames and avoids encoding unused previews.
