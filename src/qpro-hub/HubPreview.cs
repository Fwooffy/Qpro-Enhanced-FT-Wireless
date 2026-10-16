using System.Text.Json;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    // Rendering a preview during a guided headset capture must not take focus
    // from its capture window. Normal Hub launches retain normal activation.
    protected override bool ShowWithoutActivation => _previewOnly || base.ShowWithoutActivation;

    // These are display fixtures, never compatibility evidence. The preview
    // constructor suppresses hardware checks and persistence before this runs.
    internal void ApplyPreviewScenario(string scenario, string source)
    {
        if (!_previewOnly) throw new InvalidOperationException("Sample scenarios require preview mode.");
        if (scenario is not ("ready" or "unsupported" or "runtime-error" or "magisk" or
            "hands-ready" or "hands-blocked" or "hands-unknown" or "hands-modes" or "health-live"))
            throw new ArgumentException("Unknown preview scenario: " + scenario);
        if (source is not ("VirtualDesktop" or "SteamLink"))
            throw new ArgumentException("Unknown preview source: " + source);

        Text += " · Preview — sample data";
        var sourceName = source == "SteamLink" ? "Steam Link" : "Virtual Desktop";
        _environment.SetPreviewTrackingSource(source == "SteamLink");
        _trackingSourceSelectionUpdating = true;
        try
        {
            _trackingSourceSetup.SelectedIndex = _trackingSourceLive.SelectedIndex = source == "SteamLink" ? 1 : 0;
        }
        finally { _trackingSourceSelectionUpdating = false; }

        _eyeProfiles.Items.Clear();
        _eyeProfiles.Items.Add(new FileChoice("Prepared eye profile · sample", "preview-eye-profile.json"));
        _eyeProfiles.SelectedIndex = 0;
        _tongueModels.Items.Clear();
        _tongueModels.Items.Add(new FileChoice("Developer tongue model · highly experimental · sample", "preview-gate.pt", "preview-direction.pt", true));
        _tongueModels.Items.Add(new FileChoice("Mustachio · highly experimental · sample", "preview-mustachio-gate.pt", "preview-mustachio-direction.pt", true, true));
        _tongueModels.SelectedIndex = 0;
        _modelList.Items.Clear();
        foreach (var model in _tongueModels.Items) _modelList.Items.Add(model);
        _modelList.SelectedIndex = 0;
        _modelEmpty.Visible = false;
        _gaze.Checked = _tongue.Checked = _pupil.Checked = false;
        _hybridHands.Checked = _controllerTouchpad.Checked = _eyebrowBoost.Checked = false;
        _individualCheekPuff.Checked = _individualCheekSuck.Checked = true;
        _eyebrowSensitivity.SelectedIndex = 2;
        _eyebrowSensitivity.Enabled = false;
        _cameraPreview.Checked = false;
        foreach (var toggle in new[] { _individualCheekPuff, _individualCheekSuck, _eyebrowBoost, _cameraPreview })
            UpdateToggleStyle(toggle);
        _tongueModelNote.Text = "PREVIEW · SAMPLE DATA. " + DeveloperModelGuidance +
            " Stop tracking before changing models, then press Start tracking to load the selection.";
        _trackingSourceLiveNote.Text = _trackingSourceSetupNote.Text = $"Preview · sample data. Face tracking source: {sourceName}.";

        var rawReport = PreviewCompatibilityJson(scenario != "unsupported", scenario == "magisk");
        if (!HubCompatibilityReport.TryParse(rawReport, out var report) || report is null)
            throw new InvalidOperationException("The sample compatibility report did not pass validation.");
        ApplyCompatibilityReport(report);
        _compatibilityDiagnostic = "PREVIEW · SAMPLE DATA — not a headset diagnosis.\n" + rawReport;
        _compatibilityIdentity.Text = $"Preview · sample data. {report.Model} · build {report.BuildIncremental}\n" +
            $"{report.BuildDisplayId} · ADB and root checked · sample";
        _compatibilitySummary.Text = "Sample: " + _compatibilitySummary.Text;
        _compatibilityCameras.Text = "Sample: " + _compatibilityCameras.Text;
        _compatibilityGaze.Text = "Sample: " + _compatibilityGaze.Text;

        SetStatus(_usbStatus, StatusKind.Good, "USB connected · sample");
        SetStatus(_steamStatus, StatusKind.Good, "Running · sample");
        SetStatus(_vrcftStatus, StatusKind.Good, "Running · sample");
        SetStatus(_bridgeStatus, StatusKind.Good, "Ready · sample");
        SetStatus(_runtimeStatus, StatusKind.Good, "Verified · sample");
        SetStatus(_gazeStatus, scenario == "magisk" ? StatusKind.Good : StatusKind.Warning,
            scenario == "magisk" ? "Magisk gaze detected · sample" : "Hub gaze off · optional");
        SetStatus(_inferenceStatus, StatusKind.Warning, "Stopped");
        SetStatus(_pupilStatus, StatusKind.Warning, "Stopped");
        _setupRuntimeStatus.Text = _setupBridgeStatus.Text = "Complete · sample";
        _setupRuntimeStatus.ForeColor = _setupBridgeStatus.ForeColor = Good;
        _setupGazeStatus.Text = scenario == "magisk" ? "Provided by Magisk · Skip Hub gaze setup · sample" : "Optional · skip for Magisk gaze";
        _setupGazeStatus.ForeColor = Muted;
        _setupProgressContainer.Visible = false;
        _gaze.Enabled = scenario != "magisk";
        _setupGazeButton.Enabled = _inspectGazeButton.Enabled = scenario != "magisk";
        _setupBridgeButton.Enabled = source == "VirtualDesktop";
        _setupSteamLinkModuleButton.Enabled = source == "SteamLink";
        _hybridHands.Enabled = _controllerTouchpad.Enabled = source == "VirtualDesktop";
        _checkHandsButton.Enabled = source == "VirtualDesktop";
        PresentControllerStatus(source == "SteamLink"
                ? "Steam Link controller support is unverified · sample" : "Compatibility not checked · sample",
            "Preview only. No live finger test or game performance check was run.",
            source == "SteamLink" ? "Use Virtual Desktop to check these optional inputs."
                : "Turn on the required settings above, then check compatibility.", Warning);
        _start.Enabled = true;
        _stop.Enabled = false;
        _runStatus.Text = "● Preview — sample data, no live tracking";
        _runStatus.ForeColor = Muted;

        if (scenario == "unsupported")
        {
            SetStatus(_gazeStatus, StatusKind.Warning, "Engine not validated · sample");
            _gaze.Enabled = _setupGazeButton.Enabled = false;
            _setupGazeStatus.Text = "Unsupported engine · sample";
            _setupGazeStatus.ForeColor = Warning;
            SetActionFeedback("Independent gaze is unavailable for this build",
                "Sample result: the connected Quest Pro's tracking engine has no validated eye profile. Other tracking features can still be used.",
                "Leave Independent Eye Gaze off. Copy diagnostics with the exact headset build. Check support before using a Magisk gaze module.", severity: ActivitySeverity.Warning);
        }
        else if (scenario == "runtime-error")
        {
            SetStatus(_runtimeStatus, StatusKind.Bad, "Setup failed · sample");
            _setupRuntimeStatus.Text = "Needs repair · sample";
            _setupRuntimeStatus.ForeColor = Bad;
            _setupProgressContainer.Visible = true;
            _setupProgress.IsIndeterminate = false;
            _setupProgress.Value = 0;
            _setupProgressStatus.Text = "Sample failure: Python started, but the final PyTorch import check failed. No successful install was recorded.";
            SetActionFeedback("PC runtime needs attention",
                "Sample result: Windows could not load a required PyTorch library. Your models and captures remain available.",
                "Install the current Microsoft Visual C++ x64 runtime, then retry Install runtime. If it fails again, copy the diagnostics for support.", true);
        }
        else
        {
            SetActionFeedback("Required setup complete · sample",
                $"The sample Quest Pro, PC runtime and {sourceName} module are ready. " +
                    (scenario == "magisk" ? "A Magisk gaze module is detected; skip Hub gaze setup." : "Optional features are checked separately."),
                scenario == "magisk"
                    ? "Leave Independent Eye Gaze off in the Hub. Choose your other features on Facial tracking, then press Start tracking."
                    : "Choose the features you want on Facial tracking, then press Start tracking.");
        }

        if (scenario is "hands-ready" or "hands-blocked" or "hands-modes")
        {
            // Exercise the same validated report parser used by a real check.
            // These fixtures intentionally never claim runtimeValidated.
            bool passed = scenario != "hands-blocked";
            if (scenario == "hands-modes") _controllerTouchpad.Checked = true;
            var sampleCheck = "HANDS_CHECK " + JsonSerializer.Serialize(new
            {
                compatible = passed, androidVersion = "1.34.22.0 · sample",
                pcDriverSha256 = new string('c', 64), fridaVersion = "17.18.0 · sample",
                settings = new { hand_tracking_enabled = "true", multimodal_hands_and_controllers_enabled = "true",
                    simultaneous_hands_and_controllers_mode = "1" },
                settingSource = "current-user", experimental = true, runtimeValidated = false,
                problems = passed ? Array.Empty<string>() : ["This Virtual Desktop version has no validated hand profile. Sample result."],
                warnings = Array.Empty<string>(), componentsReady = true, componentProblems = Array.Empty<string>(),
            });
            if (!HubControllerFeedback.TryParseCheckLine(sampleCheck, out var sampleFeedback))
                throw new InvalidOperationException("The sample hand/controller report did not pass validation.");
            ShowControllerFeedback(sampleFeedback! with
            {
                Title = sampleFeedback!.Title + " · sample",
                Detail = "PREVIEW · SAMPLE DATA\n" + sampleFeedback.Detail,
            });
        }
        if (scenario == "health-live")
        {
            ApplyTrackingHealthPreview();
            _runStatus.Text = "● Preview — sample data, no live tracking";
            _runStatus.ForeColor = Muted;
        }

        // Fixed text keeps screenshots reproducible and makes simulated results
        // unmistakable when an Activity screenshot is shared on its own.
        ResetActivityLog();
        AppendActivityText("PREVIEW · SAMPLE DATA — no live headset checks were run.\n\n" +
            "[Connection] Rooted Meta Quest Pro detected · sample.\n" +
            $"[Face source] {sourceName} module selected · sample.\n" +
            (scenario == "runtime-error"
                ? "[PC runtime] Final PyTorch import check failed · sample.\n[Next step] Repair the required Windows runtime and retry.\n"
                : scenario == "unsupported"
                    ? "[Compatibility] Independent gaze engine not validated · sample.\n[Next step] Keep independent gaze off; other features are available.\n"
                    : "[PC runtime] Import and device checks passed · sample.\n[Next step] Select tracking features; optional headset features remain unverified.\n"), "12:00:00");
    }

    private static string PreviewCompatibilityJson(bool supported, bool magisk = false)
    {
        // The engine hash and profile are deliberately synthetic. Even a ready
        // fixture says that no eye patch or headset tracking change was made.
        object engine = supported
            ? new { path = "/odm/lib64/libtrackingengines.so", size = 100000,
                sha256 = new string('a', 64), profile = "sample-live-reference" }
            : new { path = "/odm/lib64/libtrackingengines.so", size = 100000,
                sha256 = new string('b', 64) };
        return JsonSerializer.Serialize(new
        {
            format = HubCompatibilityReport.Format,
            firmware = new { model = "Quest Pro", productDevice = "seacliff",
                buildIncremental = "51503870024400340 · sample", buildDisplayId = "207.0.0.218.1234 · sample",
                buildFingerprint = "sample/seacliff/preview" },
            firmwareApproved = true,
            engine,
            engineSupported = supported,
            engineProfileValidation = supported ? "live-reference" : null,
            engineCompatibilityReason = supported ? null : "Sample engine has no validated profile.",
            modelPath = "/odm/etc/eyetracking/runtime/models/Seacliff_V1_5/fbnet/int8/bolt/bolt.ptl",
            modelPathMounted = false,
            modelDiscoveryError = (string?)null,
            gazeEnvironment = new
            {
                scanComplete = true, modules = magisk ? new object[] { new {
                    directory = "sample-gaze-module", id = "sample-gaze-module", name = "Independent gaze · sample",
                    enabled = true, pendingRemoval = false, gazeRelevant = true, relevantFiles = new[] { "service.sh" }
                } } : Array.Empty<object>(), relevantMounts = Array.Empty<object>(),
                transparentOverlayMounts = Array.Empty<object>(), overlayfsOdmUpperState = "empty",
                overlayfsOdmUpperInspectedPath = "/sample/overlay/odm", experimentalModelProperty = "false",
            },
            gazeEnvironmentError = magisk ? "Active Magisk gaze module · sample." : null,
            gazePreflightPassed = !magisk,
            modelPatchValidated = false,
            headsetTrackingChanged = false,
        });
    }
}
