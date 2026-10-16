# Qpro Companion

This is the native Windows control app for the Quest Pro tracking package. The Hub is a WinForms application targeting .NET 10. It keeps the existing PowerShell, Python, headset, and VRCFaceTracking components as the tracking back end.

## Pages

- **Facial tracking:** compact Your session status followed by Eyes and Lower-face tracking; persistent Start tracking and Stop tracking buttons. Eyes groups independent gaze, pupil dilation and eyebrow movement. Lower-face tracking groups Tongue model, tongue output and native Individual cheek puff (1/0) / Individual cheek suck (1/0) switches. Camera settings and explanatory details expand when needed. Disabled cheek switches pass through the streaming app's original values. Start applies the selected cheek switches; Stop or closing the Hub restores native cheeks.
- **Controllers:** separate sidebar page immediately after Facial tracking. Contains the existing hand/controller compatibility check, switches, thumb-rest modes and side selection. Optional installation stays on First-time setup. The fixed bottom Start tracking / Stop tracking bar controls all selected features across pages.
- **First-time setup:** USB/Wi-Fi connection selection and wireless enable, connect, and pairing controls; PC runtime, optional AMD ROCm installer, separate Virtual Desktop and Steam Link Qpro VRCFaceTracking module buttons, and gaze preparation. Installing one source module removes the other.
- **Tongue training/refinement (Personalize):** Quick refinement has 16 tongue cards, Focused diagonals + facial hair has 22, and Full dataset has 58. Quick and Focused extend the selected tongue model; Full trains a new personal model from scratch. Each has a separate dataset queue and publishes a complete tongue model pair. There are no current cheek camera cards or cheek-head training steps.
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
- `HubModuleHeartbeat.cs` and `HubModuleHeartbeatUi.cs`: validate and display the selected module's process, source, output slots and five current live channels.

Build with `dotnet build qpro-hub/QproFaceTracking.Hub.csproj -c Release` from `src/`. Pass `--root <complete release folder>` when launching a build outside the packaged release. The Hub does not bundle its headset binaries, pretrained models, Python runtime, or Android tools; those remain in the complete release ZIP.

The interface uses per-monitor DPI awareness. Text widths and the sidebar respond to window size; content pages scroll vertically at the minimum size, while Start tracking and Stop tracking remain visible at the bottom. The preview command can render compact windows for layout checks, for example `QproFaceTracking.Hub.exe --root <release folder> --render-preview <output.png> --preview-viewport 700 500 --preview-page setup --preview-scroll-bottom`. Compact previews do not replace testing on a real high-DPI monitor.

Native individual cheek puff and suck use toggleable **1/0** separation of the selected source's signals. A clear one-sided expression reaches full strength on that side and zero on the other; a deliberate two-sided expression can move both. No camera cheek model, calibration menu or trained cheek profile is used. Historical saved calibration data stays on disk but is unused by the current Hub.

The pupil option is experimental. Its image and packet tests pass, and relative pupil animation has been live tested on a Quest Pro in VRChat. A Hub build or screenshot preview alone does not verify camera and avatar behavior on other systems.

## Model and module checks

Fresh setups select **Developer tongue model**. Its new visibility detector retains the original developer tongue direction outputs. A separate same-wearer recording showed fewer visible-tongue misses with native fusion, with one smile-tip miss remaining; some older beard poses worsened. Broader facial-hair and independent-wearer validation remain pending. **Mustachio** remains an opt-in experimental alternative. Updates preserve personal/imported selections and redirect verified retired bundled choices to the incoming developer model.

The module heartbeat has **five live channels**: `gaze-left`, `gaze-right`, `tongue`, `pupil-left` and `pupil-right`. It verifies source and installed DLL identity, module process identity, output ownership and freshness before displaying a live-output result. Native cheek adjustments are session settings, not a camera-output channel. Matching installed files or a green process indicator does not prove that expressions move correctly in VRCFaceTracking or an avatar.

Pupil image preprocessing can use a verified GPU runtime. CPU handles contour geometry and smoothing. Latest-frame workers and subscriber-only JPEG encoding bound queued work; Activity reports backend and timing.
