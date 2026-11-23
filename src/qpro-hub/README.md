# Qpro Companion

The Hub is a WinForms application targeting .NET 10. **Direct VRChat** reads native Virtual Desktop or Steam Link data and uses OSCQuery to discover local VRChat, with a verified local HTTP fallback. **VRCFaceTracking backup** delegates that output to the matching Qpro module. Native face and eye forwarding can run without ADB or Python. PowerShell, Python and ADB are helpers for optional features and setup.

## Pages

- **Facial tracking:** compact Your session status followed by Eyes and Lower-face tracking; persistent Start tracking and Stop tracking buttons. Eyes groups independent gaze, pupil dilation and eyebrow movement. Lower-face tracking groups Tongue model, tongue output and native Individual cheek puff (1/0) / Individual cheek suck (1/0) switches. Camera settings and explanatory details expand when needed. Disabled cheek switches pass through the source's original values. Start applies selected extras. Stop restores ordinary native forwarding. Closing the Hub stops forwarding.
- **Controllers:** separate sidebar page immediately after Facial tracking. Contains the existing hand/controller compatibility check, switches, thumb-rest modes and side selection. Optional installation stays on First-time setup. The fixed bottom Start tracking / Stop tracking bar controls selected extras across pages.
- **First-time setup:** **VRChat output**, **Headset** and **PC runtime** sections. Select **Direct to VRChat** and close VRCFaceTracking to use the direct path. **Check VRChat connection** checks the local game after OSC is enabled. Choosing **VRCFaceTracking backup** exposes **Install Qpro backup module**. Installation saves recognized source modules for recovery and requires VRCFaceTracking and its helper to be closed. **Choose tracking features** opens the controls for optional features.
- **Personalize:** Quick refinement has 16 tongue cards, Focused diagonals + facial hair has 22, and Full dataset has 58. Quick and Focused extend the selected tongue model; Full trains a new personal model from scratch. Each has a separate dataset queue and publishes a complete tongue model pair. There are no current cheek camera cards or cheek-head training steps.
- **Model manager:** rename, export, import, and delete paired tongue models. The two bundled choices are Developer tongue model and Mustachio; personal imports/refinements remain available. Historical combined models are used for their tongue outputs.
- **Activity:** Tracking health and Show tracking details above Detailed activity, with setup, training, tracking and error output.

## Source layout

- `Program.cs`: entry point, release self-test, and preview capture.
- `HubForm.cs` and `HubLayoutV2.cs`: window state and page layout.
- `HubWorkflows.cs` and `HubData.cs`: setup, tracking, capture, training, datasets, and model operations.
- `HubCameraTrackingLaunch.cs`: validates camera selections and builds the arguments used by tracking startup. The same builder runs in the headless launch-selection tests.
- `HubEnvironment.cs`: ADB connection and local dependency discovery.
- `HubScriptFactory.cs`: launches package scripts with the selected Python and ADB environment.
- `HubSystem.cs`, `HubTheme.cs`, and `HubControls.cs`: status updates, styling, and custom controls.
- `HubModuleHeartbeat.cs` checks the backup module's output ownership. Direct face input and destination checks run separately.

Build with `dotnet build qpro-hub/QproFaceTracking.Hub.csproj -c Release` from `src/`. Pass `--root <complete release folder>` when launching a build outside the packaged release. The Hub does not bundle its headset binaries, pretrained models, Python runtime, or Android tools; those remain in the complete release ZIP.

The interface uses per-monitor DPI awareness. Text widths and the sidebar respond to window size; content pages scroll vertically at the minimum size, while Start tracking and Stop tracking remain visible at the bottom. The preview command can render compact windows for layout checks, for example `QproFaceTracking.Hub.exe --root <release folder> --render-preview <output.png> --preview-viewport 700 500 --preview-page setup --preview-scroll-bottom`. Compact previews do not replace testing on a real high-DPI monitor.

Native individual cheek puff and suck use toggleable **1/0** separation of the selected source's signals. A clear one-sided expression reaches full strength on that side and zero on the other; a deliberate two-sided expression can move both. No camera cheek model, calibration menu or trained cheek profile is used. Historical saved calibration data stays on disk but is unused by the current Hub.

The pupil option is experimental. Its image and packet tests pass, and relative pupil animation has been live tested on a Quest Pro in VRChat. A Hub build or screenshot preview alone does not verify camera and avatar behavior on other systems.

## Model and direct-output checks

Fresh setups select **Developer tongue model**. Its new visibility detector retains the original developer tongue direction outputs. A separate same-wearer recording showed fewer visible-tongue misses with native fusion, with one smile-tip miss remaining; some older beard poses worsened. Broader facial-hair and independent-wearer validation remain pending. **Mustachio** remains an opt-in experimental alternative. Updates preserve personal/imported selections and redirect verified retired bundled choices to the incoming developer model.

The direct session reports source and VRChat destination observations. Verify movement on the avatar. The backup heartbeat fixtures cover VRCFaceTracking process identity, ownership and five timed override channels. Direct VRChat and backup each need their own live checks.

Pupil image preprocessing can use a verified GPU runtime. CPU handles contour geometry and smoothing. Latest-frame workers and subscriber-only JPEG encoding bound queued work; Activity reports backend and timing.

## Guarded controller version checks

The manual compatibility check is read-only preflight. A different Virtual Desktop app version label no longer blocks a trial by itself. An unfamiliar PC full hash must match the conservative approved PE code/layout family, with the actual loaded image and native interfaces validated before fixed-RVA hooks. On the Quest, startup must establish the actual Mono hand-result fields, types, sizes and layout. Native caller, controller-role, bone, freshness, lease and cleanup checks remain required. Frida, component and settings requirements are unchanged.

Neither green prechecks nor a family match proves live optical fingers or game performance. Changed code/layout families and unsupported native layouts still reject cleanly; this does not promise all future Virtual Desktop versions work. The first researched Android build and historical exact-hash PC inspections remain evidence, not blanket version support.
