# Qpro Companion V3.0.4

## Before you install

Extract the entire ZIP, then open `QproFaceTracking.exe`. For native tracking, share face and eye data in Virtual Desktop or Steam Link, close VRCFaceTracking and enable OSC in VRChat. Camera features and headset tools require a rooted Quest Pro. Install VRCFaceTracking if you choose the optional backup route.

## What's changed

- **Direct to VRChat.** In **First-time setup → VRChat output**, choose your streaming app and select **Check VRChat connection**. Companion forwards the streaming app’s native face and eye data while camera tracking is stopped. Verify the result on your avatar.
- **VRCFaceTracking backup.** In **First-time setup → VRChat output**, select **VRCFaceTracking backup** on the **Output route** switch. Companion releases direct output before the backup takes over. To switch back, close VRCFaceTracking and select **Direct to VRChat**.
- **Launch with SteamVR.** With SteamVR running, enable the setting in **First-time setup** to open this copy of Companion on future starts. Camera and controller features still start with **Start tracking**.

- **Faster Hub startup.** First-time setup opens sooner because the Hub arranges its tables after their controls are in place. Saved choices are ready on the first visit to each page.
- **Tongue tracking after a stall.** When frames resume after the tongue output expires, Companion uses the current pose. A retracted tongue can no longer briefly reappear from the old smoothing history.
- **Model exports and captures.** A failed export preserves the previous model backup. Capture and label write failures are reported so you can identify incomplete recordings before training.
- **Camera diagnostics.** The first FPS reading starts with the first received frame. Quitting a stalled USB read now responds to the console stop request.
- **GeForce GTX 10-series support.** PC runtime setup selects the CUDA 12.6 build of PyTorch 2.14.1 for these cards and tests a GPU convolution before reporting CUDA ready. Use **First-time setup → Install runtime** to repair an older installation.
- **Wireless camera recovery.** Companion can recover live camera tracking if the Wi-Fi stream disconnects or sends no bytes for 20 seconds. It waits up to 12 seconds for ADB to return, checks the headset identity and can restart the stream twice per session. Activity shows each recovery attempt. Recordings and calibration sessions end on a disconnect so their files remain separate.
- **Tracking alerts.** Enable **Sound once if a tracking feature stops unexpectedly** under **Facial tracking → Show connection and camera settings** for an audible alert. The default is silent.
- **Relative pupil dilation.** The detector reuses each eye’s filtered image across brightness thresholds. On the GPU, both eyes return to the CPU in one transfer. Small timing variations at 24 FPS no longer routinely discard the next pupil update.
- **Stopping tracking.** Companion clears the tongue and pupil outputs before waiting for the preview or tongue worker to close. Activity reports a failed capture save if a recording cannot finish.
- **Older AMD runtimes.** Fixed a Windows ROCm 7.2.1 issue that could leave the Python tracking process running after Stop.
- **Activity.** Incoming lines are inserted together to keep the log responsive during bursts.
- **CPU usage and diagnostics.** Companion reads each saved capture session once when listing datasets and skips routine Python checks during live tracking when the runtime is already verified. **Activity → Copy diagnostics** now includes pupil worker CPU time and elapsed processing time.

- **Automatic face-module switching:** Installing a Qpro module now backs up and removes recognized Virtual Desktop, LinkFT and older Steam Link source modules. Close VRCFaceTracking and its module helper first; no manual uninstall is needed for recognized modules.
- **Verified backups and rollback:** Original module files are checked before removal. A failed installation attempts to restore the previous modules and source selection, with recovery details in Activity.
- **Restore the previous source.** Close VRCFaceTracking and its helper, then choose **Activity → Restore stock VRCFT**. Companion restores verified saved modules when their original locations are free. Backups are kept in Qpro's local app data across extracted Companion folders.
- **Backup module setup.** **Install Qpro backup module** saves recognized source modules before replacing them. Activity identifies files that need manual review.
- **Updated guide:** The PDF and text instructions cover automatic switching and recovery.

If the module and recovery folders are redirected to different drives, Companion stops before switching modules and reports the folder issue.

## Upgrading

Stop tracking before updating. Close VRCFaceTracking before installing, switching or removing its Qpro module, then reopen it. Personal models, recordings and unrelated modules are kept.

For problems, include your Companion version and **Activity → Copy diagnostics** on the [issue tracker](https://github.com/Fwooffy/Qpro-Companion/issues).
