using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO.Compression;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private string? _lastModuleInspectionDetail;
    private string? _lastUsbFailureDetail;

    private ProcessStartInfo PowerShellStart(string script, IEnumerable<string> args, bool hidden)
        => _scripts.Create(script, args, hidden);

    private async Task RefreshStatusAsync()
    {
        if (_previewOnly || _closingInProgress || _statusRefreshBusy || IsDisposed || Disposing) return;
        _statusRefreshBusy = true;
        try
        {
            // Status checks select the remembered transport without resetting it.
            // Reconnect is an explicit setup action, so it cannot race tracking startup.
            var quest = await HasQuestAsync();
            if (_closingInProgress || IsDisposed || Disposing) return;
            var steam = Process.GetProcessesByName("vrserver").Any();
            var vrcft = Process.GetProcessesByName("VRCFaceTracking").Any();
            SetStatus(_usbStatus, quest ? StatusKind.Good : StatusKind.Bad,
                quest ? (_environment.WirelessSelected ? "Wi-Fi ADB connected" : "USB ADB connected")
                    : (_environment.WirelessSelected ? "Wi-Fi not connected" : "USB not connected"));
            if (!_environment.WirelessSelected)
            {
                _connectionModeNote.Text = _environment.UsbConnectionMessage;
                _connectionModeNote.ForeColor = quest ? Muted : Warning;
                _usbStatus.AccessibleDescription = _environment.UsbConnectionMessage;
                var usbFailure = _environment.UsbFailureDetail;
                if (usbFailure is not null && usbFailure != _lastUsbFailureDetail)
                    AppendLog("[USB connection check] " + usbFailure);
                _lastUsbFailureDetail = usbFailure;
            }
            SetStatus(_steamStatus, steam ? StatusKind.Good : StatusKind.Bad, steam ? "Running" : "Not running");
            SetStatus(_vrcftStatus, vrcft ? StatusKind.Good : StatusKind.Bad, vrcft ? "Running" : "Not running");
            var module = _environment.InspectModuleInstallation();
            var moduleNeedsRestart = module.IsCurrent && vrcft && _environment.TrackingSourceRequiresVrcftRestart();
            var moduleDetail = moduleNeedsRestart
                ? module.Detail + " Restart VRCFaceTracking to load the selected source; its process is still running from before the source changed."
                : module.Detail;
            SetStatus(_bridgeStatus, module.IsCurrent && !moduleNeedsRestart ? StatusKind.Good : StatusKind.Warning,
                moduleNeedsRestart ? "Installed · restart VRCFT" : module.Status);
            _bridgeStatus.AccessibleDescription = moduleDetail;
            // Report only transitions so a failed card/hash check names its
            // cause without filling Activity on every status refresh.
            if (_lastModuleInspectionDetail != moduleDetail)
            {
                _lastModuleInspectionDetail = moduleDetail;
                AppendLog("[Qpro module check] " + moduleDetail);
            }
            SetStatus(_runtimeStatus, BackendReady() ? StatusKind.Good : StatusKind.Warning, BackendReady() ? "Ready" : "Setup needed");
            SetStatus(_gazeStatus, _gaze.Checked ? StatusKind.Warning : StatusKind.Good,
                !_gaze.Checked ? "Hub gaze off · optional" : EyeModelReady() ? "Prepared · validated at start" : "Hub gaze needs preparation");
            UpdateSetupStepStyles();
            UpdateControlState();
        }
        catch (Exception error) { AppendLog("Status refresh could not complete: " + error.Message); }
        finally { _statusRefreshBusy = false; }
    }

    private void UpdateSetupStepStyles()
    {
        if (_previewOnly) return;
        var ready = new[] { BackendReady(), CurrentBridgeInstalled() };
        var next = Array.FindIndex(ready, value => !value);
        StyleSetupStep(_setupRuntimeButton, _setupRuntimeStatus, "Install runtime", ready[0], next == 0);
        StyleSetupButton(_setupBridgeButton, "Install Virtual Desktop module",
            ready[1] && !_environment.SteamLinkSelected, next == 1 && !_environment.SteamLinkSelected);
        StyleSetupButton(_setupSteamLinkModuleButton, "Install Steam Link module",
            ready[1] && _environment.SteamLinkSelected, next == 1 && _environment.SteamLinkSelected);
        _setupBridgeStatus.Text = ready[1] ? "● Complete" : next == 1 ? "● Next step" : "○ Waiting";
        _setupBridgeStatus.ForeColor = ready[1] ? Good : next == 1 ? Warning : Muted;
        _uninstallBridgeButton.Enabled = !_setupActionRunning && BridgeUninstallAvailable();
        StyleSetupStep(_setupGazeButton, _setupGazeStatus, "Prepare gaze", EyeModelReady(), false);
        _setupGazeStatus.Text = !_gaze.Checked ? "Optional · skip for Magisk gaze" :
            EyeModelReady() ? "Prepared · validated at start" : "Prepare only for Hub gaze";
        _setupGazeStatus.ForeColor = !_gaze.Checked ? Muted : EyeModelReady() ? Good : Warning;
        var installedVersion = LatestRocmInstalledVersion();
        var latestReady = installedVersion is not null;
        var offeredVersionReady = installedVersion?.StartsWith(HubRocmRuntime.InstallVersion + ".", StringComparison.Ordinal) == true;
        var legacyReady = LegacyRocmInstalled();
        var latestEnvironmentExists = LatestRocmEnvironmentExists();
        _setupAmdStatus.Text = _rocmInstallRunning ? $"◌ Installing and verifying ROCm {HubRocmRuntime.InstallVersion}…" :
            latestReady ? $"● ROCm {installedVersion} verified; " +
                (offeredVersionReady ? "GPU tests passed" : $"ROCm {HubRocmRuntime.InstallVersion} update available") +
                (legacyReady ? "; ROCm 7.2.1 fallback ready" : "") :
            legacyReady ? $"● ROCm 7.2.1 fallback ready; ROCm {HubRocmRuntime.InstallVersion} available" :
            latestEnvironmentExists ? $"○ Unverified ROCm environment found; install or repair {HubRocmRuntime.InstallVersion}" :
            AmdInstallEligible ? $"○ ROCm {HubRocmRuntime.InstallVersion} available after PC runtime" : "○ Eligible discrete Radeon required";
        _setupAmdStatus.ForeColor = _rocmInstallRunning || latestEnvironmentExists && !latestReady
            ? Warning : latestReady || legacyReady ? Good : AmdInstallEligible ? Warning : Muted;
        _setupAmdButton.Text = offeredVersionReady ? $"Repair ROCm {HubRocmRuntime.InstallVersion}" :
            latestReady || legacyReady ? $"Upgrade to ROCm {HubRocmRuntime.InstallVersion}" :
            latestEnvironmentExists ? $"Verify / repair ROCm {HubRocmRuntime.InstallVersion}" : $"Install ROCm {HubRocmRuntime.InstallVersion}";
        _setupAmdButton.OutlineColor = offeredVersionReady ? Good : AmdInstallEligible && _setupPulseOn ? Accent : Border;
        _setupAmdButton.OutlineWidth = offeredVersionReady || AmdInstallEligible && _setupPulseOn ? 2 : 1;
        SetSetupButtonsEnabled(true);
    }

    private void StyleSetupStep(DarkButton button, Label status, string label, bool complete, bool attention)
    {
        StyleSetupButton(button, label, complete, attention);
        status.Text = complete ? "● Complete" : attention ? "● Next step" : "○ Waiting";
        status.ForeColor = complete ? Good : attention ? Warning : Muted;
    }

    private void StyleSetupButton(DarkButton button, string label, bool complete, bool attention)
    {
        button.Text = complete ? "✓  " + label : label;
        button.OutlineColor = complete ? Good : attention && _setupPulseOn ? Accent : Border;
        button.OutlineWidth = complete || attention && _setupPulseOn ? 2 : 1;
        button.Invalidate();
    }

    private void UpdateModuleInstallButtonState(bool enabled)
    {
        _setupBridgeButton.Enabled = enabled && !_environment.SteamLinkSelected;
        _setupSteamLinkModuleButton.Enabled = enabled && _environment.SteamLinkSelected;
    }

    private void PlaySfx(string fileName)
    {
        var path = Path.Combine(_root, "SFX", fileName);
        if (!File.Exists(path)) return;
        try
        {
            _soundPlayer?.Stop();
            _soundPlayer?.Dispose();
            _soundPlayer = new SoundPlayer(path);
            _soundPlayer.Play();
        }
        catch (Exception error)
        {
            AppendLog($"Sound could not play: {error.Message}");
        }
    }

    private string? GetConfiguredAdbTarget() => _environment.GetConfiguredAdbTarget();
    private Task<bool> HasQuestAsync() => _environment.HasQuestAsync();
    private Task<(bool Completed, int ExitCode, string Output)> RunAdbProbeAsync(string adb, IEnumerable<string> arguments, int timeoutSeconds = 4)
        => _environment.RunAdbProbeAsync(adb, arguments, timeoutSeconds);
    private string? FindAdb() => _environment.FindAdb();
    private bool BridgeInstalled() => _environment.BridgeInstalled();
    private static bool VrcftModuleProcessRunning() =>
        new[] { "VRCFaceTracking", "VRCFaceTracking.ModuleProcess", "ModuleProcess" }
            .Any(name => Process.GetProcessesByName(name).Length > 0);
    private bool BridgeUninstallAvailable()
    {
        return BridgeInstalled() || HubModuleInstallation.HasSavedBackups(Path.Combine(_root, "research"));
    }
    private bool CurrentBridgeInstalled() => _environment.CurrentBridgeInstalled();
    private bool BackendReady() => !_previewOnly && _environment.BackendReady();
    private bool EyeModelReady() => _environment.EyeModelReady();
    private string VisibilityModeValue() => _visibilityMode.SelectedIndex switch { 1 => "camera", 2 => "native", 3 => "agreement", _ => "weighted" };
    private string PupilSensitivityValue() => ((10 + 2 * Math.Max(0, _pupilSensitivity.SelectedIndex)) / 10.0)
        .ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    private string? FindPythonRuntime() => _previewOnly ? null : _environment.FindPythonRuntime();

    private void UpdateControlState()
    {
        var running = _trackingCleanupPending || LiveTrackingRunning;
        // These values are passed once to the child process. Keep the controls
        // locked until Stop, rather than implying that a live model was reloaded.
        var sessionEditable = !running && !_stopping && !_starting;
        _gaze.Enabled = sessionEditable;
        _tongue.Enabled = sessionEditable;
        _pupil.Enabled = sessionEditable;
        _eyeProfiles.Enabled = _gaze.Checked && sessionEditable;
        // Model selection precedes the opt-in output switches. It must remain
        // usable when all camera features are off on a fresh Hub launch.
        _tongueModels.Enabled = sessionEditable;
        _fps.Enabled = (_tongue.Checked || _cameraCheekPuff.Checked || _pupil.Checked) && sessionEditable;
        _pupilSensitivity.Enabled = _pupil.Checked && sessionEditable;
        _cameraPreview.Enabled = sessionEditable;
        _connectionMode.Enabled = !_setupActionRunning && !_utilityActionRunning &&
            !_datasetOperationBusy && !running && !_stopping && !_starting;
        _reconnectUsbButton.Enabled = _connectionMode.Enabled && !_closingInProgress && !_compatibilityChecking && !_gazeRecoveryRunning;
        _forgetUsbButton.Enabled = _reconnectUsbButton.Enabled && _environment.UsbHeadsetRemembered;
        _trackingSourceSetup.Enabled = !_setupActionRunning && !_utilityActionRunning &&
            !_datasetOperationBusy && !running && !_stopping && !_starting;
        _trackingSourceLive.Enabled = _trackingSourceSetup.Enabled;
        _smoothing.Enabled = _tongue.Checked && sessionEditable;
        _visibilityMode.Enabled = _tongue.Checked && sessionEditable;
        _recoverGazeButton.Enabled = sessionEditable && !_setupActionRunning && !_utilityActionRunning && !_datasetOperationBusy && !_gazeRecoveryRunning;
        _inspectGazeButton.Enabled = _recoverGazeButton.Enabled;
        _resetLegacyGazeButton.Enabled = _recoverGazeButton.Enabled;
        var canStart = !running && !_stopping && !_starting && !_setupActionRunning &&
            !_utilityActionRunning && !_datasetOperationBusy;
        if (_checkCompatibility is not null) _checkCompatibility.Enabled = canStart && !_compatibilityChecking;
        _cheekCameraBaseModels.Enabled = canStart;
        _cheekCameraDatasets.Enabled = canStart;
        _recordCameraCheeks.Enabled = canStart;
        _trainCameraCheeks.Enabled = canStart && _cheekCameraBaseModels.SelectedItem is FileChoice &&
            _cheekCameraDatasets.SelectedItem is DatasetChoice;
        _start.Enabled = canStart;
        _stop.Enabled = (running || _starting) && !_stopping;
        StyleRunButton(_start, canStart);
        StyleRunButton(_stop, (running || _starting) && !_stopping);
        UpdateCameraCheekAvailability();
        UpdateControllerInputAvailability();
        SetSetupButtonsEnabled(true);
    }

    private bool TrackingShutdownPending => _starting || _stopping || _trackingCleanupPending || LiveTrackingRunning;

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        e.Cancel = true;
        if (_closingInProgress || UtilityActionIsBusy(allowTrackingTransition: true)) return;
        _closingInProgress = true;
        Enabled = false;
        try
        {
            try { SaveLiveOptions(); }
            catch (Exception error) { AppendLog("Could not save Live tracking choices: " + error.Message); }
            await _environment.SuspendAdbProbesAsync();
            if (_starting || LiveTrackingRunning)
                await StopTrackingAsync();
            // Startup may still be returning from its canceled ADB check.
            // Do not stop ADB until the tracking scripts have restored headset overrides.
            for (var attempt = 0; attempt < 200 && _starting; attempt++)
                await Task.Delay(50);
            if (TrackingShutdownPending)
            {
                AppendLog("Hub shutdown is waiting for tracking cleanup. Stop tracking, then close the Hub again.");
                return;
            }
            AppendLog("Stopping the ADB server before closing the Hub…");
            var result = await _environment.StopAdbServerAsync();
            AppendLog(result.Completed && result.ExitCode == 0 ? "ADB server stopped."
                : $"ADB server shutdown could not be confirmed: {result.Output}");
            FormClosing -= OnClosing;
            Close();
        }
        catch (Exception error) { AppendLog("Hub shutdown could not finish: " + error.Message); }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                _closingInProgress = false;
                _environment.ResumeAdbProbes();
                Enabled = true;
                UpdateControlState();
            }
        }
    }

    private void PostProcessUpdate(string label, Action update)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        void Apply()
        {
            if (IsDisposed || Disposing) return;
            try { update(); }
            catch (Exception error) { AppendLog($"[{label}] Progress display could not update: {error.Message}"); }
        }
        if (!InvokeRequired) { Apply(); return; }
        try { BeginInvoke(Apply); }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
        {
            // Closing can destroy the window between the checks and dispatch.
        }
    }

    private void AppendLog(string text)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => AppendLog(text)); }
            catch (InvalidOperationException) { /* The window closed before the log arrived. */ }
            return;
        }
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        try { _componentUpdateOutput?.Invoke(text); }
        catch (Exception)
        {
            AppendActivityText("Component update progress could not refresh. Its helper output is still shown here.",
                timestamp, ActivitySeverity.Warning);
        }
        AppendActivityText(text, timestamp);
    }

    private static int VersionFromPath(string path) => Regex.Match(Path.GetFileName(path), @"-v(\d+)").Success && int.TryParse(Regex.Match(Path.GetFileName(path), @"-v(\d+)").Groups[1].Value, out var v) ? v : 0;
    private static void SelectOrFirst(ComboBox box, string? previous)
    {
        for (var i = 0; i < box.Items.Count; i++) if ((box.Items[i] as FileChoice)?.Primary == previous) { box.SelectedIndex = i; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }
    private static void SelectOrFirst(ListBox box, string? previous)
    {
        for (var i = 0; i < box.Items.Count; i++) if ((box.Items[i] as FileChoice)?.Primary == previous) { box.SelectedIndex = i; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }
    private enum StatusKind { Good, Warning, Bad }
}
