# QproFaceTracking

## Download

For normal use, download the complete `QproFaceTracking-<version>-poc.zip` package
from this repository's **Releases** page and extract the whole folder. GitHub's
automatically generated “Source code” archives do not contain the large executable,
pretrained model, private Python installer, or bundled Android tools required to
run the application.

QproFaceTracking is an experimental, USB-connected Quest Pro proof of concept for
VRCFaceTracking. It keeps Virtual Desktop's normal face, brow, jaw, and blink data,
then optionally replaces only:

- left/right gaze with independently preserved detector rays, allowing visible eye
  divergence and convergence; and
- detailed tongue channels with a personalized stereo model using the Quest Pro's
  two lower-face cameras.

This is enthusiast research software, not a polished consumer driver. It requires a
rooted Quest Pro and currently supports USB only.

## Requirements

- Windows 10 or 11 x64
- Rooted Quest Pro with face and eye tracking enabled
- Magisk Superuser access granted to **Shell / ADB Shell**
- Meta developer mode and an authorized USB debugging connection
- No separate ADB installation; the release includes the required official Android
  Platform-Tools files
- SteamVR, Virtual Desktop, and VRCFaceTracking
- A current NVIDIA display driver is strongly recommended for fast tongue-model
  training. NVIDIA hardware is optional; CPU training is supported but is much
  slower, especially for the full dataset.

## First run

1. Extract the entire release folder. Do not run the executable from inside the zip.
2. Double-click `QproFaceTracking.exe`.
3. Select **Set up PC runtime**. No preinstalled Python or PATH modification is
   required. The release carries the official signed Python 3.12.10 installer and
   silently installs a private per-user copy plus OpenCV, NumPy, and PyTorch under
   `%LOCALAPPDATA%\QproFaceTracking\runtime`. It creates no launcher, shortcuts,
   file associations, or PATH entries. PyTorch is a large download, but later
   release folders reuse the same runtime. Setup uses PyTorch's official CUDA 12.8
   wheel when an NVIDIA driver/GPU is detected and the official CPU wheel
   otherwise. If you upgraded from v0.1.5 and training reports CPU fallback despite
   having NVIDIA hardware, run this step again to repair the earlier CPU-only
   runtime.

The PC-runtime status becomes green only after OpenCV, NumPy, and PyTorch all pass
their final import check. Merely finding a partially created `python.exe` is not
considered complete. An interrupted or failed setup can be retried with the same
button; it reuses the private Python installation and repairs the environment.
4. Close VRCFaceTracking, then select **Install/update bridge**. Restart VRCFT.
5. For independent gaze, connect the rooted headset and select **Prepare gaze from
   headset**. The tool reads the stock eye archive from *your headset*, creates the
   byte-length-preserving local patch, and deletes the temporary stock copy.
6. Start Virtual Desktop, SteamVR, and VRCFT. Confirm ordinary tracking works.
7. Choose gaze and/or tongue tracking, select profiles and settings, then press
   **Apply and start selected**.
8. Press **Stop and restore stock** before disconnecting USB or closing the app.

Opening the hub does not modify tracking. The hub checks every prerequisite before
Apply. If a tracking window is still closing, press `Q` in that window and wait for
the activity panel to confirm restoration.

The setup page marks completed steps in green and pulses the first unfinished
action. Successful setup and model training show a confirmation and play a short
sound; a warning sound accompanies an Apply attempt whose prerequisites are not
ready. A setup progress strip appears only while a first-time action is active and
animates during long operations such as the initial private-runtime installation.
The setup and personalization pages scroll independently at high Windows
DPI scaling, and the setup cards use DPI-aware sizing to keep their descriptions
and buttons visible.

Before gaze preparation starts, the hub checks the headset connection and root
access separately. A missing, offline, or unauthorized ADB device produces direct
USB/debugging guidance; a visible headset that rejects `su` produces separate
root/Magisk guidance instead of a generic script error.

Tongue training automatically selects CUDA when PyTorch can access it and falls
back to CPU instead of failing on systems without NVIDIA graphics. The
personalization page shows frame preparation, the active device, checkpoint stage,
epoch count, and overall completion while training is active. The progress panel is
collapsed while idle, and the personalization page has its own scrollbar when the
live status needs more room. CPU mode uses a smaller batch to remain usable
on ordinary PCs, but it can take substantially longer; full-dataset CPU training
may take hours. Installing the CUDA-enabled PyTorch wheel does not replace the
Windows NVIDIA display driver—the driver must already be installed and working.

