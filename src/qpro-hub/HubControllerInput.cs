using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private readonly CheckBox _hybridHands = FeatureToggle("Experimental hands + controllers", false);
    private readonly CheckBox _controllerTouchpad = FeatureToggle("Experimental Touch Pro thumb-rest input", false);
    private readonly ComboBox _touchpadMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _touchpadSide = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly DarkSlider _swipeSensitivity = new() { Minimum = 25, Maximum = 200, SmallChange = 25, Value = 100, Height = 32, Dock = DockStyle.Fill };
    private readonly Label _swipeSensitivityValue = new() { AutoSize = true, Text = "100%", Anchor = AnchorStyles.Right, TextAlign = ContentAlignment.MiddleRight };
    private const int DefaultSwipeSensitivityPercent = 100;
    private const decimal SwipeGainAt100Percent = 0.22m;
    private readonly Label _controllerModeHint = Info("");
    private readonly DarkButton _controllerBindingGuide = SecondaryButton("SteamVR binding instructions");
    private readonly Label _handsStatus = new() { AutoSize = true, ForeColor = Warning,
        Font = new Font(UiFontName, 10F, FontStyle.Bold), Tag = "responsive-info" };
    private readonly Label _handsStatusDetail = Info("");
    private readonly Label _handsStatusNext = Info("");
    private readonly DarkButton _installHandsButton = SetupButton("Install hand/controller components");
    private readonly DarkButton _removeHandsButton = SetupButton("Uninstall controller add-on");
    private readonly DarkButton _checkHandsButton = SecondaryButton("Check hand/controller compatibility");
    private const string ControllerBindingInstructions =
        "1. Close SteamVR, then use First-time setup > Install hand/controller components. Reopen SteamVR through Virtual Desktop.\n\n" +
        "2. For SteamVR dashboard and VRChat menu scrolling, choose Swipe and press Start tracking. The updated add-on prepares bindings automatically where supported. Existing custom bindings are kept; select the prepared Qpro binding manually if needed. If your binding layout is not recognized, leave it unchanged and follow the manual steps in Activity.\n\n" +
        "3. For other game actions, open SteamVR Settings > Controllers > Manage Controller Bindings. Select your game and edit a custom binding. Assign the extra Qpro thumb-rest trackpad's position, touch, force or click to the actions you want.\n\n" +
        "Qpro does not replace your game's bindings. Normal movement, buttons, gestures, haptics and finger skeleton remain available on their original inputs.\n\n" +
        "Menu scroll input is active only in Swipe while tracking runs. Desktop mouse mode controls the Windows pointer instead of sending extra SteamVR input. Stop tracking releases the extra inputs.";
    private bool _handsReady;
    private bool _touchpadReady;
    private HubControllerFeedback? _controllerFeedback;
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

    private static Control HandsPrerequisites(bool includeAppSettings)
    {
        var panel = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            BackColor = Inset,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = "Hands + controllers prerequisites",
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = SectionCaption("TURN ON BEFORE USING HANDS + CONTROLLERS");
        heading.Tag = "responsive-info";
        heading.Margin = new Padding(0, 0, 0, 8);
        panel.Controls.Add(heading, 0, 0);
        panel.SetColumnSpan(heading, 2);

        void Requirement(int row, string source, string settings)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = FieldLabel(source);
            label.TextAlign = ContentAlignment.TopLeft;
            label.Font = new Font(UiFontName, 9F, FontStyle.Bold);
            label.Margin = new Padding(0, 0, 14, 8);
            var detail = Info(settings);
            detail.UseMnemonic = false;
            detail.Margin = new Padding(0, 0, 0, 8);
            panel.Controls.Add(label, 0, row);
            panel.Controls.Add(detail, 1, row);
        }

        // A menu prerequisite is not a successful hardware check. Keep the
        // instructions visible independently of compatibility feedback.
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Requirement(1, "Quest", "Hands & eyes: Hand tracking On; Automatically switch between controllers and hands On.");
        Requirement(2, "Singularity", "AIO Tweaks: Simultaneous Hands & Controllers On.");
        if (includeAppSettings)
        {
            Requirement(3, "Virtual Desktop", "Streaming: Forward tracking data to PC On; Track controllers On.");
            Requirement(4, "VRChat", "Main Menu > Controls: Avatars Use Finger Tracking On.");
        }
        return panel;
    }

    private Control ControllerCompatibilitySummary()
    {
        var panel = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, BackColor = Inset, Padding = new Padding(12),
            Margin = new Padding(0, 4, 0, 8), AccessibleName = "Hand/controller check result",
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var label in new[] { _handsStatus, _handsStatusDetail, _handsStatusNext })
        {
            label.Margin = new Padding(0, 0, 0, 6);
            label.UseMnemonic = false;
            panel.Controls.Add(label);
        }
        _handsStatusNext.ForeColor = Accent;
        return panel;
    }

    private void PresentControllerStatus(string title, string detail, string nextStep, Color color)
    {
        _handsStatus.Text = title;
        _handsStatus.ForeColor = color;
        _handsStatusDetail.Text = detail;
        _handsStatusDetail.Visible = detail.Length != 0;
        _handsStatusNext.Text = "Next: " + nextStep;
        _handsStatusNext.Visible = nextStep.Length != 0;
    }

    private void InitializeControllerInputUi()
    {
        _touchpadMode.Items.AddRange(["Trackpad", "Relative joystick", "Swipe", "Desktop mouse"]);
        _touchpadMode.SelectedIndex = 0;
        ConfigureDropDown(_touchpadMode);
        _touchpadSide.Items.AddRange(["Both", "Left (L)", "Right (R)"]);
        _touchpadSide.SelectedIndex = 0;
        ConfigureDropDown(_touchpadSide);
        RestoreSwipeSensitivity(null);
        _swipeSensitivity.ValueChanged += (_, _) => _swipeSensitivityValue.Text = _swipeSensitivity.Value + "%";
        _swipeSensitivity.AccessibleName = "Swipe sensitivity";
        _swipeSensitivity.AccessibleDescription = "25% to 200% in 25% steps. Use the arrow keys to adjust. Lower is steadier; higher scrolls faster. Stop tracking, adjust, then Start tracking.";
        _swipeSensitivityValue.AccessibleName = "Swipe sensitivity percentage";
        _touchpadMode.SelectedIndexChanged += (_, _) => UpdateControllerModeHint();
        _controllerBindingGuide.Enabled = true;
        _controllerBindingGuide.Click += (_, _) => MessageBox.Show(this,
            ControllerBindingInstructions,
            "Assign the extra thumb-rest inputs", MessageBoxButtons.OK, MessageBoxIcon.Information);
        UpdateControllerModeHint();
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
        _checkHandsButton.Click += async (_, _) => await CheckControllerCompatibilityAsync();
        UpdateControllerInputAvailability();
    }

    private async Task CheckControllerCompatibilityAsync()
    {
        _controllerFeedback = null;
        PresentControllerStatus("Checking hand/controller compatibility…",
            "Reading the connected headset version, settings and installed components.",
            "Keep the headset awake and connected until the result appears.", Warning);
        var passed = await RunUtilityAsync("Hand/controller compatibility", "controller-input.ps1", "-Action", "check");
        // Typed reports already supply an actionable failure. Startup, pipe or
        // helper failures must also leave a final result here, rather than an
        // earlier green check or an indefinite checking message.
        if (!passed && !IsDisposed && !Disposing && _controllerFeedback?.IsError != true)
            ShowControllerFeedback(new(ControllerFeedbackState.NeedsAttention,
                "Hand/controller compatibility could not be verified", _actionDetail.Text,
                _actionNext.Text.StartsWith("Next: ", StringComparison.Ordinal) ? _actionNext.Text[6..] : _actionNext.Text, true));
    }

    private string TouchpadModeValue() => _touchpadMode.SelectedIndex switch
    {
        1 => "joystick", 2 => "swipe", 3 => "mouse", _ => "trackpad",
    };

    private string TouchpadSideValue() => _touchpadSide.SelectedIndex switch
    {
        1 => "left", 2 => "right", _ => "both",
    };

    private static bool ValidSwipeSensitivity(int percent) => percent is >= 25 and <= 200 && percent % 25 == 0;

    private int SwipeSensitivityPercentValue() => _swipeSensitivity.Value;

    private decimal SwipeGainValue() => SwipeGainAt100Percent * SwipeSensitivityPercentValue() / 100m;

    private void RestoreSwipeSensitivity(int? percent) =>
        _swipeSensitivity.Value = percent is { } value && ValidSwipeSensitivity(value)
            ? value : DefaultSwipeSensitivityPercent;

    private void UpdateControllerModeHint()
    {
        _controllerModeHint.Text = TouchpadModeValue() switch
        {
            "joystick" => "Relative joystick: the first touch sets the center. Slide from that point to steer the extra input; lifting your thumb returns it to center. Assign its axes in your app's SteamVR binding.",
            "swipe" => "Swipe: the updated add-on prepares SteamVR dashboard and VRChat menu scrolling automatically where supported. Custom bindings are kept; follow Activity's manual steps if setup needs attention. Normal movement, buttons, gestures and other game bindings stay unchanged.",
            "mouse" => "Desktop mouse: slide your thumb to move the Windows pointer and press the thumb rest to left-click. This mode sends no extra trackpad actions to SteamVR. Stop tracking releases the mouse input.",
            _ => "Trackpad: your thumb's position controls the extra trackpad axes. Touch, pressure and click are separate inputs you can assign in SteamVR. Lifting your thumb releases this extra input.",
        };
        _swipeSensitivity.Enabled = _touchpadMode.Enabled && TouchpadModeValue() == "swipe";
    }

    private void UpdateControllerInputAvailability() => UpdateControllerInputAvailabilityFromSnapshot(null);

    private void UpdateControllerInputAvailabilityFromSnapshot(bool? runtimeReady)
    {
        bool idle = !_closingInProgress && !_trackingCleanupPending && !_gazeRecoveryRunning &&
            !_starting && !_stopping && !_utilityActionRunning && !_setupActionRunning &&
            !_datasetOperationBusy && !LiveTrackingRunning;
        bool supportedSource = !_environment.SteamLinkSelected;
        _hybridHands.Enabled = _controllerTouchpad.Enabled = idle && supportedSource;
        _touchpadMode.Enabled = idle && supportedSource && _controllerTouchpad.Checked;
        _touchpadSide.Enabled = _touchpadMode.Enabled;
        _swipeSensitivity.Enabled = _touchpadMode.Enabled && TouchpadModeValue() == "swipe";
        _installHandsButton.Enabled = idle && supportedSource;
        _removeHandsButton.Enabled = idle && Directory.Exists(ControllerAddonRoot);
        _checkHandsButton.Enabled = idle && supportedSource && (runtimeReady ?? BackendReady());
        if (idle)
        {
            if (!supportedSource) _hybridHands.Checked = _controllerTouchpad.Checked = false;
            if (ControllerCleanupUnconfirmed)
            {
                PresentControllerStatus("Controller restoration needs attention",
                    "The previous controller session has not confirmed restoration.",
                    "Keep controller input off. Close SteamVR, restart the headset and reopen the Hub before checking compatibility.", Bad);
            }
            else if (!supportedSource)
            {
                PresentControllerStatus("Steam Link controller support is unverified",
                    "These experimental inputs currently require Virtual Desktop.",
                    "Use Virtual Desktop to check hand/controller compatibility.", Warning);
            }
            else
            {
                if (_controllerFeedback is { } feedback) PresentControllerFeedback(feedback);
                else PresentControllerStatus("Compatibility not checked",
                    "No hand/controller check has run in this Hub session.",
                    "Turn on the required settings above, then check compatibility.", Warning);
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
        arguments.AddRange(["-Side", TouchpadSideValue()]);
        if (_controllerTouchpad.Checked && TouchpadModeValue() == "swipe")
            arguments.AddRange(["-SwipeGain", SwipeGainValue().ToString(CultureInfo.InvariantCulture)]);
        return arguments.ToArray();
    }

    private void StartControllerInput()
    {
        if (!_hybridHands.Checked && !_controllerTouchpad.Checked) return;
        PresentControllerStatus("Checking controller input…",
            "Startup checks are running. Live optical fingers have not arrived yet.",
            "Keep the headset connected and your fingers in view.", Warning);
        _controllerFeedback = null;
        _controllerInputStopped = false;
        _handsReady = _touchpadReady = false;
        if (_controllerTouchpad.Checked && TouchpadModeValue() == "mouse")
            AppendLog("Desktop mouse mode selected: thumb-rest gestures can move the Windows pointer. Stop tracking disables this input.");
        if (_controllerTouchpad.Checked)
            AppendLog($"Thumb-rest mode: {_touchpadMode.SelectedItem}; controllers: {_touchpadSide.SelectedItem}. Normal controller inputs and optical fingers are unchanged by this selection.");
        if (_controllerTouchpad.Checked && TouchpadModeValue() == "swipe")
            AppendLog($"Swipe sensitivity: {SwipeSensitivityPercentValue()}% (gain {SwipeGainValue().ToString(CultureInfo.InvariantCulture)}). Lower is steadier; higher scrolls faster. Stop tracking before adjusting, then start again.");
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
        ShowControllerFeedback(new(ControllerFeedbackState.NeedsAttention,
            "Optional controller input could not start", error.Message,
            otherTracking ? "Other Qpro tracking continues. Check Activity and stop the session before retrying controller input."
                : "Check Activity before retrying optional controller input.", true));
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
        bool liveHandLine = line.StartsWith("HANDS_STATUS ", StringComparison.Ordinal);
        if ((inputReadyLine || liveHandLine) && (_stopping || _controllerInputStopped || ControllerCleanupUnconfirmed)) return;
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
            PresentControllerStatus("Live optical fingers received",
                "Valid optical fingers are routed alongside the physical controllers.",
                "Confirm real finger movement in your game while holding the controllers.", Good);
        }
        else if (line.StartsWith("TOUCHPAD_READY ", StringComparison.Ordinal))
        {
            _touchpadReady = true;
            if (TouchpadModeValue() == "mouse")
                PresentControllerStatus("Desktop mouse input received",
                    "Valid thumb-rest packets are being read for Windows pointer control.",
                    "Confirm the pointer moves and the thumb-rest press clicks. Stop tracking releases the mouse input.", Good);
            else
                PresentControllerStatus("Live thumb-rest input received",
                    "Valid thumb-rest packets are being forwarded to SteamVR.",
                    "Confirm the controller add-on loaded and assign the extra input in your app's SteamVR binding.", Good);
        }
        else if (liveHandLine)
        {
            if (HubHandLiveStatus.TryParse(line, out var handStatus))
            {
                _handsReady = handStatus!.Ready;
                PresentControllerStatus(handStatus.Description,
                    "This status reports fresh optical input; it does not measure game performance.",
                    handStatus.Ready ? "Confirm the fingers move in your game." : "Keep your fingers in view; check Activity if they stay unavailable.",
                    handStatus.Ready ? Good : Warning);
            }
            else
            {
                _handsReady = false;
                PresentControllerStatus("Live optical input is unverified",
                    "The latest optical finger report could not be read.",
                    "Wait for a valid report; check Activity if this continues.", Warning);
            }
            UpdateSessionReadiness();
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
        _controllerFeedback = feedback;
        PresentControllerFeedback(feedback);
        SetActionFeedback(feedback.Title, feedback.Detail, feedback.NextStep, feedback.IsError);
    }

    private void PresentControllerFeedback(HubControllerFeedback feedback)
    {
        // Green prechecks confirm the static report only. The report's detail
        // still says that live optical input and game performance are unverified.
        var color = feedback.IsError ? Bad : feedback.State == ControllerFeedbackState.Waiting ? Warning : Good;
        var next = feedback.State == ControllerFeedbackState.Checked && !feedback.IsError
            ? "Choose your optional inputs and press Start tracking; confirm real finger movement and any thumb-rest bindings in your game."
            : feedback.NextStep;
        PresentControllerStatus(feedback.Title, feedback.Detail, next, color);
    }
}

internal sealed record HubHandLiveStatus(bool Ready, bool Left, bool Right)
{
    internal string Description => $"Optical fingers: left {(Left ? "active" : "waiting")}; right {(Right ? "active" : "waiting")}. " +
        (Ready ? "Keep your fingers in view while holding the controllers."
            : "Waiting for fresh optical fingers. Controller movement and buttons remain available.");

    internal static bool TryParse(string line, out HubHandLiveStatus? status)
    {
        status = null;
        const string prefix = "HANDS_STATUS ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || line.Length > 65536) return false;
        try
        {
            using var document = JsonDocument.Parse(line[prefix.Length..], new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            RequireUniqueFields(root);
            bool ready = ReportJson.Boolean(root.GetProperty("ready"));
            var adapters = root.GetProperty("adapters");
            if (adapters.ValueKind != JsonValueKind.Array || adapters.GetArrayLength() != 3) return false;
            foreach (var adapter in adapters.EnumerateArray()) RequireUniqueFields(adapter);
            if (!adapters[0].GetProperty("queries").TryGetInt64(out var queries) || queries < 0) return false;
            var fresh = adapters[1].GetProperty("freshSides");
            var sides = adapters[2].GetProperty("activeSides");
            if (sides.ValueKind != JsonValueKind.Array || sides.GetArrayLength() != 2 ||
                fresh.ValueKind != JsonValueKind.Array || fresh.GetArrayLength() != 2) return false;
            bool leftFresh = ReportJson.Boolean(fresh[0]), rightFresh = ReportJson.Boolean(fresh[1]);
            bool leftRouted = ReportJson.Boolean(sides[0]), rightRouted = ReportJson.Boolean(sides[1]);
            bool left = queries > 0 && leftFresh && leftRouted;
            bool right = queries > 0 && rightFresh && rightRouted;
            if (ready != (left || right)) return false;
            status = new(ready, left, right);
            return true;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { return false; }
    }

    private static void RequireUniqueFields(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Object required.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!names.Add(field.Name)) throw new FormatException("Duplicate field.");
    }
}
