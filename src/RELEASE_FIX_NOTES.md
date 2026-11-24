# Qpro Companion V3.1.0

## Before you install

Download **QproCompanion.V3.1.0.zip**, extract the entire folder, then open `QproFaceTracking.exe`. The ZIP includes the updated PDF guide. GitHub's source archives contain development files.

Native face and eye input comes from **Virtual Desktop** or **Steam Link**. A rooted Quest Pro is required for camera tracking and headset tools. Enable OSC in VRChat and use an avatar with compatible face parameters. VRCFaceTracking is available as an optional backup output route.

## New since V3.0.3

- **Direct VRChat output.** Companion uses OSCQuery to discover VRChat and the current avatar's supported inputs. Native face and eye forwarding starts while Companion is open and continues after optional camera tracking stops.
- **A choice of output routes.** Use the **Direct to VRChat / VRCFaceTracking backup** switch in **First-time setup > VRChat output**. Switching releases the previous sender before the other route takes over. Close VRCFaceTracking before choosing direct output.
- **Optional SteamVR startup.** Enable **Launch with SteamVR** in First-time setup while SteamVR is running. Camera and controller extras still start with **Start tracking**.
- **More compact controls.** Connection checks sit beside session status. Model actions sit beside the selected model. Activity groups Copy and Save together and places Pause and Jump beside the log.

## Tracking and reliability fixes

- **USB camera startup.** The relay keeps its ADB and Magisk root session alive and waits for a verified listener. It reuses a correctly writable shared buffer without repeating a permission change that some rooted headsets refuse.
- **Wireless camera recovery.** A live camera session can recover after a stream disconnect or 20 seconds without data. Recovery checks the headset identity, waits up to 12 seconds for ADB and permits two restart attempts. A recording or calibration session ends on disconnect to preserve its data boundaries.
- **Pupil processing.** Reused image filtering, earlier rejection of unsuitable regions and a combined stereo GPU readback reduce repeated processing. Scheduling keeps only current pending frames and respects each feature's processing budget.
- **Tongue and eye validity.** Tongue smoothing history resets after output expiry, preventing an old pose from briefly returning. Independent eye filters reset separately when an eye becomes invalid. The backup module clears brow values it owns when Virtual Desktop loses upper-face data.
- **Cleaner stopping.** Tongue and pupil overrides clear before Companion waits for camera workers. ROCm readback cleanup addresses a worker that could stay running after Stop on older AMD runtimes.
- **GeForce GTX 10-series setup.** Runtime setup selects a CUDA 12.6 PyTorch build for these cards and checks a GPU convolution before reporting CUDA ready.

## Setup and data handling

- **Automatic source-module replacement.** Installing the backup module saves and replaces recognized Virtual Desktop, LinkFT and older Steam Link modules. Installation checks backups before removing files and attempts restoration if a switch fails. Close VRCFaceTracking and its module helper first.
- **Module recovery.** **Activity > Restore stock VRCFT** removes Qpro modules and restores verified saved source modules when their original locations are free. Activity reports recovery conflicts and the retained backup location.
- **Safer model and capture actions.** File changes are blocked during active tracking, setup or training. A failed export preserves the previous model backup, and capture or label write failures are reported.
- **Less repeated work.** Hub construction defers table layout, dataset lists read each session once, and active tracking skips routine runtime probes after verification. Activity batches incoming lines and includes pupil worker timings in diagnostics.
- **Optional stop alert.** **Sound once if a tracking feature stops unexpectedly** is available under **Facial tracking > Show connection and camera settings**. It starts off.

## Upgrading from V3.0.3

You can update directly to **V3.1.0** through the updater. Stop optional tracking and training, then choose **Update** and **Restart and update**. Personal models, recordings, saved options and connections retain their locations. Verified retired bundled models can be replaced by the current developer model.

For **Direct to VRChat**, close VRCFaceTracking and enable OSC in VRChat. For **VRCFaceTracking backup**, close VRCFaceTracking and its helper, then use **Install Qpro backup module** in First-time setup to install the matching V3.1.0 module. Reopen VRCFaceTracking afterward. Runtime and optional component updates are available separately under **Updates > Components**.

## Checks for this release

A tester confirmed that the camera startup fix works on their Quest. The original V3.0.3 updater selected, staged and applied the V3.1.0 ZIP in a disposable installation, preserving sample personal models, recordings, options and connections. The upgraded app passed its packaged self-test.

Offline checks also cover shared-buffer failures, output ownership, native source mapping, avatar parameter handling and synthetic pupil result agreement. Live avatar behaviour, SteamVR automatic launch, sustained wireless recovery and performance on other PCs still need session-specific checks.

Report problems with your Companion version, headset build, GPU and **Activity > Copy diagnostics** on the [issue tracker](https://github.com/Fwooffy/Qpro-Companion/issues).