If gaze startup was interrupted, the next launch automatically removes the stale
headset trace reader before applying the independent-eye branch. You should not
need to reboot the headset or manually clean tracefs.

## Included profiles

- `Developer visual-axis mapping v2` is a demonstrator calibrated to the original
  developer. Eye anatomy and headset fit differ, so its absolute alignment may be
  imperfect for another wearer.
- `Developer-trained tongue model v8 (demo)` is trained on one person. It is useful
  as an immediate bootstrap/demo, not a universal model. Quick refinement is
  recommended for another wearer; false positives and blind spots remain possible
  with different mouths, facial hair, clothing, and headset fit.

The hub discovers additional paired tongue files named
`qpro-stereo-tongue-vN-gate.pt` and `qpro-stereo-tongue-vN-direction.pt`. Full or
quick-personalization training chooses the next unused version and never overwrites
the bundled v8 pair.

After a guided capture closes, the hub asks for a friendly dataset name. The raw
timestamped capture remains unchanged for reliability, while the friendly name is
stored in its session metadata and carried into the trained model. Each
personalization card lists completed, untrained datasets; select the one you want
before pressing Train. Successfully trained datasets leave that queue.
When running from a versioned `dist` folder, the hub also discovers captures in
adjacent QproFaceTracking release folders, so an upgrade does not hide recordings
made with the previous build.
On first launch it also copies complete personal tongue model pairs from an
adjacent older release into the new release folder. The public package itself still
contains only the developer v8 demonstration model.

Capture currently requires Virtual Desktop tracking, SteamVR, and VRCFaceTracking
to be running because the trainer records Quest Pro's native `TongueOut` confidence
as an auxiliary visibility label. The hub checks these common prerequisites before
opening the guided camera window and explains what is missing directly.

The **Tongue model manager** tab can rename, export, import, and delete personal
paired models. Export produces one `.qptonguemodel` package containing both neural
network checkpoints. Import assigns the next unused local version. Only import
models from people you trust; PyTorch model files are executable data when loaded.
The bundled developer v8 demo is protected from accidental deletion.

## Tongue controls

- **Motion smoothing:** left/`0` is most responsive; right/`100` is smoothest but
  adds latency. Camera inference is frame-based, so this control filters steps but
  does not create extra tracking samples between camera frames.
- **Weighted camera + native:** recommended default; combines the stereo model with
  Quest Pro's native `TongueOut` confidence.
- **Camera only:** ignores native tongue confidence.
- **Native only:** diagnostic visibility gate; direction still comes from cameras.
- **Conservative agreement:** requires both visibility sources and reduces false
  positives, at the cost of more false negatives.
- **Camera FPS cap:** 24 is the conservative default. Higher choices, up to 72,
  request a faster headset source cadence. On the tested Quest Pro, a 72 cap was
  stable but produced about 36 paired stereo samples per second; it does not imply
  72 completed tongue inferences per second.

## Source and development

The main repository contains the editable WinForms/.NET hub, Python tracking and
training code, and C headset relay/injector sources. Large generated executables,
the developer-trained model, Android tools, and the private Python installer are
kept out of Git history and distributed in the versioned Release package instead.
That keeps clones reviewable while ordinary users still receive a complete build.

A source-only clone can build and edit the managed/Python/C components, but the
full `build-release.ps1` packaging step also requires maintainer-staged assets that
are intentionally not kept in Git: the developer model checkpoints, official
Python installer, Android Platform Tools, and prebuilt rooted-headset binaries.
GitHub does not automatically produce a runnable package from the repository;
official runnable builds are attached explicitly on the Releases page.

Developers rebuilding native components can run:

```powershell
.\build-and-run.ps1 -RebuildNative
```

Developers rebuilding managed helpers can use `-RebuildManaged` or build the
individual `.csproj` files. See `CONTRIBUTING.md` and `THIRD_PARTY_NOTICES.md` before
redistributing changes.

## Safety and privacy

Inward camera frames are highly sensitive. Live frames remain local and recordings
are created only during explicit calibration. Never publish `captures/`,
`training/`, a generated eye archive, or another person's model without informed
consent. A headset reboot removes injected native code. The launcher also uses a
capture lease and attempts scoped cleanup on every normal exit.

Firmware updates can change provider symbols, trace offsets, or model contracts.
Treat every update as unsupported until revalidated. This project is unaffiliated
with Meta, Virtual Desktop, VRCFaceTracking, VRChat, Project Babble, or EyeTrackVR.
