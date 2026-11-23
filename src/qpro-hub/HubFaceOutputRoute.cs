using Qpro.Shared;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private readonly string? _faceOutputConfigDirectory;
    private readonly Func<bool> _competingFaceHost;
    private readonly Func<bool> _packageProvidesBackup;
    private FaceOutputRoute _faceOutputRoute;
    private IDisposable? _directOwner;
    private IDisposable? _faceExtrasOwner;
    private BackupFaceAdjustments? _backupFaceSession;
    private bool _faceRouteChanging, _syncRouteChoice, _directHostConflict, _faceOutputEnding;
    private bool _offerBackup;
    private bool _backupBundled;
    private bool _backupConnectionVerified;
    private string? _ownerProblem;
    private bool MemoryOnlyRoute => _previewOnly && _faceOutputConfigDirectory is null;
    private bool BackupSelected => _faceOutputRoute == FaceOutputRoute.Vrcft;
    private bool DirectSelected => _faceOutputRoute is FaceOutputRoute.Direct or FaceOutputRoute.Unconfigured;
    private bool DirectRouteAvailable => DirectSelected && !_faceRouteChanging && !_directHostConflict && !_faceOutputEnding;
    private bool RouteChoiceAvailable => !_faceRouteChanging && !_faceOutputEnding && !_closingInProgress &&
        !_starting && !_stopping && !_trackingCleanupPending && !_datasetOperationBusy && !_gazeRecoveryRunning &&
        !_setupActionRunning && !_utilityActionRunning;
    private bool SelectedRouteStillCurrent => MemoryOnlyRoute || _directOwner is not null ||
        FaceOutputOwnership.ReadRoute(_faceOutputConfigDirectory) == _faceOutputRoute;

    private void ConfigureOutputRouteControls()
    {
        _faceOutputRoute = MemoryOnlyRoute ? FaceOutputRoute.Unconfigured : FaceOutputOwnership.ReadRoute(_faceOutputConfigDirectory);
        _backupBundled = _packageProvidesBackup();
        SyncOutputRouteChoice();
        _outputRouteSetup.SelectedIndexChanged += async (_, _) =>
        {
            if (_syncRouteChoice || _outputRouteSetup.SelectedIndex < 0) return;
            bool heldFocus = _outputRouteSetup.ContainsFocus;
            var transition = SelectFaceOutputRouteAsync(_outputRouteSetup.SelectedIndex == 1 ? FaceOutputRoute.Vrcft : FaceOutputRoute.Direct);
            var focusAfterDispatch = ActiveControl;
            await transition;
            if (heldFocus && ContainsFocus && ReferenceEquals(ActiveControl, focusAfterDispatch))
                _outputRouteSetup.FocusSelectedChoice();
        };
        _useBackupOutputButton.Click += async (_, _) => await SelectFaceOutputRouteAsync(FaceOutputRoute.Vrcft);
        _installBackupModuleButton.Click += async (_, _) =>
        {
            if (BackupSelected && RouteChoiceAvailable && !LiveTrackingRunning) await InstallBackupModuleAsync();
        };
        if (!DirectSelected) ShowOutputRouteState();
        UpdateOutputRouteControls();
        var poll = new System.Windows.Forms.Timer(_uiComponents) { Interval = 1000 };
        poll.Tick += async (_, _) => await ObserveOutputOwnershipAsync();
        if (!_previewOnly) poll.Start();
    }

    private void SyncOutputRouteChoice()
    {
        _syncRouteChoice = true;
        try { _outputRouteSetup.SelectedIndex = BackupSelected ? 1 : DirectSelected ? 0 : -1; }
        finally { _syncRouteChoice = false; }
        UpdateTrackingSourceNotes();
    }

    private void UpdateOutputRouteControls()
    {
        bool editable = RouteChoiceAvailable;
        _outputRouteSetup.Enabled = editable;
        _installBackupModuleButton.Enabled = editable && BackupSelected && _backupBundled && !LiveTrackingRunning;
        _useBackupOutputButton.Visible = DirectSelected && _offerBackup;
        _useBackupOutputButton.Enabled = editable && DirectSelected && _offerBackup;
    }

    private void ShowOutputRouteState()
    {
        _backupConnectionVerified = false;
        _directVrchatStatus.AccessibleDescription = null;
        if (BackupSelected)
            SetStatus(_directVrchatStatus, _backupBundled ? StatusKind.Neutral : StatusKind.Warning,
                _backupBundled ? "VRCFaceTracking backup selected. Check the backup connection in First-time setup." : BackupPackageMissing);
        else if (!DirectSelected)
            SetStatus(_directVrchatStatus, StatusKind.Warning, "Face output preference is unreadable. Choose an output in First-time setup.");
        else if (_directHostConflict || _ownerProblem is not null)
            SetStatus(_directVrchatStatus, StatusKind.Warning, _ownerProblem ?? "Direct output paused. Close VRCFaceTracking to resume, or choose the backup.");
    }

    private bool EnsureDirectOwnership()
    {
        if (!DirectRouteAvailable) return false;
        if (_competingFaceHost())
        {
            _ = PauseForCompetingHostAsync();
            return false;
        }
        if (_directOwner is not null || MemoryOnlyRoute || (_directOwner = FaceOutputOwnership.TryAcquireDirect(_faceOutputConfigDirectory)) is not null)
        {
            if (_ownerProblem is not null) { _ownerProblem = null; UpdateControlState(); }
            return true;
        }
        bool newlyBlocked = _ownerProblem is null;
        _ownerProblem = "Direct output paused. Another face output owns the connection, or its preference cannot be read.";
        _offerBackup = true;
        ShowOutputRouteState();
        if (newlyBlocked) UpdateControlState();
        return false;
    }

    private async Task SelectFaceOutputRouteAsync(FaceOutputRoute route)
    {
        if (route == _faceOutputRoute || (route == FaceOutputRoute.Direct && DirectSelected) || !RouteChoiceAvailable)
        {
            SyncOutputRouteChoice();
            return;
        }
        _faceRouteChanging = true;
        UpdateControlState();
        IDisposable? transition = null;
        IDisposable? extras = null;
        try
        {
            if (route == FaceOutputRoute.Vrcft)
            {
                _backupBundled = _packageProvidesBackup();
                if (!_backupBundled) throw new InvalidOperationException(BackupPackageMissing);
            }
            if (route == FaceOutputRoute.Direct && _competingFaceHost())
                throw new InvalidOperationException("Close VRCFaceTracking, then choose direct output again.");
            if (!MemoryOnlyRoute && _faceExtrasOwner is null)
                extras = FaceOutputOwnership.TryAcquireExtras(_faceOutputConfigDirectory) ?? throw new IOException("Another Companion window is using face adjustments. Stop tracking there before changing output.");
            CancelVrchatCheck();
            // A choice changes the face producer for this session. Independent
            // controller-only workers can continue through an idle handoff.
            if ((_directSession is not null || _backupFaceSession is not null) && !await TryStopTrackingAsync())
                throw new InvalidOperationException("Finish tracking cleanup before changing face output.");
            await SuspendNativeIdleAsync();
            if (_faceOutputEnding || _closingInProgress) return;
            if (route == FaceOutputRoute.Direct && _competingFaceHost())
                throw new InvalidOperationException("Close VRCFaceTracking, then choose direct output again.");
            if (!MemoryOnlyRoute)
            {
                // Stopping our foreground just released its producer handle.
                // Reacquire it before changing the preference across processes.
                extras ??= FaceOutputOwnership.TryAcquireExtras(_faceOutputConfigDirectory) ?? throw new IOException("Another Companion window started face adjustments. Stop tracking there and retry.");
                transition = _directOwner ?? FaceOutputOwnership.TryAcquireTransition(_faceOutputConfigDirectory);
                if (transition is null) throw new IOException("Another app owns face output, or its configuration is unavailable. Close VRCFaceTracking and any other Companion window, then retry.");
                FaceOutputOwnership.SaveRoute(route, _faceOutputConfigDirectory);
                _directOwner = route == FaceOutputRoute.Direct ? transition : null;
            }
            _faceOutputRoute = route;
            _directHostConflict = false;
            _offerBackup = false;
            _ownerProblem = null;
            if (BackupSelected) ShowOutputRouteState();
            else SetVrchatOutputOff();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            SetActionFeedback("Face output could not change", error.Message, "Check First-time setup, then choose the output again.", true);
            SetStatus(_directVrchatStatus, StatusKind.Warning, error.Message);
        }
        finally
        {
            if (!ReferenceEquals(transition, _directOwner)) transition?.Dispose();
            extras?.Dispose();
            _faceRouteChanging = false;
            if (!_faceOutputEnding && !IsDisposed && !Disposing)
            {
                SyncOutputRouteChoice();
                UpdateControlState();
                RequestNativeIdleRefresh();
            }
        }
    }

    private async Task ObserveOutputOwnershipAsync()
    {
        if (_faceOutputEnding || _faceRouteChanging || _closingInProgress || !_nativeIdleShown) return;
        if (_directOwner is null && !MemoryOnlyRoute)
        {
            var saved = FaceOutputOwnership.ReadRoute(_faceOutputConfigDirectory);
            if (saved != _faceOutputRoute)
            {
                if (_datasetOperationBusy || _setupActionRunning || _utilityActionRunning || _stopping ||
                    (_starting && _backupFaceSession is null)) return;
                _faceRouteChanging = true;
                try
                {
                    CancelVrchatCheck();
                    if ((_backupFaceSession is not null || _trackingCleanupPending) && !await TryStopTrackingAsync()) return;
                    if (_faceOutputEnding || _closingInProgress) return;
                    _faceOutputRoute = FaceOutputOwnership.ReadRoute(_faceOutputConfigDirectory);
                    _ownerProblem = null;
                    _offerBackup = false;
                    SyncOutputRouteChoice();
                    ShowOutputRouteState();
                }
                finally
                {
                    _faceRouteChanging = false;
                    if (!_faceOutputEnding) UpdateControlState();
                }
            }
        }
        if (!DirectSelected)
        {
            if (BackupSelected && _backupConnectionVerified && !_competingFaceHost())
            {
                _backupConnectionVerified = false;
                ShowVrchatStatus(new("Open VRCFaceTracking and check the backup connection again.", false));
            }
            return;
        }
        bool conflict = _competingFaceHost();
        if (conflict && !_directHostConflict) await PauseForCompetingHostAsync();
        else if (!conflict && (_directHostConflict || _ownerProblem is not null))
        {
            _directHostConflict = false;
            RequestNativeIdleRefresh();
            UpdateControlState();
        }
        else if (!conflict && _nativeIdleFeed is null && MayOpenNativeIdle) RequestNativeIdleRefresh();
    }

    private async Task PauseForCompetingHostAsync()
    {
        if (_directHostConflict) return;
        _directHostConflict = true;
        _ownerProblem = null;
        _offerBackup = true;
        _faceRouteChanging = true;
        try
        {
            CancelVrchatCheck();
            var idleStopped = SuspendNativeIdleAsync();
            if (_directSession is not null || (_starting && FaceAdjustmentsSelected)) await TryStopTrackingAsync();
            await idleStopped;
            _directOwner?.Dispose();
            _directOwner = null;
        }
        finally
        {
            _faceRouteChanging = false;
            if (!_faceOutputEnding) { ShowOutputRouteState(); UpdateControlState(); }
        }
    }

    private void EndFaceOutput()
    {
        if (_faceOutputEnding) return;
        _faceOutputEnding = true;
        _nativeIdleShown = _nativeCaptureLabels = false;
        CancelVrchatCheck();
        _backupFaceSession?.Dispose();
        _backupFaceSession = null;
        if (_trackingProcesses.All(p => p.HasExited) && !_trackingCleanupPending) ReleaseFaceExtras();
        _ = ReleaseFaceOwnerOnCloseAsync();
    }

    private async Task ReleaseFaceOwnerOnCloseAsync()
    {
        try { await Task.WhenAll(StopDirectTrackingAsync(), SuspendNativeIdleAsync()).ConfigureAwait(false); }
        finally { Interlocked.Exchange(ref _directOwner, null)?.Dispose(); }
    }

    private bool AcquireFaceExtras()
    {
        if (!MemoryOnlyRoute)
            _faceExtrasOwner ??= FaceOutputOwnership.TryAcquireExtras(_faceOutputConfigDirectory);
        if ((MemoryOnlyRoute || _faceExtrasOwner is not null) && SelectedRouteStillCurrent) return true;
        ReleaseFaceExtras();
        return false;
    }

    private void ReleaseFaceExtras() => Interlocked.Exchange(ref _faceExtrasOwner, null)?.Dispose();

    // A foreground session remains stoppable when its final live toggle is
    // off. Only the presence of this named event enables module adjustments.
    private sealed class BackupFaceAdjustments(string? name) : IDisposable
    {
        private CheekTrackingSession? _presence;

        internal void SetActive(bool active)
        {
            if (active) _presence ??= new CheekTrackingSession(name);
            else Dispose();
        }

        public void Dispose() { _presence?.Dispose(); _presence = null; }
    }
}
