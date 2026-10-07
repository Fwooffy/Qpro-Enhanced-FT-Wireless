using System.Diagnostics;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private readonly Label _actionTitle = SectionTitle("Nothing running yet");
    private readonly Label _actionDetail = Info("Setup and tracking results appear here. Your full Activity log stays below.");
    private readonly Label _actionNext = Info("Next: complete First-time setup, then choose features on Live tracking.");
    private readonly Label _compatibilityIdentity = Info("Headset not checked. An ADB connection alone does not verify a Quest Pro or its firmware.");
    private readonly Label _compatibilitySummary = Info("Feature support has not been checked.");
    private readonly Label _compatibilityGaze = Info("Independent gaze: not checked");
    private readonly Label _compatibilityCameras = Info("Camera tracking: not checked");
    private readonly Label _compatibilityHands = Info("Hands + controllers: run the separate version check before use");
    private readonly Label _compatibilityNext = Info("Install the PC runtime, connect the headset, then check compatibility. This check does not change headset tracking.");
    private DarkButton? _checkCompatibility;
    private bool _compatibilityChecking;
    private string _compatibilityDiagnostic = "Headset compatibility has not been checked.";
    private bool _cameraInputReady;

    private async Task<string?> QuestIdentityProblemAsync()
    {
        var adb = FindAdb();
        if (adb is null) return "Android tools are missing; extract the complete release ZIP.";
        var target = GetConfiguredAdbTarget();
        var prefix = string.IsNullOrWhiteSpace(target) ? Array.Empty<string>() : new[] { "-s", target };
        var model = await RunAdbProbeAsync(adb, prefix.Concat(["shell", "getprop", "ro.product.model"]));
        var product = await RunAdbProbeAsync(adb, prefix.Concat(["shell", "getprop", "ro.product.device"]));
        if (!model.Completed || model.ExitCode != 0 || !product.Completed || product.ExitCode != 0)
            return "Headset identity could not be read. Keep it awake, reconnect ADB and retry.";
        return model.Output.Trim().Equals("Quest Pro", StringComparison.OrdinalIgnoreCase) ||
            product.Output.Trim().Equals("seacliff", StringComparison.OrdinalIgnoreCase)
            ? null : "This device was not identified as a Meta Quest Pro. Other Quest headsets are not supported by Qpro's camera and engine features.";
    }

    private void UpdateSessionReadiness()
    {
        if (_stopping || _closingInProgress || _gazeFailureHandled) return;
        var cameras = _tongue.Checked || _cameraCheekPuff.Checked || _pupil.Checked;
        var missing = new List<string>();
        if (cameras && !_cameraInputReady) missing.Add("camera frames");
        if (_hybridHands.Checked && !_handsReady) missing.Add("optical fingers");
        if (_controllerTouchpad.Checked && !_touchpadReady) missing.Add("thumb-rest input");
        _runStatus.Text = missing.Count > 0 ? "● Waiting for " + string.Join(" and ", missing) + "…"
            : cameras ? "● Camera input received · check tracking in VRCFaceTracking" : "● Selected tracking features active";
        _runStatus.ForeColor = missing.Count > 0 ? Warning : Good;
    }

    private Control BuildFeedbackCard()
    {
        var card = Card();
        card.ColumnCount = 1;
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Controls.Add(_actionSeverity);
        _actionTitle.ForeColor = Good;
        _actionDetail.ForeColor = Good;
        card.Controls.Add(_actionTitle);
        card.Controls.Add(_actionDetail);
        _actionNext.ForeColor = Accent;
        card.Controls.Add(_actionNext);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        actions.Controls.Add(ActionButton("Copy diagnostics", (_, _) => CopyDiagnostics()));
        actions.Controls.Add(ActionButton("Save log…", (_, _) => SaveDiagnostics()));
        card.Controls.Add(actions);
        return card;
    }

    private Control BuildHeadsetCompatibilityCard()
    {
        var card = Card();
        card.ColumnCount = 1;
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Controls.Add(SectionTitle("Headset compatibility"));
        card.Controls.Add(_compatibilityIdentity);
        card.Controls.Add(_compatibilitySummary);
        var details = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = new Padding(0, 8, 0, 0), Visible = false };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.Controls.Add(_compatibilityCameras);
        details.Controls.Add(_compatibilityGaze);
        details.Controls.Add(_compatibilityHands);
        _compatibilityNext.ForeColor = Accent;
        details.Controls.Add(_compatibilityNext);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        _checkCompatibility = ActionButton("Check headset compatibility", async (_, _) => await CheckHeadsetCompatibilityAsync());
        actions.Controls.Add(_checkCompatibility);
        var disclosure = ActionButton("Show firmware details", (_, _) => { });
        disclosure.Click += (_, _) => { details.Visible = !details.Visible; disclosure.Text = details.Visible ? "Hide firmware details" : "Show firmware details"; };
        actions.Controls.Add(disclosure);
        card.Controls.Add(actions);
        card.Controls.Add(details);
        return card;
    }

    // Keep the latest result visible after a popup closes. Calls from process
    // output are marshalled to the UI thread; they never start another action.
    private void SetActionFeedback(string title, string detail, string nextStep, bool isError = false,
        ActivitySeverity? severity = null)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => SetActionFeedback(title, detail, nextStep, isError, severity)); }
            catch (InvalidOperationException) { /* Closing while output arrived. */ }
            return;
        }
        var level = severity ?? (isError ? ActivitySeverity.Error : new HubActivityClassifier().Classify(title));
        _actionSeverity.Text = ActivityName(level);
        _actionSeverity.ForeColor = ActivityColor(level);
        _actionTitle.Text = title;
        _actionTitle.ForeColor = ActivityColor(level);
        _actionDetail.ForeColor = ActivityColor(level);
        if (level == ActivitySeverity.Error && (detail.Contains("Traceback (", StringComparison.Ordinal)
            || detail.Contains("CategoryInfo", StringComparison.Ordinal)
            || detail.Contains("FullyQualifiedErrorId", StringComparison.Ordinal)))
            detail = HubActionFailure.Explain(title, detail, detail).Detail;
        _actionDetail.Text = detail;
        _actionNext.Text = "Next: " + nextStep;
    }

    private string DiagnosticText()
    {
        var text = $"QproFaceTracking V{AppVersion}\nWindows build: {Environment.OSVersion.Version.Build}\n" +
            $"Face source: {_trackingSourceLive.Text}\n\n{_actionSeverity.Text}: {_actionTitle.Text}\n{_actionDetail.Text}\n{_actionNext.Text}\n\n" +
            _compatibilityDiagnostic + "\n\nACTIVITY\n" + _log.Text;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) text = text.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        // The firmware report already excludes serials. Mask common serial log
        // fields as well, while retaining build and engine identifiers.
        return Regex.Replace(text, @"(?im)(ADB target:|(?<!\w)serial[=:])\s*[^\r\n]+", "$1 [redacted]");
    }

    private void CopyDiagnostics()
    {
        try { Clipboard.SetText(DiagnosticText()); }
        catch (Exception error) { AppendLog("Could not copy diagnostics: " + error.Message); }
    }

    private void SaveDiagnostics()
    {
        using var dialog = new SaveFileDialog { Filter = "Text log (*.txt)|*.txt", FileName = "Qpro-diagnostics.txt", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, DiagnosticText()); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Log could not be saved", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private async Task CheckHeadsetCompatibilityAsync()
    {
        if (_previewOnly || _compatibilityChecking || UtilityActionIsBusy()) return;
        if (_starting || _stopping || _trackingProcesses.Any(p => !p.HasExited))
        {
            SetActionFeedback("Compatibility check is waiting", "Stop Qpro live overrides before checking the headset.", "Press Stop tracking, then retry the compatibility check.");
            return;
        }
        _compatibilityChecking = _utilityActionRunning = true;
        _compatibilityDiagnostic = "Headset compatibility check is in progress; no current result is verified.";
        _checkCompatibility!.Enabled = false;
        UpdateControlState();
        SetActionFeedback("Checking headset compatibility", "Reading the model, firmware and eye-engine identity. No tracking changes are applied.", "Keep the headset connected and awake until the result appears.");
        try
        {
            if (!await HasQuestAsync()) throw new InvalidOperationException("No single authorized headset is connected. Connect by USB or use Connect to Quest, then allow USB debugging.");
            AppendLog("[Compatibility] Read-only headset and tracking-engine check started.");
            var start = PowerShellStart("prepare-eye-model.ps1", ["-Diagnose"], hidden: true);
            var result = await HubProcessResult.RunAsync(start, TimeSpan.FromSeconds(70));
            _compatibilityDiagnostic = result.Diagnostics;
            foreach (var line in result.Diagnostics.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                AppendLog("[Compatibility] " + line);
            if (result.TimedOut)
                throw new TimeoutException("The headset did not complete its read-only check within 70 seconds. Reconnect it and retry.");
            if (result.CleanupError is not null)
                throw new InvalidOperationException(result.CleanupError);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"The compatibility checker failed with code {result.ExitCode}. Check Activity for its complete output.");
            if (!HubCompatibilityReport.TryParse(result.Output, out var report) || report is null)
                throw new InvalidOperationException("The checker returned an incomplete compatibility report. Extract the complete ZIP and retry.");
            ApplyCompatibilityReport(report);
            AppendLog("[Compatibility] " + _compatibilityIdentity.Text);
            AppendLog("[Compatibility] " + _compatibilityGaze.Text);
        }
        catch (Exception error)
        {
            _compatibilityDiagnostic = "CURRENT CHECK FAILED: " + error.Message + "\n" + _compatibilityDiagnostic;
            _compatibilityIdentity.Text = "Headset verification did not complete.";
            _compatibilitySummary.Text = "Feature support remains unverified; check Activity for the missing prerequisite.";
            _compatibilitySummary.ForeColor = Warning;
            _compatibilityCameras.Text = "Camera tracking: not verified";
            _compatibilityGaze.Text = "Independent gaze: not verified";
            _compatibilityNext.Text = "Check the connection, root permission and PC runtime, then retry. No compatibility result is assumed.";
            AppendLog("[Compatibility] " + error.Message);
            SetActionFeedback("Headset check needs attention", error.Message, "Read the error above, fix the connection or setup issue it names, then press Check headset compatibility again.", true);
        }
        finally
        {
            _compatibilityChecking = _utilityActionRunning = false;
            if (!IsDisposed) { _checkCompatibility!.Enabled = true; UpdateControlState(); }
        }
    }

    private void ApplyCompatibilityReport(HubCompatibilityReport report)
    {
        _compatibilityIdentity.Text = $"{report.Model} · build {report.BuildIncremental}\n{report.BuildDisplayId} · ADB and root checked at {DateTime.Now:HH:mm}";
        _compatibilitySummary.Text = !report.EngineSupported
            ? "Independent gaze: engine unsupported. Other features are checked separately."
            : !report.CanPrepareGaze ? "Independent gaze: headset setup needs attention."
            : report.EngineProfileValidation == "firmware-analysis" ? "Independent gaze: engine recognized from firmware files; live tracking still needs testing."
            : "Independent gaze: engine recognized; preparation and live input check still needed.";
        _compatibilitySummary.ForeColor = report.NeedsAttention ? Warning : Muted;
        _compatibilityCameras.Text = "Camera tracking: Quest Pro and root verified; camera frames and GPU are checked when tracking starts";
        _compatibilityGaze.Text = "Gaze detail: " + report.Summary;
        _compatibilityGaze.ForeColor = report.NeedsAttention ? Warning : Muted;
        _compatibilityNext.Text = report.NextStep;
        // A report is an observation, never permission to bypass the launcher's
        // exact engine, stock model and recovery checks on the next session.
        SetStatus(_gazeStatus, StatusKind.Warning, report.CanPrepareGaze ? "Check passed · prepare first" : "Needs compatibility review");
        SetActionFeedback("Headset compatibility checked", report.Summary, report.NextStep,
            severity: report.NeedsAttention ? ActivitySeverity.Warning : ActivitySeverity.Normal);
    }
}
