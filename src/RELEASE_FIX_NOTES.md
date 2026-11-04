# Qpro Companion V3.0.1 test

## Before you install

A rooted Quest Pro and the latest [VRCFaceTracking on Steam](https://store.steampowered.com/app/3329480/VRCFaceTracking/) are required. Extract the entire **QproCompanion.V3.0.1.Test.zip** into a fresh folder before opening the Hub. This is a test build; it has not been published through the updater.

Follow the PDF guide included in the ZIP or the text setup instructions in Docs.

## Changes from V3.0.0

- **Guided first-time setup:** Connect, PC runtime and VRCFT module now appear as three navigable steps, with one step visible at a time and observed component status above. Optional features and recovery stay under a separate disclosure. USB uses **Check USB connection**; Wi-Fi enable/pairing tools remain available under wireless setup options. **View setup activity** opens the full installation log. Module installation still requires VRCFaceTracking and its ModuleProcess helper to be closed.
- **Refreshed Hub layout:** Kept the dark teal theme while aligning status labels, simplifying navigation and making the Facial tracking and Model manager layouts adapt to window width. Detailed settings stay available without crowding the main controls. Empty training queues explain the next step and protected bundled models cannot be deleted.
- **Window and Activity fixes:** Corrected final row sizing during restore and resize, retained model/log scroll positions, and kept release notes reviewable during tracking while update installation remains blocked.
- **Windows Streamer updater control:** Added **First-time setup > Show optional tools and recovery > Show Windows Streamer updates**, with Check, Pause and Restore. Pause blocks network access for the verified installed PC Updater.exe using a Companion-owned Windows Defender Firewall rule. Restore removes that rule. Windows administrator approval is required for changes. Quest app updates, Streamer files and its INI are left alone. Restore before removing Companion. Cached/manual installers and application version requirements are outside this hold.
- **Repository rename compatibility:** The Companion updater now checks **Fwooffy/Qpro-Companion**, accepts the exact GitHub metadata redirect for the existing repository ID, and uses the old address as a controlled fallback. ZIP checksums and package validation remain required. Install this compatibility update before the repository is renamed; older updaters may need a manual first download if the rename happens first.
- **Guarded Virtual Desktop version compatibility:** Retained the reviewed PC fingerprints and relaxed version-only rejection. An unfamiliar PC full hash must match a conservative approved PE code/layout family, then pass loaded-image and native-interface checks before fixed-RVA hooks. An unfamiliar Quest app can attempt startup only after proving its actual Mono hand-result field/type/size layout and native caller checks. Roles, bones, freshness, leases and cleanup remain required. Unrecognized layouts are rejected; this does not promise all future versions work.
- **Clearer compatibility results:** Read-only preflight reports the installed versions, PC driver identity, component/settings problems and the next step. Version warnings do not claim live native-hook validation; reinstalling components is not a fix for an unsupported layout.
- **Guide updates:** Updated the guided setup flow and optional-tool paths, added PC updater hold and restore instructions, and clarified PC-versus-Quest hand compatibility.

## Testing status

Offline guided-setup navigation, compact-window geometry, page routing, resize/restore, Activity reading-position, updater-control, repository migration and compatibility feedback checks passed. Package checks are recorded with the delivered test ZIP. The repository metadata check also passed against GitHub without downloading or installing an update. The actual installed updater has only been inspected. Real Pause/UAC/Restore and guarded cross-version hand startup still need live testing.

This build keeps Developer tongue v15, optional Mustachio v9 and the controller add-on unchanged. It does not accept arbitrary driver/layout changes or treat a version label as live validation.

## Upgrading and feedback

Personal/imported models, recordings, saved options and connections use the existing locations. Stop tracking and close VRCFaceTracking before installing or switching its Qpro module; restart it afterward. No installation, firewall change or headset hook happens merely from extracting this ZIP.

For a problem, use **Activity > Copy diagnostics** and include the selected streaming app, Quest app version, PC Streamer version and relevant Activity lines.
