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
    private string? _lastUsbFailureDetail;
    private string? _lastUsbStatusReason;
    private string? _lastWirelessStatusReason;
    private readonly Dictionary<DarkButton, (bool Complete, bool Attention)> _setupButtonStyles = [];
    private sealed record GazeStatusObservation(HubCompatibilityReport? Compatibility, HubGazeInspection? Inspection,
        string? Target, bool Wireless);
    private GazeStatusObservation? _lastGazeStatusObservation;
    internal sealed record HubStatusSnapshot(bool SteamRunning, bool RuntimeReady,
        string? RocmInstalledVersion, bool LegacyRocmReady, bool RocmEnvironmentExists);
    private Func<Task<HubStatusSnapshot>>? _previewReadStatusSnapshot;

    // Exercise the production refresh order in an isolated preview fixture.
    // The transport/USB probe itself remains the production implementation.
    internal void ConfigurePreviewStatusRefresh(Func<Task<HubStatusSnapshot>> readSnapshot)
    {
        if (!_previewOnly || _statusRefreshBusy)
            throw new InvalidOperationException("Status fixture dependencies require an idle preview Hub.");
        ArgumentNullException.ThrowIfNull(readSnapshot);
        _previewReadStatusSnapshot = readSnapshot;
    }

    private HubStatusSnapshot ReadStatusSnapshot(bool allowRuntimeReprobe)
    {
        var steam = HubProcessProbe.AnyRunning("vrserver");
        var rocm = HubRocmRuntime.ReadStatus(_root, _amdGpuLatestTarget, _amdGpuLegacyEligible);
        return new(steam, _environment.BackendReady(allowRuntimeReprobe),
            rocm.LatestReadyVersion, rocm.LegacyReady, rocm.LatestEnvironmentExists);
    }

    private ProcessStartInfo PowerShellStart(string script, IEnumerable<string> args, bool hidden)
        => _scripts.Create(script, args, hidden);

    private async Task RefreshStatusAsync()
    {
        if (_previewOnly && _previewReadStatusSnapshot is null || _closingInProgress ||
            _statusRefreshBusy || IsDisposed || Disposing) return;
        _statusRefreshBusy = true;
        try
        {
            // Status checks select the remembered transport without resetting it.
            // Reconnect is an explicit setup action, so it cannot race tracking startup.
            var source = _environment.SteamLinkSelected;
            var wireless = _environment.WirelessSelected;
            var target = wireless ? GetConfiguredAdbTarget() : null;
            bool ContextChanged() => source != _environment.SteamLinkSelected || wireless != _environment.WirelessSelected ||
                wireless && target != GetConfiguredAdbTarget();
            var quest = await HasQuestAsync();
            if (_closingInProgress || IsDisposed || Disposing || ContextChanged()) return;
            // Publish the fresh transport result ahead of unrelated checks.
            PublishConnectionStatus(quest, wireless, target);
            await DetectMagiskGazeOnceAsync(quest);
            if (_closingInProgress || IsDisposed || Disposing || ContextChanged()) return;
            // Capture process-list/transition state on the UI thread. The
            // snapshot worker must not enumerate the mutable tracking list.
            var allowRuntimeReprobe = !TrackingShutdownPending;
            var snapshot = await (_previewReadStatusSnapshot?.Invoke() ?? Task.Run(() => ReadStatusSnapshot(allowRuntimeReprobe)));
            if (_closingInProgress || IsDisposed || Disposing || ContextChanged()) return;
            using var readiness = BeginReadinessUpdate();
            var steamRequired = source || _hybridHands.Checked || _controllerTouchpad.Checked;
            SetStatus(_steamStatus, snapshot.SteamRunning ? StatusKind.Good : steamRequired ? StatusKind.Bad : StatusKind.Neutral,
                snapshot.SteamRunning ? "Running" : steamRequired ? "Not running" : "Optional");
            SetStatus(_runtimeStatus, snapshot.RuntimeReady ? StatusKind.Good : WorkerTrackingSelected ? StatusKind.Warning : StatusKind.Neutral,
                snapshot.RuntimeReady ? "Ready" : WorkerTrackingSelected ? "Setup needed" : "Optional");
            UpdateSetupStepStylesFromSnapshot(snapshot);
            UpdateControlStateFromSnapshot(snapshot);
        }
        catch (Exception error) { AppendLog("Status refresh could not complete: " + error.Message); }
        finally { _statusRefreshBusy = false; }
    }

    private void PublishConnectionStatus(bool connected, bool wireless, string? target)
    {
        using var readiness = BeginReadinessUpdate();
        var optional = !WorkerTrackingSelected;
        var missingWirelessTarget = wireless && string.IsNullOrWhiteSpace(target);
        var reason = wireless
            ? connected ? $"Wi-Fi ADB connected to the Quest at {target}."
                : missingWirelessTarget
                    ? "Wireless ADB is selected and no Quest Wi-Fi address is saved. In First-time setup, enter the Quest's current Wi-Fi IP and press Connect to Quest. Select USB cable to use the cable connection."
                    : $"Wi-Fi ADB has not confirmed the Quest at {target}. Keep the headset awake on the same trusted network, check its current Wi-Fi IP, then press Connect to Quest in First-time setup."
            : _environment.UsbConnectionMessage;
        SetStatus(_usbStatus, connected ? StatusKind.Good : optional ? StatusKind.Neutral : StatusKind.Bad,
            connected ? wireless ? "Wi-Fi ADB connected" : "USB ADB connected"
                : optional ? "Optional" : wireless ? missingWirelessTarget ? "Wi-Fi address needed" : "Wi-Fi not connected" : "USB not connected");
        _connectionModeNote.Text = reason;
        _connectionModeNote.ForeColor = connected || optional ? Muted : Warning;
        _usbStatus.AccessibleDescription = reason;
        if (!connected && optional)
        {
            if (wireless) _lastWirelessStatusReason = null;
            else _lastUsbStatusReason = _lastUsbFailureDetail = null;
            return;
        }
        if (wireless)
        {
            if (reason != _lastWirelessStatusReason)
                AppendLog("[Wi-Fi connection check] " + (connected ? "" : "WARNING: ") + reason);
            _lastWirelessStatusReason = reason;
        }
        else
        {
            // A successful `adb devices` command can still report unauthorized,
            // offline or no devices. Preserve those state reasons in Activity.
            if (reason != _lastUsbStatusReason)
                AppendLog("[USB connection check] " + (connected ? "" : "WARNING: ") + reason);
            _lastUsbStatusReason = reason;
            var failure = _environment.UsbFailureDetail;
            if (failure is not null && failure != _lastUsbFailureDetail) AppendLog("[USB connection check] " + failure);
            _lastUsbFailureDetail = failure;
        }
    }

    private void UpdateSetupStepStyles() => UpdateSetupStepStylesFromSnapshot(null);

    private void UpdateSetupStepStylesFromSnapshot(HubStatusSnapshot? snapshot)
    {
        if (_previewOnly) return;
        var runtimeReady = snapshot?.RuntimeReady ?? BackendReady();
        var runtimeNeeded = WorkerTrackingSelected && !runtimeReady;
        StyleSetupStep(_setupRuntimeButton, _setupRuntimeStatus, "Install runtime", runtimeReady, runtimeNeeded);
        _setupRuntimeButton.VisualStyle = runtimeNeeded ? HubButtonStyle.Primary : HubButtonStyle.Secondary;
        UpdateGazeSetupStatus();
        var installedVersion = snapshot is not null ? snapshot.RocmInstalledVersion : LatestRocmInstalledVersion();
        var latestReady = installedVersion is not null;
        var offeredVersionReady = installedVersion?.StartsWith(HubRocmRuntime.InstallVersion + ".", StringComparison.Ordinal) == true;
        var legacyReady = snapshot?.LegacyRocmReady ?? LegacyRocmInstalled();
        var latestEnvironmentExists = snapshot?.RocmEnvironmentExists ?? LatestRocmEnvironmentExists();
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
        TrackSetupButtonBorder(_setupAmdButton, offeredVersionReady, AmdInstallEligible);
        SetSetupButtonsEnabled(true, runtimeReady);
    }

    private void RememberGazeCompatibility(HubCompatibilityReport report)
    {
        _lastGazeStatusObservation = new(report, null, GetConfiguredAdbTarget(), _environment.WirelessSelected);
        // A failed root/environment read is not a negative module scan.
        if (report.HasActiveMagiskGaze || report.GazePreflightPassed)
            _magiskGazeState.Observe(GetConfiguredAdbTarget(), _environment.WirelessSelected, report.HasActiveMagiskGaze);
        UpdateGazeSetupStatus();
    }

    private void RememberGazeInspection(HubGazeInspection report)
    {
        _lastGazeStatusObservation = new(null, report, GetConfiguredAdbTarget(), _environment.WirelessSelected);
        _magiskGazeState.Observe(GetConfiguredAdbTarget(), _environment.WirelessSelected, report.HasActiveMagiskGaze);
        UpdateGazeSetupStatus();
    }

    private void CompleteGazeStatusRecovery(bool confirmed)
    {
        if (!confirmed) return;
        // A confirmed recovery changes the state described by the last check.
        // Clear that observation without claiming a fresh stock-tracking check.
        _lastGazeStatusObservation = null;
        UpdateGazeSetupStatus();
    }

    private void UpdateGazeSetupStatus()
    {
        var observed = _lastGazeStatusObservation;
        var target = GetConfiguredAdbTarget();
        if (observed is not null && (observed.Wireless != _environment.WirelessSelected ||
            target is not null && observed.Target != target))
            observed = _lastGazeStatusObservation = null;
        // Keep the last completed observation during unrelated refreshes and
        // pending checks. It describes that check, never runtime authorization.
        var magisk = HasActiveMagiskGaze;
        var sessionEditable = !_trackingCleanupPending && !LiveTrackingRunning && !_starting && !_stopping;
        ApplyMagiskGazeControlGate(sessionEditable);
        var needsRecovery = observed?.Inspection?.NeedsAttention == true;
        var incompatible = observed?.Compatibility is { HasActiveMagiskGaze: false, CanPrepareGaze: false };
        var prepared = EyeModelReady();
        string setupText, statusText;
        Color setupColor;
        StatusKind status;
        if (needsRecovery)
        {
            setupText = "Last gaze check needs attention · see Activity";
            statusText = "Recovery or gaze selection needs attention";
            setupColor = Warning; status = StatusKind.Warning;
        }
        else if (magisk)
        {
            setupText = _gaze.Checked ? "Magisk gaze detected · Stop tracking first" : "Provided by Magisk · Skip Hub gaze setup";
            statusText = _gaze.Checked ? "Two gaze methods · Stop tracking" : "Provided by Magisk · Hub gaze off";
            setupColor = _gaze.Checked ? Warning : Muted;
            status = _gaze.Checked ? StatusKind.Warning : StatusKind.Good;
        }
        else if (incompatible)
        {
            setupText = "Hub gaze unavailable · see compatibility details";
            statusText = "Hub method unavailable · other features separate";
            setupColor = Warning; status = StatusKind.Warning;
        }
        else
        {
            setupText = !_gaze.Checked ? "Optional · skip for Magisk gaze" :
                prepared ? "Prepared · validated at start" : "Prepare only for Hub gaze";
            statusText = !_gaze.Checked ? "Hub gaze off · optional" :
                prepared ? "Prepared · validated at start" : "Hub gaze needs preparation";
            setupColor = !_gaze.Checked ? Muted : prepared ? Good : Warning;
            status = _gaze.Checked ? StatusKind.Warning : StatusKind.Good;
        }
        // Do not write Complete/Waiting before the final optional-gaze message.
        // Auto-sized labels would otherwise relayout twice on every pulse.
        StyleSetupButton(_setupGazeButton, "Prepare gaze", prepared && !magisk && !needsRecovery && !incompatible, false);
        if (_setupGazeStatus.Text != setupText) _setupGazeStatus.Text = setupText;
        if (_setupGazeStatus.ForeColor != setupColor) _setupGazeStatus.ForeColor = setupColor;
        SetStatus(_gazeStatus, status, statusText);
        if (magisk) _setupGazeButton.Enabled = _inspectGazeButton.Enabled = false;
    }

    private void StyleSetupStep(DarkButton button, Label status, string label, bool complete, bool attention)
    {
        StyleSetupButton(button, label, complete, attention);
        status.Text = complete ? "● Complete" : attention ? "● Next step" : "○ Optional for your selected features";
        status.ForeColor = complete ? Good : attention ? Warning : Muted;
    }

    private void StyleSetupButton(DarkButton button, string label, bool complete, bool attention)
    {
        var text = complete ? "✓  " + label : label;
        if (button.Text != text) button.Text = text;
        TrackSetupButtonBorder(button, complete, attention);
    }

    private void TrackSetupButtonBorder(DarkButton button, bool complete, bool attention)
    {
        _setupButtonStyles[button] = (complete, attention);
        ApplySetupButtonBorder(button, complete, attention);
    }

    private void ApplySetupButtonBorder(DarkButton button, bool complete, bool attention)
    {
        var color = complete ? Good : attention && _setupPulseOn ? Accent : ControlOutline;
        var width = complete || attention && _setupPulseOn ? 2 : 1;
        if (button.OutlineColor != color) button.OutlineColor = color;
        if (button.OutlineWidth != width) button.OutlineWidth = width;
    }

    private void PulseSetupAttention()
    {
        // Animation uses the readiness snapshot and performs no status probes.
        var active = _setupButtonStyles.Where(item => !item.Value.Complete && item.Value.Attention &&
            item.Key.Visible && item.Key.Enabled).ToArray();
        if (active.Length == 0) { _setupPulseOn = false; return; }
        _setupPulseOn = !_setupPulseOn;
        foreach (var item in active) ApplySetupButtonBorder(item.Key, false, true);
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
            .Any(HubProcessProbe.AnyRunning);
    private bool BridgeUninstallAvailable()
    {
        return BridgeInstalled() || HubModuleInstallation.HasSavedBackups(Path.Combine(_root, "research"));
    }
    private bool BackendReady() => !_previewOnly && _environment.BackendReady(!TrackingShutdownPending);
    private bool EyeModelReady() => _environment.EyeModelReady();
    private string VisibilityModeValue() => _visibilityMode.SelectedIndex switch { 1 => "camera", 2 => "native", 3 => "agreement", _ => "weighted" };
    private string PupilSensitivityValue() => ((10 + 2 * Math.Max(0, _pupilSensitivity.SelectedIndex)) / 10.0)
        .ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    private string? FindPythonRuntime() => _previewOnly ? null : _environment.FindPythonRuntime(!TrackingShutdownPending);

    private void UpdateControlState() => UpdateControlStateFromSnapshot(null);

    private void UpdateControlStateFromSnapshot(HubStatusSnapshot? snapshot)
    {
        var running = _trackingCleanupPending || LiveTrackingRunning;
        // These values are passed once to the child process. Keep the controls
        // locked until Stop, rather than implying that a live model was reloaded.
        var sessionEditable = !running && !_stopping && !_starting && !_faceRouteChanging;
        ApplyMagiskGazeControlGate(sessionEditable);
        _tongue.Enabled = sessionEditable;
        _pupil.Enabled = sessionEditable;
        var adjustmentsEditable = (sessionEditable || _directSession is not null || _backupFaceSession is not null) &&
            !_starting && !_stopping && !_trackingCleanupPending && !_closingInProgress;
        _individualCheekPuff.Enabled = _individualCheekSuck.Enabled = _eyebrowBoost.Enabled = adjustmentsEditable;
        _eyebrowSensitivity.Enabled = adjustmentsEditable && _eyebrowBoost.Checked;
        // Model selection precedes the opt-in output switches. It must remain
        // usable when all camera features are off on a fresh Hub launch.
        _tongueModels.Enabled = sessionEditable;
        _fps.Enabled = (_tongue.Checked || _pupil.Checked) && sessionEditable;
        _processingProfile.Enabled = _pupil.Checked && sessionEditable;
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
        _inspectGazeButton.Enabled = _recoverGazeButton.Enabled && !HasActiveMagiskGaze;
        _resetLegacyGazeButton.Enabled = _recoverGazeButton.Enabled;
        var canStart = !running && !_stopping && !_starting && !_faceRouteChanging && !_gazeRecoveryRunning && !_closingInProgress && !_setupActionRunning &&
            !_utilityActionRunning && !_datasetOperationBusy;
        if (_checkCompatibility is not null) _checkCompatibility.Enabled = canStart && !_compatibilityChecking;
        var faceRouteReady = !FaceAdjustmentsSelected || (BackupSelected ? _backupBundled : DirectRouteAvailable && _ownerProblem is null);
        _start.Enabled = canStart && faceRouteReady && !ControllerRestartBlocked && (FaceAdjustmentsSelected || _hybridHands.Checked || _controllerTouchpad.Checked);
        _stop.Enabled = (running || _starting) && !_stopping;
        StyleRunButton(_start, _start.Enabled);
        StyleRunButton(_stop, (running || _starting) && !_stopping);
        UpdateVrchatCheckButtons();
        UpdateOutputRouteControls();
        UpdateControllerInputAvailabilityFromSnapshot(snapshot?.RuntimeReady);
        UpdateStreamerUpdaterAvailability();
        SetSetupButtonsEnabled(true, snapshot?.RuntimeReady);
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
            if (_steamVrStartupChange is { } startupChange) await startupChange;
            await SuspendNativeIdleAsync();
            try { SaveLiveOptions(); }
            catch (Exception error) { AppendLog("Could not save Facial tracking choices: " + error.Message); }
            await _environment.SuspendAdbProbesAsync();
            if (_starting || _stopping || LiveTrackingRunning)
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
                RequestNativeIdleRefresh();
            }
        }
    }

    private void PostProcessUpdate(string label, Action update)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired) { QueueProcessUiUpdate(label, null, update); return; }
        using var readiness = BeginReadinessUpdate();
        ApplyProcessUpdate(label, update);
    }

    private void AppendLog(string text)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired || _activityBatch is null && !_processUiUpdates.IsEmpty)
        {
            // A synchronous completion log joins already queued pipe output,
            // so a bounded drain cannot move the final result ahead of it.
            QueueProcessUiUpdate("Activity", text, null);
            return;
        }
        RecordActivity(text, DateTime.Now.ToString("HH:mm:ss"));
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
    private enum StatusKind { Good, Warning, Bad, Neutral }
}
