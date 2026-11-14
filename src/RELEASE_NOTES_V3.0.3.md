# Qpro Companion V3.0.3

## Before you install

A rooted Quest Pro and the latest [VRCFaceTracking on Steam](https://store.steampowered.com/app/3329480/VRCFaceTracking/) are required. Download **QproCompanion.V3.0.3.zip** and extract the entire folder. GitHub's automatically generated source archives do not contain the ready-to-run app.

Follow the PDF guide included in the ZIP or the text setup instructions in **Docs**.

## What's changed

- **Newer firmware checks for Touch Pro thumb-rest input:** Replaced the exact firmware-build restriction with an experimental read-only check of the existing controller data layout. Build **51503870035800340** and later numeric builds can attempt the same checks. Firmware updates that preserve this layout no longer need a separate application update for their build number.
- **Stronger sensor validation:** Both controller pages and both sample buffers must be consistent, with fresh updates from each controller before extra input starts. Changed mappings, malformed data or counter rollback suspend the affected input until the checks pass again. A sleeping controller stays neutral while its already-admitted awake partner can continue.
- **Clearer startup errors:** A completed source or layout refusal reports its actual reason. The supervisor confirms cleanup after disabling the add-on instead of labelling every early refusal as unconfirmed restoration. Interrupted ADB connections and genuine cleanup failures still require attention.
- **Controller behaviour preserved:** Trackpad, Relative joystick, Swipe and Desktop mouse keep their existing input processing, sensitivity, Left/Right/Both selection and SteamVR bindings. The Windows controller driver is byte-identical to V3.0.2. Normal controller buttons, poses, haptics and optical hand routing retain their existing paths.
- **Guide refresh:** Updated the bundled guide and instructions for the new firmware-check behaviour.

## Experimental firmware support

The probe checks data consistency. It cannot establish that unfamiliar firmware gives those fields the same physical meaning. Thumb movement, contact, pressure and in-game behaviour on **51503870035800340** have not been live tested here. Both controllers must be awake for the initial check; confirm left and right input after a headset firmware update. An actual controller-data format change may still require an updated reader profile.

## Upgrading

Stop tracking before updating. Models, recordings, saved options and connections retain their existing locations. Close VRCFaceTracking before installing or updating its Qpro module, then reopen it.

For problems, include the headset build, selected controller mode and **Activity > Copy diagnostics** on the [issue tracker](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless/issues).
