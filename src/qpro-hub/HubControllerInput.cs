using System.Diagnostics;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private readonly CheckBox _hybridHands = FeatureToggle("Experimental hands + controllers", false);
    private readonly CheckBox _controllerTouchpad = FeatureToggle("Experimental Touch Pro thumb-rest input", false);
    private readonly ComboBox _touchpadMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label _handsStatus = new() { AutoSize = true, ForeColor = Muted, Tag = "responsive-info" };
    private readonly DarkButton _installHandsButton = SetupButton("Install hand/controller components");
    private readonly DarkButton _removeHandsButton = SetupButton("Uninstall controller add-on");
    private readonly DarkButton _checkHandsButton = SecondaryButton("Check hand/controller compatibility");
    private bool _handsReady;
    private bool _touchpadReady;
    private string? _controllerFeedbackText;
    private bool _controllerFeedbackWarning;
    private bool _controllerCleanupFailed;
    private bool _controllerInputStopped;
    private Task<HubControllerFeedback>? _controllerRestoration;
    private Process? _controllerProcess;
    private string ControllerStopFile => _stopFile + "-controllers";
    private bool ControllerCleanupUnconfirmed => _controllerCleanupFailed ||
        _controllerRestoration is { IsCompletedSuccessfully: true } result && result.Result.IsError;
    private bool ControllerRestartBlocked => (_hybridHands.Checked || _controllerTouchpad.Checked) &&
        (ControllerCleanupUnconfirmed || _controllerRestoration is { IsCompleted: false });

    private string ControllerComponentsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "hands");
    private string ControllerAddonRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "controller-addon");

    private void InitializeControllerInputUi()
    {
        _touchpadMode.Items.AddRange(["Trackpad", "Relative joystick", "Swipe", "Desktop mouse"]);
        _touchpadMode.SelectedIndex = 0;
        ConfigureDropDown(_touchpadMode);
        _hybridHands.CheckedChanged += (_, _) => { UpdateToggleStyle(_hybridHands); UpdateControllerInputAvailability(); };
        _controllerTouchpad.CheckedChanged += (_, _) => { UpdateToggleStyle(_controllerTouchpad); UpdateControllerInputAvailability(); };
        UpdateToggleStyle(_hybridHands);
        UpdateToggleStyle(_controllerTouchpad);
        _installHandsButton.Click += async (_, _) =>
        {
            if (Process.GetProcessesByName("vrserver").Length != 0)
            {
                MessageBox.Show(this, "Close SteamVR before installing these optional components, then reopen SteamVR when setup finishes.",
                    "Close SteamVR first", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            await RunSetupStepAsync("Hand/controller components", "controller-input.ps1",
                "Optional components installed.", "Reopen SteamVR through Virtual Desktop, then run Check hand/controller compatibility. These features remain experimental.");
        };
        _removeHandsButton.Click += async (_, _) =>
        {
            if (Process.GetProcessesByName("vrserver").Length != 0)
            {
                MessageBox.Show(this, "Close SteamVR before uninstalling the controller add-on. Reopen SteamVR afterward to reload the normal controller profile.",
                    "Close SteamVR first", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            await RunUtilityAsync("Uninstall controller add-on", "controller-input.ps1", "-Action", "uninstall");
        };
        _checkHandsButton.Click += async (_, _) =>
            await RunUtilityAsync("Hand/controller compatibility", "controller-input.ps1", "-Action", "check");
        UpdateControllerInputAvailability();
    }

    private string TouchpadModeValue() => _touchpadMode.SelectedIndex switch
    {
        1 => "joystick", 2 => "swipe", 3 => "mouse", _ => "trackpad",
    };

    private void UpdateControllerInputAvailability()
    {
        bool idle = !_closingInProgress && !_trackingCleanupPending && !_gazeRecoveryRunning &&
            !_starting && !_stopping && !_utilityActionRunning && !_setupActionRunning &&
            !_datasetOperationBusy && !LiveTrackingRunning;
        bool supportedSource = !_environment.SteamLinkSelected;
        _hybridHands.Enabled = _controllerTouchpad.Enabled = idle && supportedSource;
        _touchpadMode.Enabled = idle && supportedSource && _controllerTouchpad.Checked;
        _installHandsButton.Enabled = idle && supportedSource;
        _removeHandsButton.Enabled = idle && Directory.Exists(ControllerAddonRoot);
        _checkHandsButton.Enabled = idle && supportedSource && BackendReady();
        if (idle)
        {
            if (!supportedSource) _hybridHands.Checked = _controllerTouchpad.Checked = false;
            if (ControllerCleanupUnconfirmed)
            {
                _handsStatus.Text = "Controller restoration needs attention. Keep controller input off; close SteamVR, restart the headset and reopen the Hub before checking controller compatibility.";
                _handsStatus.ForeColor = Warning;
            }
            else if (!supportedSource)
            {
                _handsStatus.Text = "Experimental controller inputs currently require Virtual Desktop. Steam Link compatibility has not been verified.";
                _handsStatus.ForeColor = Muted;
            }
            else
            {
                _handsStatus.Text = _controllerFeedbackText ?? "Optional, off by default. Check compatibility before use. Finger routing and thumb-rest input require a rooted Quest Pro; supported runtime versions are listed in the controller guide.";
                _handsStatus.ForeColor = _controllerFeedbackWarning ? Warning : Muted;
            }
        }
    }

    private void AddControllerPrerequisites(List<string> missing)
    {
        if (!_hybridHands.Checked && !_controllerTouchpad.Checked) return;
        if (ControllerRestartBlocked)
            missing.Add("controller restoration — stop other tracking, close SteamVR, restart the headset and reopen the Hub before checking controller compatibility");
        if (_environment.SteamLinkSelected) missing.Add("Virtual Desktop for the experimental hand/controller features");
        if (_hybridHands.Checked && !File.Exists(Path.Combine(ControllerComponentsRoot, "ready.json")))
            missing.Add("the optional hand components — close SteamVR and use Install hand/controller components");
        if (_controllerTouchpad.Checked && !File.Exists(Path.Combine(ControllerAddonRoot, "qpro-owner.json")))
            missing.Add("the controller add-on — close SteamVR and use Install hand/controller components");
    }

    private string[] ControllerInputArguments()
    {
        // The supervisor writes its stop marker during failure cleanup. It
        // must never share the camera/gaze stop signal.
        var arguments = new List<string> { "-Action", "run", "-StopFile", ControllerStopFile };
        if (_hybridHands.Checked) arguments.Add("-Hands");
        if (_controllerTouchpad.Checked) arguments.Add("-Touchpad");
        arguments.AddRange(["-Mode", TouchpadModeValue()]);
        return arguments.ToArray();
    }

    private void StartControllerInput()
    {
        if (!_hybridHands.Checked && !_controllerTouchpad.Checked) return;
        _handsStatus.Text = "Checking controller compatibility; waiting for valid input…";
        _controllerFeedbackText = null;
        _controllerFeedbackWarning = false;
        _controllerInputStopped = false;
        _handsReady = _touchpadReady = false;
        if (_controllerTouchpad.Checked && TouchpadModeValue() == "mouse")
            AppendLog("Desktop mouse mode selected: thumb-rest gestures can move the Windows pointer. Stop tracking disables this input.");
        try
        {
            if (ControllerRestartBlocked) throw new InvalidOperationException("Controller restoration is unconfirmed. Stop other tracking, close SteamVR, restart the headset and reopen the Hub before checking controller compatibility.");
            File.Delete(ControllerStopFile);
            StartManaged("Hand/controller input", "controller-input.ps1", ControllerInputArguments());
        }
        catch (Exception error) { ControllerInputStartupFailed(error); }
    }

    private void ControllerInputStartupFailed(Exception error)
    {
        _controllerInputStopped = true;
        _handsReady = _touchpadReady = false;
        // A reader-attachment failure may leave an owned child running. Stop
        // that child through its private signal, retaining it for supervision.
        if (_controllerProcess is { HasExited: false })
        {
            _controllerCleanupFailed = true;
            try { RequestControllerStop(); }
            catch (IOException stopError) { AppendLog("Controller stop request needs attention: " + stopError.Message); }
        }
        AppendLog("Optional controller input could not start: " + error.Message);
        var otherTracking = _trackingProcesses.Any(worker => worker != _controllerProcess && !worker.HasExited);
        SetActionFeedback("Optional controller input could not start", error.Message,
            otherTracking ? "Other Qpro tracking continues. Check Activity and stop the session before retrying controller input."
                : "Check Activity before retrying optional controller input.", true);
        if (otherTracking) UpdateSessionReadiness();
    }

    private void RequestControllerStop()
    {
        var failures = new List<Exception>();
        try { File.WriteAllText(ControllerStopFile, DateTimeOffset.Now.ToString("O")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures.Add(error); }
        if (_controllerProcess is { HasExited: false } process)
        {
            try { process.StandardInput.Close(); }
            catch (Exception error) when (error is IOException or InvalidOperationException) { failures.Add(error); }
        }
        if (failures.Count > 0) throw new IOException("Controller stop request failed: " + string.Join("; ", failures.Select(error => error.Message)));
    }

    private void RequestTrackingStop()
    {
        var failures = new List<Exception>();
        try { File.WriteAllText(_stopFile, DateTimeOffset.Now.ToString("O")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures.Add(error); }
        try { RequestControllerStop(); }
        catch (IOException error) { failures.Add(error); }
        if (failures.Count > 0) throw new IOException("A tracking stop signal could not be written: " + string.Join("; ", failures.Select(error => error.Message)));
    }

    private async Task FinishControllerRestorationAsync()
    {
        if (_controllerRestoration is null || _controllerProcess is null) return;
        HubControllerFeedback restoration;
        try { restoration = await _controllerRestoration.WaitAsync(TimeSpan.FromSeconds(6)); }
        catch (TimeoutException)
        {
            restoration = new(ControllerFeedbackState.NeedsAttention, "Controller restoration is unconfirmed",
                "The controller worker exited, but its final restoration result did not finish within six seconds.",
                "Keep controller input off and copy diagnostics. Stop other tracking, close SteamVR, restart the headset and reopen the Hub before checking controller compatibility.", true);
        }
        CompleteControllerInputExit(_controllerProcess, restoration, stopWasRequested: true);
    }

    private void CompleteControllerInputExit(Process process, HubControllerFeedback restoration, bool stopWasRequested)
    {
        _controllerInputStopped = true;
        _handsReady = _touchpadReady = false;
        _controllerCleanupFailed |= restoration.IsError;
        if (_controllerCleanupFailed) ShowControllerFeedback(restoration with
        {
            State = ControllerFeedbackState.NeedsAttention,
            Title = "Controller restoration needs attention",
            Detail = restoration.IsError ? restoration.Detail : "A controller cleanup failure was recorded earlier in this Hub session. The earlier restoration remains unconfirmed.",
            NextStep = "Keep controller input off and copy diagnostics. Stop other tracking, close SteamVR, restart the headset and reopen the Hub before checking controller compatibility.",
            IsError = true,
        });
        else if (!stopWasRequested)
        {
            var otherTracking = _trackingProcesses.Any(worker => worker != process && !worker.HasExited);
            ShowControllerFeedback(new(ControllerFeedbackState.Restored, "Optional controller input stopped",
                "The controller workers stopped and their recorded changes were restored. " +
                    (otherTracking ? "Other Qpro tracking continues." : "No other Qpro worker is running."),
                "Check the controller error in Activity. Stop the session before retrying optional controller input.", true));
            if (!otherTracking)
            {
                _runStatus.Text = "● Controller input stopped — check Activity";
                _runStatus.ForeColor = Warning;
            }
        }
        else ShowControllerFeedback(restoration);
        if (!stopWasRequested && _trackingProcesses.Any(worker => worker != process && !worker.HasExited))
            UpdateSessionReadiness();
        UpdateControlState();
    }


    private void ObserveControllerInput(string label, string line)
    {
        if (label is not ("Hand/controller input" or "Hand/controller compatibility") || IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => ObserveControllerInput(label, line)); }
            catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
            {
                // The form can close between the process callback and UI dispatch.
            }
            return;
        }
        bool inputReadyLine = line.StartsWith("HANDS_READY ", StringComparison.Ordinal) || line.StartsWith("TOUCHPAD_READY ", StringComparison.Ordinal);
        if (inputReadyLine && (_stopping || _controllerInputStopped || ControllerCleanupUnconfirmed)) return;
        if (line.StartsWith(HubControllerFeedback.CheckPrefix, StringComparison.Ordinal))
        {
            if (HubControllerFeedback.TryParseCheckLine(line, out var feedback))
                ShowControllerFeedback(feedback!);
            else
                ShowControllerFeedback(new(ControllerFeedbackState.NeedsAttention,
                    "Hand/controller compatibility could not be verified",
                    "The compatibility report was incomplete or malformed. No successful result can be inferred.",
                    "Check the detailed Activity output and copy the complete report before trying an unverified runtime.", true));
        }
        else if (line.StartsWith("HANDS_READY ", StringComparison.Ordinal))
        {
            _handsReady = true;
            _handsStatus.Text = "Valid optical fingers are routed alongside the physical controllers.";
            _handsStatus.ForeColor = Good;
        }
        else if (line.StartsWith("TOUCHPAD_READY ", StringComparison.Ordinal))
        {
            _touchpadReady = true;
            _handsStatus.Text = "Valid thumb-rest packets are being forwarded to SteamVR. Confirm the controller add-on loaded and check your app's input binding.";
        }
        else if (line.StartsWith(HubControllerFeedback.CleanupPrefix, StringComparison.Ordinal))
        {
            _handsReady = _touchpadReady = false;
            var cleanup = HubControllerFeedback.ParseCleanupLine(line);
            // Keep a failed or malformed restoration visible for this session;
            // a later progress line cannot erase that evidence.
            if (!_controllerCleanupFailed || cleanup.State == ControllerFeedbackState.NeedsAttention)
                ShowControllerFeedback(cleanup);
            _controllerCleanupFailed |= cleanup.State == ControllerFeedbackState.NeedsAttention;
        }
        if (inputReadyLine && (!_hybridHands.Checked || _handsReady) && (!_controllerTouchpad.Checked || _touchpadReady) && !_stopping)
            UpdateSessionReadiness();
    }

    private void ShowControllerFeedback(HubControllerFeedback feedback)
    {
        if (ControllerCleanupUnconfirmed && feedback.State != ControllerFeedbackState.NeedsAttention)
            feedback = new(ControllerFeedbackState.NeedsAttention, "Controller restoration needs attention",
                "A controller cleanup failure was recorded earlier in this Hub session. A compatibility check or PC component change cannot confirm headset restoration.",
                "Keep controller input off and copy diagnostics. Stop other tracking, close SteamVR, restart the headset and reopen the Hub before checking controller compatibility.", true);
        _controllerFeedbackText = feedback.Title + ". " + feedback.NextStep;
        _controllerFeedbackWarning = feedback.IsError || feedback.State == ControllerFeedbackState.Waiting;
        _handsStatus.Text = _controllerFeedbackText;
        _handsStatus.ForeColor = _controllerFeedbackWarning ? Warning : Muted;
        SetActionFeedback(feedback.Title, feedback.Detail, feedback.NextStep, feedback.IsError);
    }
}
