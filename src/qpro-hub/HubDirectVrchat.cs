using System.Net.Sockets;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private VrchatFaceSession? _directSession;
    private bool _foregroundFaceStopping;
    private Task _directStop = Task.CompletedTask;
    private bool _cameraFaceWorkerExited;
    private CancellationTokenSource? _vrchatCheck;
    private DarkButton? _checkVrchatLiveButton;
    private DarkButton? _checkVrchatSetupButton;
    private readonly Func<bool, VrchatFaceSelection, Action<VrchatFaceStatus>, VrchatFaceSession> _openDirectSession;

    private VrchatFaceSelection FaceSelection() => new(_gaze.Checked, _tongue.Checked,
        _pupil.Checked, _eyebrowBoost.Checked, _individualCheekPuff.Checked, _individualCheekSuck.Checked);
    private bool FaceAdjustmentsSelected => FaceSelection().HasAdjustments;

    private void UpdateDirectFaceAdjustments()
    {
        var selection = FaceSelection();
        // Keep next-start choices while an ended camera worker yields to native input.
        if (_cameraFaceWorkerExited) selection = selection with { Tongue = false, Pupils = false };
        _directSession?.UpdateSelection(selection);
        _backupFaceSession?.SetActive(selection.HasAdjustments);
    }

    private void StartDirectTracking(VrchatFaceSelection selection)
    {
        if (!EnsureDirectOwnership()) throw new InvalidOperationException("Direct output is paused. Check the face output in First-time setup.");
        _cameraFaceWorkerExited = false;
        VrchatFaceSession? current = null;
        current = _directSession = _openDirectSession(_environment.SteamLinkSelected, selection, status => PostProcessUpdate("VRChat", () =>
        {
            if (current is not null) ShowDirectSessionStatus(current, status);
        }));
        if (!WorkerTrackingSelected)
            SetStatus(_runStatus, StatusKind.Warning, "Waiting for VRChat face output");
        _ = ObserveDirectTrackingAsync(current);
    }

    private static VrchatFaceSession OpenDirectSession(bool steam, VrchatFaceSelection selection, Action<VrchatFaceStatus> report)
    {
        var source = new VrchatFaceSource(steam, enableGaze: selection.Gaze, enableTongue: selection.Tongue, enablePupils: selection.Pupils);
        try
        {
            source.SetFaceAdjustments(selection);
            return new(source, report);
        }
        catch { source.Dispose(); throw; }
    }

    private void ShowDirectSessionStatus(VrchatFaceSession session, VrchatFaceStatus status)
    {
        if (!ReferenceEquals(session, _directSession) || !DirectRouteAvailable || _stopping || _closingInProgress) return;
        ShowVrchatStatus(status);
        if (!WorkerTrackingSelected && !_gazeFailureHandled)
            SetStatus(_runStatus, status.Ready ? StatusKind.Good : StatusKind.Warning,
                status.Ready ? "Sending face data to VRChat" : "Waiting for VRChat face output");
    }

    private async Task ObserveDirectTrackingAsync(VrchatFaceSession session)
    {
        try { await session.Completion; }
        catch (Exception error)
        {
            if (IsDisposed || Disposing || !ReferenceEquals(session, _directSession)) return;
            ShowVrchatStatus(new("Direct face tracking stopped: " + error.Message, false, true));
            SetActionFeedback("Direct face tracking stopped", error.Message,
                "Check the streaming app's face sharing and press Start tracking again.", true);
            await StopTrackingAsync();
        }
    }

    private Task StopDirectTrackingAsync() => _directStop.IsCompleted ? _directStop = StopDirectWriterAsync() : _directStop;

    private async Task StopDirectWriterAsync()
    {
        CancelVrchatCheck();
        if (_directSession is { } session)
        {
            _foregroundFaceStopping = true;
            _directSession = null;
            try { await session.StopAsync(); }
            catch (Exception error) { AppendLog("[VRChat] Direct output stopped with an error: " + error.Message); }
            finally { _foregroundFaceStopping = false; }
        }
        if (!IsDisposed && !Disposing && !_faceOutputEnding && _nativeIdleSession is null)
        {
            if (DirectRouteAvailable) SetVrchatOutputOff();
            else ShowOutputRouteState();
        }
    }

    private void SetVrchatOutputOff()
    {
        _directVrchatStatus.AccessibleDescription = null;
        SetStatus(_directVrchatStatus, StatusKind.Neutral, "VRChat output off");
    }

    private void CancelVrchatCheck()
    {
        var check = _vrchatCheck;
        _vrchatCheck = null;
        check?.Cancel();
        UpdateVrchatCheckButtons();
    }

    private void ShowVrchatStatus(VrchatFaceStatus status)
    {
        _directVrchatStatus.AccessibleDescription = null;
        SetStatus(_directVrchatStatus, status.Ready ? StatusKind.Good : StatusKind.Warning, status.Text);
        if (DirectSelected)
        {
            if (status.Ready) _offerBackup = false;
            else if (status.OfferBackup is bool offer) _offerBackup = offer;
            UpdateOutputRouteControls();
        }
        AppendLog("[VRChat] " + status.Text);
    }

    private void CheckVrchatConnection()
    {
        if (!DirectSelected || !_previewOnly || _nativeIdleSession is not null || _directSession is not null)
            _ = CheckVrchatConnectionAsync(VrchatOscQuery.DiscoverAsync);
    }

    private void UpdateVrchatCheckButtons()
    {
        if (Disposing || IsDisposed) return;
        bool enabled = !_faceRouteChanging && !_closingInProgress && !_starting && !_stopping && _vrchatCheck is null;
        if (_checkVrchatSetupButton is not null) _checkVrchatSetupButton.Enabled = enabled;
        if (_checkVrchatLiveButton is not null) _checkVrchatLiveButton.Enabled = enabled;
    }

    private async Task CheckVrchatConnectionAsync(Func<CancellationToken, Task<VrchatOscTarget?>> discover)
    {
        if (_faceRouteChanging || _closingInProgress || _starting || _stopping || _vrchatCheck is not null) return;
        using var check = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _vrchatCheck = check;
        UpdateVrchatCheckButtons();
        _directVrchatStatus.AccessibleDescription = null;
        SetStatus(_directVrchatStatus, StatusKind.Warning, BackupSelected ? "Checking the backup module…" : "Looking for VRChat…");
        bool Current() => ReferenceEquals(check, _vrchatCheck) && !IsDisposed && !Disposing && !_closingInProgress;
        try
        {
            if (BackupSelected)
            {
                _backupConnectionVerified = false;
                string? problem = _backupBundled ? await BackupModuleProblemAsync() : BackupPackageMissing;
                if (Current())
                {
                    _backupConnectionVerified = problem is null;
                    ShowVrchatStatus(new(problem ?? "The last check found an active Qpro module in VRCFaceTracking for the selected source.", problem is null));
                }
                return;
            }
            if (!DirectRouteAvailable || _ownerProblem is not null) { ShowOutputRouteState(); return; }
            if ((_directSession ?? _nativeIdleSession) is { } session)
            {
                await session.RequestRefreshAsync(check.Token);
                return;
            }
            var target = await discover(check.Token);
            if (!Current()) return;
            check.Token.ThrowIfCancellationRequested();
            if (target is null)
            {
                ShowVrchatStatus(new("VRChat not found. Open the game and enable OSC in its Action Menu.", false, true));
                return;
            }
            using var output = new VrchatFaceOutput(target.Endpoint, target.Inputs, target.NativeFace, blockedInputs: target.BlockedInputs);
            ShowVrchatStatus(output.SupportedInputCount > 0
                ? new(output.EyeOnly ? "VRChat found. Built-in eye tracking is available. This avatar has no recognized face inputs."
                    : "VRChat found. This avatar accepts direct face tracking.", true)
                : new("VRChat found. This avatar has no recognized face or eye inputs.", false, true));
        }
        catch (OperationCanceledException) when (check.IsCancellationRequested)
        {
            if (Current())
                ShowVrchatStatus(new("VRChat discovery timed out. Check OSC in the Action Menu and retry.", false, true));
        }
        catch (Exception ex) when (ex is SocketException or IOException or HttpRequestException or UnauthorizedAccessException)
        {
            if (Current() && !check.IsCancellationRequested)
                ShowVrchatStatus(new("Could not check VRChat: " + ex.Message, false, true));
        }
        finally
        {
            if (ReferenceEquals(check, _vrchatCheck))
            {
                _vrchatCheck = null;
                UpdateVrchatCheckButtons();
            }
        }
    }
}
