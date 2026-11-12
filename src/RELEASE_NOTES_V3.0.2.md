# Qpro Companion V3.0.2 test

## Before you install

A rooted Quest Pro and the latest [VRCFaceTracking on Steam](https://store.steampowered.com/app/3329480/VRCFaceTracking/) are required. Extract the entire **QproCompanion.V3.0.2.Test.zip** into a fresh folder before opening the Hub. This is a test build; publishing it through the updater is a separate step.

Follow the PDF guide included in the ZIP or the text setup instructions in Docs.

## Changes from V3.0.1

- **Optional setup and recovery buttons:** Restored collapsed actions in gaze, AMD, hands, runtime recovery and module maintenance. Buttons retain readable heights when submenus open or the window resizes.
- **Update package compatibility:** Fixed rejection of Qpro Companion ZIP folders. Future packages retain the internal folder name expected by V3.0.0, while the download keeps the Qpro Companion name. File, manifest and checksum verification remain required.
- **Readable updater notes:** Release notes now show headings, bold and italic text, lists and code formatting in the Hub instead of displaying the Markdown punctuation as plain text.
- **Clickable release links:** HTTP and HTTPS links in the notes open only when clicked. Reading notes does not download an update or run an installer.
- **Clearer VRCFaceTracking module status:** The installation badge now says **Installed · files verified**. **View module input** opens the separate live status in Activity without starting tracking. Virtual Desktop's normal lack of an input timestamp no longer appears as a warning, and missing or failed module status explains what to check next.
- **Easier Activity reading:** **Pause display** holds the log still while tracking and collection continue. **Resume display** returns to your reading position where possible, and **Jump to latest** follows new messages. Copy/save includes messages received while paused, within the normal recent-history limit.
- **Safer diagnostic sharing:** Copy/save masks more common user-folder paths, headset IDs and local network addresses while retaining useful errors and versions. Review the text before posting; no models or recordings are attached, and nothing is uploaded automatically.
- **Clearer controller checks:** **Controller requirements ready** separates the setup precheck from live finger input. Missing Quest hand settings get the relevant Quest and Singularity steps, while runtime compatibility problems retain their specific explanation.
- **Lighter status checks:** A status refresh reuses its ROCm discovery and verified current-module result, and releases all process handles after checking running apps. Tracking models and processing settings are unchanged; a live FPS improvement has not been measured.
- **Existing update safeguards retained:** Version selection, update downloads, checksum and package verification, installation permissions and preservation of models, options and connections use the existing update process.
- **Guide refresh:** The setup documents explain the new status wording and formatted update notes.

## Upgrading and feedback

Older Hub versions continue to show plain release notes until upgraded. Formatting this release's notes cannot change an older installed app's display.

If V3.0.0 reports that the V3.0.1 ZIP does not contain a Qpro app folder, its installed updater needs a corrected release ZIP or a manual upgrade. Retrying the original V3.0.1 asset will produce the same error; no installed files are replaced at that preparation stage.

Tracking model weights, native controller mappings, runtime packages and headset compatibility rules are unchanged. These interface and diagnostic changes do not establish support for additional firmware or a new Virtual Desktop build. The guided setup and compact **Optional features**, **Checks & Updates** and **Recovery** groups from V3.0.1 remain available.

Personal/imported models, recordings, saved options and connections keep their existing locations. Stop tracking before updating. Close VRCFaceTracking before installing or updating its Qpro module, then reopen it afterward.

For a problem, use **Activity > Copy diagnostics** and include the relevant Activity lines and which update action you selected.
