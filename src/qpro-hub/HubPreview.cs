using System.Text.Json;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    // These are display fixtures, never compatibility evidence. The preview
    // constructor suppresses hardware checks and persistence before this runs.
    internal void ApplyPreviewScenario(string scenario, string source)
    {
        if (!_previewOnly) throw new InvalidOperationException("Sample scenarios require preview mode.");
        if (scenario is not ("ready" or "unsupported" or "runtime-error"))
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
        _tongueModels.Items.Add(new FileChoice("Developer lower-face model · sample", "preview-gate.pt", "preview-direction.pt"));
        _tongueModels.Items.Add(new FileChoice("Mustachio · highly experimental · sample", "preview-mustachio-gate.pt", "preview-mustachio-direction.pt", true, true));
        _tongueModels.SelectedIndex = 0;
        _modelList.Items.Clear();
        foreach (var model in _tongueModels.Items) _modelList.Items.Add(model);
        _modelList.SelectedIndex = 0;
        _modelEmpty.Visible = false;
        _gaze.Checked = _tongue.Checked = _pupil.Checked = _cameraCheekPuff.Checked = false;
        _hybridHands.Checked = _controllerTouchpad.Checked = _eyebrowBoost.Checked = false;
        _individualCheekPuff.Checked = _individualCheekSuck.Checked = true;
        _cheekPuffStyle.SelectedIndex = 1;
        _cheekSuckStyle.SelectedIndex = 1;
        _cheekPuffStyle.Enabled = _cheekSuckStyle.Enabled = true;
        _eyebrowSensitivity.SelectedIndex = 2;
        _eyebrowSensitivity.Enabled = false;
        _cameraPreview.Checked = true;
        foreach (var toggle in new[] { _individualCheekPuff, _individualCheekSuck, _eyebrowBoost, _cameraPreview })
            UpdateToggleStyle(toggle);
        _tongueModelNote.Text = "Sample model list. Your installed models appear here in the app.";
        _trackingSourceLiveNote.Text = _trackingSourceSetupNote.Text = $"Preview · sample data. Face tracking source: {sourceName}.";

        var rawReport = PreviewCompatibilityJson(scenario != "unsupported");
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
        SetStatus(_gazeStatus, StatusKind.Warning, "Optional · not checked");
        SetStatus(_inferenceStatus, StatusKind.Warning, "Stopped");
        SetStatus(_pupilStatus, StatusKind.Warning, "Stopped");
        _setupRuntimeStatus.Text = _setupBridgeStatus.Text = "Complete · sample";
        _setupRuntimeStatus.ForeColor = _setupBridgeStatus.ForeColor = Good;
        _setupGazeStatus.Text = "Optional · check before use";
        _setupGazeStatus.ForeColor = Muted;
        _setupProgressContainer.Visible = false;
        _gaze.Enabled = true;
        _setupGazeButton.Enabled = true;
        _setupBridgeButton.Enabled = source == "VirtualDesktop";
        _setupSteamLinkModuleButton.Enabled = source == "SteamLink";
        _hybridHands.Enabled = _controllerTouchpad.Enabled = source == "VirtualDesktop";
        _handsStatus.Text = source == "SteamLink"
            ? "Hands and controllers require Virtual Desktop; Steam Link support is unverified."
            : "Optional. Check the connected headset and Virtual Desktop versions before enabling.";
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
                $"The sample Quest Pro, PC runtime and {sourceName} module are ready. Optional features are checked separately.",
                "Choose the features you want on Live tracking, then press Start tracking.");
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

    private static string PreviewCompatibilityJson(bool supported)
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
                scanComplete = true, modules = Array.Empty<object>(), relevantMounts = Array.Empty<object>(),
                transparentOverlayMounts = Array.Empty<object>(), overlayfsOdmUpperState = "empty",
                overlayfsOdmUpperInspectedPath = "/sample/overlay/odm", experimentalModelProperty = "false",
            },
            gazeEnvironmentError = (string?)null,
            gazePreflightPassed = true,
            modelPatchValidated = false,
            headsetTrackingChanged = false,
        });
    }
}
