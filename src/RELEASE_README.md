# Qpro Companion V3.0.0 - release candidate

Qpro Companion is the new name for QproFaceTracking. Download **QproCompanion.V3.0.0.zip** when published. For update compatibility, the executable, existing app/data folders and `.qptonguemodel` format keep their old names. The legacy **QproFaceTracking.V3.0.0.zip** asset contains the same bytes for older updaters; use either ZIP, not both.

This is a clean derivative of [Qpro-Enhanced-FT](https://github.com/n0tmast3r/Qpro-Enhanced-FT) v0.1.10-poc. V2.0 added AMD ROCm for tongue-model tracking and training, while NVIDIA CUDA has also been live-tested and is functional. A CPU fallback remains available. It carries headset camera and eye data over USB or wireless ADB on a trusted Wi-Fi network. Independent gaze and relative pupil dilation remain experimental features. A rooted Quest Pro is required.

V3.0.0 includes a selectable Steam Link source alongside Virtual Desktop, native individual cheek puff/suck controls, eyebrow sensitivity, an updated Developer tongue model and the separate experimental Mustachio tongue model. Runtime setup uses a private Python archive and reports setup progress in Activity. AMD ROCm becomes ready only after its GPU inference and training checks pass. This folder is a release candidate; publishing the GitHub release is a separate step.

**Upgrading from a version before V2.0? Record and train a new tongue model.** Keep older models as backups. Working V2.0 or newer models can be exported from the old version and imported into V3.0.0 through **Model manager**, without retraining. See the [beginner PDF guide](Quest_Pro_Enhanced_Face_Tracking_Guide.pdf) for the recording steps.

**Required:** install the [latest VRCFaceTracking from Steam](https://store.steampowered.com/app/3329480/VRCFaceTracking/) and let Steam finish updating it before installing Qpro's module.

**Updating the optional controller add-on:** Close SteamVR, open **First-time setup > Optional hands and controllers**, expand **Show hands setup and uninstall**, then press **Install hand/controller components** in the new Companion version, then reopen SteamVR through Virtual Desktop. This updates the separately installed add-on and keeps valid saved tuning. Swipe offers a 25% to 200% sensitivity slider, with 100% as the default; stop tracking before adjusting it, then start again.

## What's in this package

- The Windows Hub, bundled Android Platform-Tools, private Python runtime archive, Virtual Desktop and Steam Link Qpro modules, and **Developer tongue model**.
- The opt-in **Mustachio** tongue model. **Developer tongue model** and **Mustachio** are the two bundled model choices; personal imports and refinements remain available.
- Local changes for AMD Radeon GPU inference and training, including the Windows ROCm training-exit fix.
- The developer eye-mapping demo profile, with local file paths removed.

The package contains **no personal camera captures, image arrays, training cache, generated headset eye patch, or machine-specific Python environment**. It starts with empty `QproRuntime/captures/` and `QproRuntime/training/` folders. Approved model weights are included; their source camera recordings stay private.

**Developer tongue model** is the default on a fresh setup. Updates keep personal/imported selections; retired bundled choices switch to the new developer model. Its new visibility detector was trained with clean-shaven recordings and checked on a separate recording after reseating the same wearer's headset. Native fusion reduced missed visible-tongue stills from 75 to 1 across 155 stills, with no hidden-tongue false detections in that recording. One small tongue tip during a smile was still missed. Some older bearded poses worsened; broader wearer and facial-hair checks remain pending. Tongue direction outputs retain the original developer weights.

**Mustachio is highly experimental.** It extends the earlier developer tongue model using a diagonal/facial-hair recording from one bearded and moustached wearer. Broader facial-hair and independent-wearer validation are still pending. Contrast preprocessing can improve visible low-contrast detail; it cannot recover a tongue covered by hair or clipped camera detail. Choose Mustachio explicitly under **Facial tracking > Lower-face tracking > Tongue model** to try it. Quick refinement and Focused training extend the selected model and keep Mustachio copies' experimental status through training, renaming, export, and import. **Full dataset** trains a new personal model instead.

Start with the [beginner PDF guide](Quest_Pro_Enhanced_Face_Tracking_Guide.pdf), or use the [text setup instructions](RELEASE_INSTRUCTIONS.md) in the source repository. The Hub opens on **First-time setup** the first time you launch this extracted copy and on **Facial tracking** afterward. Setup remains available in the sidebar. Keep `QproFaceTracking.exe`, `Helpers`, and `QproRuntime` together. Supporting scripts, models, and bundled Android tools live in `QproRuntime`; optional command launchers live in `Helpers` in the release ZIP (see the [helper notes](RELEASE_HELPERS_README.md)); licenses and upstream documentation live in `Docs`.

## Setup inside the Hub

Choose **USB cable** or **Wireless ADB (Wi-Fi)** in First-time setup. For Wi-Fi, enter the Quest's address and press **Connect to Quest**; use **Pair and connect** if the headset shows a pairing code, or **Enable from USB** for a one-time cable setup. The Hub saves the selected transport and uses it for tracking. Switching to USB makes the Hub target the cable connection.

Press **Install runtime**. Choose **Virtual Desktop** or **Steam Link** under **Streaming app**, then press the matching **Install Virtual Desktop module** or **Install Steam Link module** button. Only the selected source's install button is available, and installing it removes the other Qpro source module. Close VRCFaceTracking and wait for its module process to exit before pressing either button, then reopen it. For Steam Link, enable OSC, eye sharing, and face sharing in its headset **Advanced Settings**, using **OSC Output Port 9015**. Remove separate modules that would compete for the same source.

**Already using an enabled Independent Eye Gaze Magisk module?** Skip **Check gaze setup** and **Prepare gaze**. Once detected, the Hub keeps **Independent eye gaze + convergence (provided by Magisk)** off and disabled, along with both setup buttons. Tongue, pupil dilation and eyebrow controls work separately. After changing the module, reboot the headset and use **Reconnect USB** or **Check headset compatibility** to refresh detection.

If you choose the Hub's temporary independent gaze method, disable any gaze Magisk module and reboot first, then use **Check gaze setup** followed by **Prepare gaze** on the **Independent gaze** card. The result popup identifies the detected gaze method, gives the next step and explains **Recover Qpro gaze** and **Reset legacy gaze**. The Hub's gaze option starts off by default and can fail on some Quest Pro firmware builds. The [beginner PDF guide](Quest_Pro_Enhanced_Face_Tracking_Guide.pdf) explains the alternative Magisk method.

**Use one independent gaze method at a time.** While the Magisk module is active, leave **Independent Eye Gaze** unchecked in the Hub. If you want to try the Hub's temporary gaze method instead, enable it and start tracking; the headset will freeze while it hooks into the tracking engine. **Stop tracking** only reverses the Hub's changes; it does not disable a Magisk module.

Disable the gaze Magisk module and reboot before switching to the Hub's gaze method or returning to ordinary headset eye tracking. Qpro does not disable Magisk modules for you.

If the Hub recognizes your discrete AMD GPU, press **Install ROCm 10.1** after the PC runtime is ready. The installer validates GPU inference and training before marking the new environment ready.

The **VRCFT module** card also has **Uninstall Qpro module**. Stop Qpro tracking, close VRCFaceTracking and wait for its module process to exit before pressing it. The uninstall script removes Qpro's source module and restores previous Virtual Desktop modules backed up by this extracted copy when their original locations are unoccupied. It leaves personal captures, models and headset settings alone. Restart VRCFaceTracking with exactly one official module for the selected streaming app; install the official Virtual Desktop or Steam Link module if needed.

On **Personalize**, each capture mode has **Recorded datasets (including trained)** and **Delete selected dataset…**. Deletion requires confirmation and removes only that recording, its labels and session details, and its prepared training cache from the extracted release folder. Existing trained models stay available.

## Install on AMD Radeon

1. Open `QproFaceTracking.exe` and use **First-time setup > Install runtime**.
2. If the Hub shows an eligible AMD GPU, press **Install ROCm 10.1** on that same page. If a verified ROCm 7.2.1 installation is available, the button says **Upgrade to ROCm 10.1**. Mapped RX 6000, 7000, and 9000 cards use [TheRock's architecture-specific ROCm 10.1 wheels](https://github.com/ROCm/TheRock/blob/main/RELEASES.md). New installations use a short per-user folder under `%LOCALAPPDATA%\QproFaceTracking\r` (for example, `10-gfx1100` for an RX 7900 XT), so a deeply nested Qpro download folder does not cause Windows library-path failures. Verified older `.venv-rocm-experimental` folders remain usable. AMD's packages are stable while Qpro's integration is experimental. Allow several gigabytes of free space and an internet connection. [AMD's ROCm 10.1 matrix](https://rocm.docs.amd.com/en/latest/compatibility/compatibility-matrix.html) validates Windows 11 25H2 with Adrenalin 26.10.41.05; other Windows 11 builds or drivers may fail the GPU checks.
3. After the Hub reports that the GPU training and inference checks passed, choose tongue tracking. The training and live tracking scripts automatically select ROCm. Activity should name the AMD GPU when the model loads.

The separate `Helpers/Install-AMD-ROCm.cmd` and `Helpers/Launch-QproRocm.cmd` files are troubleshooting helpers. An existing verified `.venv-rocm` with PyTorch 2.9.1 + ROCm 7.2.1 remains a compatibility fallback on the eight cards in [AMD's Windows 7.2.1 list](https://rocm.docs.amd.com/projects/radeon-ryzen/en/docs-7.2.1/docs/compatibility/compatibilityrad/windows/windows_compatibility.html); that path was live-tested on an RX 7900 XTX. The new ROCm 10.1 recipe has not yet been installed or live-tested in this review and becomes active only after training and inference checks pass. The stable AMD package index provides gfx1031 and gfx1032 device wheels, but package availability does not establish driver compatibility on every RX 6000 card. If checks fail, Qpro can use a previous verified 10.0 environment or the 7.2.1 fallback where available, or the shared CPU or NVIDIA runtime.

ROCm setup verifies exact host, device, and SDK packages before testing GPUs. Missing `torchgen` or incomplete host-wheel metadata triggers one targeted repair in the separate Qpro environment; an import failure is reported separately from an unsupported GPU. Inherited GPU visibility masks are ignored only in Qpro's ROCm process, and the selected discrete device must match the installed gfx target.

## Install on NVIDIA or CPU

Use **First-time setup > Install runtime** in `QproFaceTracking.exe` and skip AMD ROCm. The setup detects NVIDIA hardware and installs its CUDA 12.8 PyTorch build. The original CPU path remains available when no supported GPU runtime is detected, though training is slower. NVIDIA CUDA hardware has been live-tested and is functional in this edition. See [PyTorch's Windows installation guide](https://pytorch.org/get-started/locally/) for CUDA requirements.

### Python on the PC

You do **not** need to install or remove Python yourself. The release includes a 64-bit Python 3.12 archive that Qpro extracts into `%LOCALAPPDATA%\QproFaceTracking\runtime\python-3.12.10`. It does not run the Windows Python installer, enter **Modify** mode, change PATH, or alter another Python installation. Qpro creates `%LOCALAPPDATA%\QproFaceTracking\runtime\.venv` from that private base and installs its tracking packages there. New AMD ROCm environments use the same private base and the compact `%LOCALAPPDATA%\QproFaceTracking\r` storage. Existing release-local ROCm folders stay separate and are not deleted.

An existing working Qpro environment is reused. If that environment becomes unusable because its former base Python was removed or damaged, **Install runtime** rebuilds it from the bundled private Python archive. Keep the full extracted Qpro release folder available when repairing the runtime.

If **Install runtime** exits with code 1, read the first error in **Activity** rather than treating the code as a diagnosis. A missing `VCRUNTIME` or native DLL can require [Microsoft's latest x64 Visual C++ Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170#latest-supported-redistributable-version). Repairing that prerequisite and rerunning **Install runtime** is safer than deleting the entire `%LOCALAPPDATA%\QproFaceTracking` folder. Other failures need their own Activity details.

## Wireless tracking without USB after rooting

1. Keep the rooted Quest and PC on the same trusted Wi-Fi network. On the Hub's **First-time setup** page, select **Wireless ADB (Wi-Fi)**.
2. If the Quest already exposes wireless ADB, enter its Wi-Fi IP and port (usually `5555`) and press **Connect to Quest**. The Hub verifies ADB and Magisk root before saving the address. A **Quest connected** pop-up and “Wireless Quest ready” in Activity mean you can start tracking without pairing. A **Quest connection failed** pop-up points you to Activity for the exact error. Use **Pair and connect** only if the headset requires Android's six-digit pairing flow. Enter the temporary **pairing IP:port** shown beside that code, as well as the regular **connection IP:port**. The two ports are different. A “protocol fault” during pairing often means the temporary pairing dialog closed or the regular connection port was entered instead; reopen the dialog for a fresh address and code.
3. If wireless ADB is off, connect an authorized Quest by USB once and press **Enable from USB**. The Hub checks Magisk Shell root, enables ADB over Wi-Fi, and saves the Quest address. Unplug USB afterward; the Hub uses the selected Wi-Fi connection.
4. Start the selected Virtual Desktop or Steam Link app on the Quest and connect to the PC. Start SteamVR and VRCFaceTracking, then press **Start tracking** in the Hub. Wi-Fi quality and router settings affect camera latency and stability.

On the tested Quest Pro, Singularity kept ADB TCP port `5555` enabled after reboot. If your port is closed after reboot, open **Singularity > Root Terminal** on the headset and run `su -c 'setprop service.adb.tcp.port 5555; stop adbd; start adbd'`, approving root in Magisk if prompted. This manual command affects the current boot only. Then connect to the current Quest IP again in the Hub. Some headsets instead expose Android's pairing-code flow. Use a trusted private network: the ADB TCP port remains reachable on the local network until disabled or the headset reboots.

To return to cable tracking, choose **USB cable** on the setup page. **Disable Wi-Fi ADB** can switch the connected Quest back to USB mode for the current boot. The helper `.cmd` files remain available for troubleshooting. Wireless camera and eye relay is separate from Virtual Desktop's PCVR video stream; both can use Wi-Fi. If connection fails, check for router client isolation and verify the Quest's current IP and port.

## Camera preview windows

On **Facial tracking**, open **Your session > Show connection and camera settings**. **Preview tracking cameras** is off by default. Turn it on before starting a session to show the camera and tongue model windows. Tongue and pupil output work with the preview off; the cameras are still used for tracking. An explicitly saved preference is preserved for later launches and takes effect on the next tracking start. Guided tongue capture still opens its prompt window because the capture workflow needs it.

## Face controls on Facial tracking

**Controllers** is a separate sidebar page directly below **Facial tracking**. It contains the existing optional hands/controller checks, switches and modes. **Start tracking** and **Stop tracking** remain in the shared bottom bar and control all selected features.

The **Lower-face tracking** section groups **Tongue model**, **Experimental tongue tracking** and native cheek controls. Choose Developer tongue model, Mustachio or your own imported/refined model. Stop and restart tracking after changing a camera feature or model.

**Individual cheek puff (1/0)** and **Individual cheek suck (1/0)** use the streaming app's native signals with **1/0** separation: a clear leading side reaches full strength while the other stays at zero; deliberate two-sided expressions still move both. Turn a cheek switch off for the source's original values. Cheeks do not use the tongue camera model or require cheek recording. Adjustments apply only while Qpro tracking runs; **Stop tracking** or closing the Hub restores native cheeks.

Under **Eyes**, **Adjust eyebrow movement** starts off. **Eyebrow sensitivity** scales the source's existing separate brow movements from 0.50x to 3.00x. The avatar must support those expressions. Smirk handling reduces the weaker smile side when one corner clearly leads while preserving a deliberate two-sided smile; it is part of the module, with no separate Hub toggle.

**Motion smoothing** controls tongue animation transitions, and the visibility hold reduces brief dropouts. These filters cannot compensate for fully obscured tongue poses. Record **Focused diagonals + facial hair** on **Personalize** if those poses need extra examples.

## Experimental relative pupil dilation

**Activity > Tracking health** shows the active PC tongue inference backend after its model loads: **CPU**, **NVIDIA CUDA**, or **AMD ROCm**. It reads the loaded PyTorch runtime, so a GPU being installed does not by itself make the indicator say GPU. Pupil processing reports its separate backend; verified GPU filtering still includes CPU quality checks and smoothing. Independent gaze is processed on the Quest headset.

Closing the Hub first waits for tracking cleanup, then sends `adb kill-server` using its configured ADB executable and endpoint. The ADB server is shared with other Android tools, which will disconnect and may restart it. Setup, capture, training and Qpro eye-model recovery must finish before the Hub closes.

**Stop tracking** restores native cheek puff and suck values and requests a clean shutdown of camera output and independent gaze. Closing the Companion or a Companion crash also ends cheek adjustments; cheek switch choices stay saved. The camera process checks for Stop even when the headset stream pauses. The gaze process may take several more seconds to restore the headset eye-model state recorded before the Qpro session. Wait for the Activity log to confirm the recovery result before starting another session. If cleanup is still running after the Hub's wait period, the stop request remains active and the button can be pressed again. Stopping does not uninstall the Qpro VRCFaceTracking module: its smirk and saved eyebrow adjustments can still run with the Hub closed. It also does not disable an independent-gaze Magisk module. Trained tongue models are PC files; they stop providing live output when their tracking process ends.

Use **Recover Qpro gaze** in **First-time setup** for a verified interrupted Qpro gaze session. **Check gaze setup** reports the headset selection and other active gaze methods without changing them. If an older build left experimental gaze enabled without a recovery record, **Reset legacy gaze** offers an explicit, confirmed choice of the normal nonexperimental selection. It cannot reconstruct an unknown previous state. The reset refuses active independent-gaze Magisk modules and unverified overlays on the eye model or tracking engine; it does not uninstall modules or overwrite firmware. Disable any active gaze Magisk module and reboot before trying to return to ordinary headset gaze.

The Hub has an optional **Experimental relative pupil dilation (eye cameras)** checkbox, off by default. Enable it alongside gaze and/or tongue tracking, or on its own, then press **Start tracking**. It requires the rooted headset's eye cameras, enabled eye tracking, the PC runtime, and the **Qpro VRCFaceTracking module** installed from the Hub. When tongue tracking is also selected, both features share one camera stream. A wireless camera stream can use more bandwidth when all cameras are selected.

**Pupil response** controls how strongly relative changes move the avatar value away from neutral. It runs from 1.0× to 3.0× in 0.2× steps, with 1.4× as the default. The pupil filter now responds faster to real changes while still rejecting single-frame diameter errors. A gradual curve keeps larger response settings from hitting the animation limit as abruptly. The Qpro VRCFaceTracking module now maps the output's 2–8 relative range to the full normalized 0–1 dilation animation range, with neutral at 0.5. Start with 1.4× and increase it gradually; use a lower setting if the avatar looks jumpy. The setting changes the animation value, not the camera detector or a measured pupil diameter. Your avatar must have a pupil dilation animation mapped in VRCFaceTracking for the change to be visible.

Independent gaze briefly restarts Meta trackingservice when its temporary eye model is applied and again when the recorded previous state is restored. The headset may look frozen and lose positional tracking for a moment during those two transitions. It has not crashed; wait for the service to return and check Activity before starting another session. Pupil-only tracking does not request that service restart.

If the independent-gaze process fails during startup, stops unexpectedly, or receives no paired eye samples for 20 seconds, the Hub unchecks **Independent Eye Gaze** and saves that choice for later launches. It checks for a recorded Qpro session and restores its verified previous state if needed. Other selected tracking can continue. Read **Activity** to confirm recovery; if the headset is disconnected, reconnect it before retrying gaze. Select the option manually when you want to retry.

For each eye, 12 consecutive, steady valid frames establish a session baseline shown as approximately 5 mm in VRCFaceTracking. Look straight ahead and keep your gaze steady while the pupil status warms up. Later values describe *relative change for avatar animation*; they are **not measured millimetres, medical data, or a calibrated absolute pupil size**. The camera preview outlines accepted pupil candidates and the Activity log prints `PUPIL_STATUS` with the current estimates and each eye's state. If an eye is occluded or moves far from its calibrated gaze position, its value returns to the module's neutral 5 mm; stopping the stream also restores neutral values. Changes in headset fit, lighting, reflections, and eyelid position may affect detection. The detector has passed synthetic tests and replay of two short live Quest Pro eye-camera captures, including blinks and gaze motion. Relative pupil animation has also been live tested in VRChat on a Quest Pro; the visible result depends on the avatar's pupil mapping.

The AMD ROCm 10.1 setup downloads packages from `stable.repo.amd.com`; the original runtime setup downloads Python dependencies and PyTorch. No personal headset data is included in this archive. New captures and models are created only after you use the app. For help, use the [community Discord](https://discord.gg/ghvuJTpRu4); this server is not owned by Fwooffy.

## Changes from the upstream package

The Hub executable is built from this project's source. Changes cover source-specific VRCFaceTracking modules, tongue capture and training, gaze recovery, relative pupil processing, isolated runtime setup, GPU selection, and wireless connection helpers. `SHA256SUMS.txt` lists the exact contents of the V3.0.0 ZIP.

This edition is not an official release of the upstream repository. See [LICENSE](LICENSE) and [third-party notices](THIRD_PARTY_NOTICES.md).

## Tongue training and refinement

Quick refinement has **16 tongue cards** and Focused training has **22**; they extend the selected **Tongue model**. Full dataset has **58 tongue cards** and trains a new personal model. The cards teach tongue visibility, extension and direction; cheeks use the streaming app's native values independently.

Existing personal models, exports and backups remain usable for their tongue outputs. Keep a backup through **Model manager** before training a new copy. Imported weights cannot reconstruct the original camera recordings.

Pupil image filtering can run on verified NVIDIA CUDA or AMD ROCm devices. Quality checks, contour geometry and smoothing remain on CPU. The worker processes the latest eye pair, falls back to CPU when needed, and reports timing and backend in Activity. Unused MJPEG streams are not encoded.
