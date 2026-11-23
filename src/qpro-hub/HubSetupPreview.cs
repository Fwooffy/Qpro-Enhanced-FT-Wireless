namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private Func<string, string, string[], Task<bool>>? _previewSetupCommand;
    private bool SetupCommandsAvailable => !_previewOnly || _previewSetupCommand is not null;

    internal void ConfigurePreviewSetupCommands(Func<string, string, string[], Task<bool>> runCommand)
    {
        if (!_previewOnly) throw new InvalidOperationException("Setup command fixtures require preview mode.");
        _previewSetupCommand = runCommand;
    }

    private Task<bool> RunSetupCommandAsync(string label, string script, string[] arguments) => _previewOnly
        ? _previewSetupCommand?.Invoke(label, script, arguments) ?? Task.FromResult(false)
        : RunUtilityAsync(label, script, arguments);

    private void ApplySetupPreviewScenario(string scenario)
    {
        if (!_previewOnly) return;
        if (!scenario.StartsWith("setup-", StringComparison.Ordinal)) return;
        bool backup = scenario == "setup-backup";
        bool connected = scenario is not ("setup-connect" or "setup-backup");
        bool runtime = scenario is "setup-vrchat" or "setup-complete";
        bool vrchatConnected = scenario == "setup-complete";
        SetStatus(_usbStatus, connected ? StatusKind.Good : StatusKind.Warning,
            connected ? "USB connected · sample" : "Not connected · sample");
        SetStatus(_runtimeStatus, runtime ? StatusKind.Good : StatusKind.Neutral,
            runtime ? "Verified · sample" : "Optional · sample");
        SetStatus(_directVrchatStatus, vrchatConnected ? StatusKind.Good : StatusKind.Warning,
            backup ? "Backup module not installed · sample" : vrchatConnected ? "OSCQuery connected · sample" : "Waiting for VRChat · sample");
        _directVrchatStatus.AccessibleDescription = backup ? "Sample module status. No installed modules were checked." : vrchatConnected
            ? "Sample OSCQuery response. No live VRChat connection was checked."
            : "Sample pending state. Open VRChat and enable OSC, then check the connection.";
        _setupRuntimeStatus.Text = runtime ? "PC runtime verified · sample" : "Optional for native face and eyes · sample";
        _setupRuntimeStatus.ForeColor = runtime ? Good : Muted;
        _compatibilityIdentity.Text = "Headset compatibility has not been checked · sample";
        _compatibilitySummary.Text = "Optional features are checked separately.";
        _setupProgressContainer.Visible = false;
        _start.Enabled = true;
        var next = backup ? "Close VRCFaceTracking, install the module for your streaming app, then reopen VRCFaceTracking." : !connected ? "Use VRChat output for native face and eyes, or connect the rooted Quest Pro for the optional features."
            : !runtime ? "Use VRChat output for native face and eyes, or install the PC runtime for the optional features."
            : !vrchatConnected ? "Choose your streaming app, enable OSC in VRChat, then check the connection."
            : "Test native expressions on your VRChat avatar. Choose any optional features on Facial tracking.";
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
