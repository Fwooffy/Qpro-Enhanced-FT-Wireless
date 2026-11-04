namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private void ApplySetupPreviewScenario(string scenario)
    {
        if (!_previewOnly) return;
        if (!scenario.StartsWith("setup-", StringComparison.Ordinal))
        {
            // The other preview scenes explicitly supply installed module
            // files and a verified runtime, except for the runtime failure.
            UpdateSetupNavigationReadiness(runtimeReady: scenario != "runtime-error", moduleFilesCurrent: true);
            return;
        }
        bool connected = scenario != "setup-connect";
        bool runtime = scenario is "setup-module" or "setup-complete";
        bool module = scenario == "setup-complete";
        SetStatus(_usbStatus, connected ? StatusKind.Good : StatusKind.Warning,
            connected ? "USB connected · sample" : "Not connected · sample");
        SetStatus(_runtimeStatus, runtime ? StatusKind.Good : StatusKind.Warning,
            runtime ? "Verified · sample" : "Setup needed · sample");
        SetStatus(_bridgeStatus, module ? StatusKind.Good : StatusKind.Warning,
            module ? "Files match · sample" : "Setup needed · sample");
        SetStatus(_vrcftStatus, StatusKind.Warning, "Closed for setup · sample");
        _setupRuntimeStatus.Text = runtime ? "PC runtime verified · sample" : "Install needed · sample";
        _setupRuntimeStatus.ForeColor = runtime ? Good : Warning;
        _setupBridgeStatus.Text = module ? "Module files installed · sample" : "Install needed · sample";
        _setupBridgeStatus.ForeColor = module ? Good : Warning;
        _compatibilityIdentity.Text = "Headset compatibility has not been checked · sample";
        _compatibilitySummary.Text = "Optional features are checked separately.";
        _setupProgressContainer.Visible = false;
        _start.Enabled = connected && runtime && module;
        UpdateSetupNavigationReadiness(runtime, module);
        var next = !connected ? "Connect the rooted Quest Pro and allow USB debugging."
            : !runtime ? "Install the PC runtime. Keep the Hub open while it downloads."
            : !module ? "Choose your streaming app and install its Qpro module with VRCFaceTracking closed."
            : "Reopen VRCFaceTracking and confirm expressions move in its preview, then choose features on Facial tracking.";
        SetActionFeedback("Setup preview · sample data", "These are example setup states, not live device checks.", next);
        ResetActivityLog();
        AppendActivityText("PREVIEW · SAMPLE DATA — no live headset checks or installations were run.\n[Next step] " + next + "\n", "12:00:00");
    }

    internal void ApplySetupWirelessPreview()
    {
        if (!_previewOnly) throw new InvalidOperationException("Wireless sample requires preview mode.");
        _connectionMode.SelectedIndex = 1;
        _usbActions.Visible = false;
        _wirelessSetup.Visible = true;
        _connectionModeNote.Text = "The headset and PC must be on the same trusted Wi-Fi network. Enter the Quest's address below.";
        _wirelessAddress.Clear();
        _wirelessAddress.PlaceholderText = "Example: 192.168.1.50:5555";
        SetStatus(_usbStatus, StatusKind.Warning, "Wi-Fi not connected · sample");
    }
}
