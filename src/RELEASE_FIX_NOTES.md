# Qpro Companion V3.0.4

## Before you install

A rooted Quest Pro and the latest [VRCFaceTracking on Steam](https://store.steampowered.com/app/3329480/VRCFaceTracking/) are required. Extract the entire ready-to-run ZIP before opening Qpro Companion.

## What's changed

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
- **Restore the previous source:** Backups are stored in Qpro's local app data and remain available across extracted Companion folders. **Uninstall Qpro module** and **Activity > Restore stock VRCFT** restore verified saved modules when their original locations are free. Occupied locations and damaged backups are preserved for review.
- **Clearer setup:** The Hub reports **Ready to switch source** for recognized providers and explains what Install will do. Unrecognized or modified source modules remain protected and require manual review.
- **Updated guide:** The PDF and text instructions cover automatic switching and recovery.

If the module and recovery folders are redirected to different drives, Companion stops before switching modules and reports the folder issue.

## Upgrading

Stop tracking before updating. Close VRCFaceTracking before installing, switching or removing its Qpro module, then reopen it. Personal models, recordings and unrelated modules are kept.

For problems, include your version and **Activity > Copy diagnostics** on the [issue tracker](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/issues).
