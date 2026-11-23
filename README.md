# Qpro Companion

Qpro Companion brings Meta Quest Pro face and eye tracking to VRChat on Windows through Virtual Desktop or Steam Link. Camera tongue tracking, estimated pupil dilation and experimental controller tools use a rooted headset. The project builds on [Qpro-Enhanced-FT by n0tmast3r](https://github.com/n0tmast3r/Qpro-Enhanced-FT).

For the public build and its setup requirements, see the [latest release](https://github.com/Fwooffy/Qpro-Companion/releases/latest). Download the Companion ZIP asset, extract the entire folder, then open `QproFaceTracking.exe`. This checkout also contains the direct VRChat test build.

Qpro Companion is the new public name. The executable, existing app/data folders and `.qptonguemodel` format retain their QproFaceTracking names for update compatibility.

If you used a Qpro version before V2.0, record and train a new tongue model. Working V2.0 or newer models can be exported and imported through **Model manager**.

**Developer tongue model** is the default and includes the accepted v15 visibility improvement from new clean-shaven recordings while preserving the original direction outputs. **Mustachio** is a separate, opt-in, highly experimental v9 extension trained with one bearded and moustached wearer. Broader wearer and facial-hair validation remain pending. Quick refinement and Focused training extend the model selected under **Facial tracking > Lower-face tracking > Tongue model**, including Mustachio; copies made from an experimental model retain that status. Full dataset trains a new personal model.

An active independent-gaze Magisk module supplies gaze itself. When detected, the Hub keeps **Independent eye gaze + convergence** off and disabled; skip **Check gaze setup** and **Prepare gaze**. Tongue, pupil dilation and eyebrow controls still work separately. After changing the Magisk module, reboot the headset and use **Check USB connection** or **Check headset compatibility** to refresh its status.

## VRChat output in this checkout

Open **First-time setup > VRChat output**, leave **Direct to VRChat** selected and choose **Virtual Desktop** or **Steam Link**. Close VRCFaceTracking if it is running. Enable **Action Menu > OSC > Enabled** in VRChat, then press **Check VRChat connection**. OSCQuery discovers the local game and supported avatar inputs. Test a blink and smile on your avatar. Direct output still needs live headset and avatar testing. [Avatar compatibility and setup](src/RELEASE_INSTRUCTIONS.md#supported-vrchat-avatars).

In Direct VRChat mode, native face and eyes stream automatically while Companion is open. **Headset** and **PC runtime** are for optional features that need them. **Choose tracking features** opens Facial tracking, where **Start tracking** and **Stop tracking** control the selected extras.

For the optional **VRCFaceTracking backup** route, select it under **VRChat output** and use **Install Qpro backup module** if your package includes it. Close VRCFaceTracking before installing and reopen it afterward. Both routes need VRChat OSC enabled and a compatible avatar. [Backup setup instructions](src/RELEASE_INSTRUCTIONS.md#optional-vrcfacetracking-backup).

To have SteamVR open Companion, start SteamVR, open the extracted Companion app and check **First-time setup > Launch with SteamVR**. This is off by default. Clear the same checkbox with SteamVR running to disable it. Optional features still require **Start tracking**. Updates in the same folder keep the setting. If you move the folder, open the new copy and enable it there.

## Updating Companion

The Hub checks for newer stable releases at startup and shows a popup when one is available. Choose **Update** to download and verify it, then **Restart and update** to apply it in the existing folder. Your personal/imported models, recordings, saved settings and connection choices stay in place. Native forwarding resumes when the Hub reopens. Start optional features again when ready.

Automatic checks can be turned off in the update window. **Include prereleases** is off by default. The updater only offers newer versions; another build with the same version needs a manual download.

Use **Updates > Components** for private Python/PyTorch/ROCm runtimes and installed optional components. Stop optional tracking or training first. To remove a Qpro backup module and restore saved source modules, close VRCFaceTracking and its helper, then use **Activity → Restore stock VRCFT**.

If an older Hub updater rejects a direct-output package because the legacy bridge is absent, download the complete release ZIP and extract it into a new folder. Keep the old folder and export personal models before switching; do not copy only the EXE. The branded and legacy ZIP names contain the same package, so changing the filename does not resolve that check.

## Guides

- [Text setup instructions](src/RELEASE_INSTRUCTIONS.md)
- [Technical notes](src/RELEASE_README.md)
- [Quest Pro rooting guide by glorpette and Fwooffy](https://github.com/glorpette/quest-guides/blob/main/root_guide_by_fwooffy.md)

## Repository layout

The runnable release keeps the Hub and guide in its main folder. Development source, tests and build scripts are in [`src/`](src/). Building a package requires the **.NET 10 SDK**, **portable Zig 0.15.2**, and binary assets from an extracted Qpro release. Source archives do not contain the runnable tools or model weights.

Build the native controller stage from the current source first; packaging verifies its source, resource and binary hashes. For a release containing both bundled models, supply separate approved **Developer tongue model** and **Mustachio** folders. Each must contain exactly one model's gate checkpoint, direction checkpoint and sanitized metadata; do not use a folder containing personal captures or several model pairs. `AssetRoot` alone does not select or copy these models.

`VrcftInstallDir` enables a build with VRCFaceTracking backup support from the VRCFaceTracking SDK reference folder. Alternatively, use a checksum-verified backup-capable release as `AssetRoot`. Direct VRChat works in builds without the backup DLL. For packages that need the older updater bridge check, see `-RequireLegacyUpdaterCompatibility`.

Older release assets contain a Python installer instead of the required NuGet archive. The build downloads pinned official Python 3.12.10 when needed and verifies its SHA-256. For an offline build, supply `python-runtime/python.3.12.10.nupkg` in the source folder or asset root. Set `$version` to the chosen three-part development version, and replace the example paths below with paths on your PC:

```powershell
cd src
.\controller-input\build-native.ps1 -Zig 'C:\path\to\zig.exe'
$releaseBuild = @{
    Version = $version
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

The two bundled choices are **Developer tongue model** and **Mustachio**. Personal imports and refinements remain available. Camera-based cheek prediction and cheek calibration are not included. Native **Individual cheek puff (1/0)** and **Individual cheek suck (1/0)** controls separate the stronger side during a Qpro tracking session. Turn the cheek switches off to forward the source's original values. **Stop tracking** ends the extras session and returns to ordinary native forwarding.

Pupil image filtering can use a verified NVIDIA CUDA or AMD ROCm runtime. Geometry and smoothing still run on CPU, with automatic CPU fallback and backend/performance messages in **Activity**. The camera loop uses the latest frames and avoids encoding unused previews.

Closing Companion stops native forwarding and requests a final OSC reset. A crash can interrupt that reset.
