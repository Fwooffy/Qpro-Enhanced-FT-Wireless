namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private readonly HubUpdateNotificationState _updateNotification = new();
    private System.Windows.Forms.Timer? _updateNotificationTimer;
    private bool _updateNotificationOpen;
    private bool _updateDialogOpen;

    private void WireUpdateNotifications()
    {
        Activated += (_, _) =>
        {
            // Activation can occur during a modal dialog's cleanup. Defer until
            // its owner is enabled and focus has settled before considering a notice.
            if (!IsDisposed && !Disposing && _updateNotification.Pending is not null) _updateNotificationTimer?.Start();
        };
        FormClosed += (_, _) => _updateNotificationTimer?.Stop();
    }

    private void QueueUpdateNotification(HubUpdateCheck check)
    {
        _updateNotification.Observe(check, AppVersion, _updateIncludePrereleases);
        if (_updateNotification.Pending is not null)
        {
            // No additional polling runs when there is no release to announce.
            if (_updateNotificationTimer is null)
            {
                _updateNotificationTimer = new System.Windows.Forms.Timer(_uiComponents) { Interval = 1000 };
                _updateNotificationTimer.Tick += async (_, _) => await ShowPendingUpdateNotificationAsync();
            }
            _updateNotificationTimer.Start();
        }
        else _updateNotificationTimer?.Stop();
    }

    private void RefreshUpdateLink()
    {
        var available = UpdateCheckForReview();
        _updateLink.Text = available?.Release is not null ? "Update available"
            : _componentPlan?.Updates.Count > 0 ? "Component updates" : "Check updates";
        _updateLink.AccessibleDescription = available?.Message ?? _updateCheck?.Message ?? "Check the selected release channel again.";
    }

    private HubUpdateCheck? UpdateCheckForReview() => _updateNotification.ForReview(_updateCheck);

    private bool CanShowUpdateNotification()
    {
        if (IsDisposed || Disposing || _closingInProgress) return false;
        bool ready = !_previewOnly && IsHandleCreated && Visible && Enabled && ContainsFocus && ActiveForm == this &&
            WindowState != FormWindowState.Minimized && !_interactiveResize;
        if (!ready) return false;
        bool busy = _updateChecking || _starting || _stopping || _setupActionRunning || _utilityActionRunning ||
            _datasetOperationBusy || _trackingCleanupPending || _gazeRecoveryRunning || _compatibilityChecking || LiveTrackingRunning;
        bool modal = _updateNotificationOpen || _updateDialogOpen || OwnedForms.Any(form => form.Visible);
        return HubUpdateNotificationState.CanPrompt(ready, busy, modal, IsDisposed || Disposing || _closingInProgress);
    }

    private async Task ShowPendingUpdateNotificationAsync()
    {
        if (_updateNotification.Pending?.Release is not { } release)
        { _updateNotificationTimer?.Stop(); return; }
        if (!CanShowUpdateNotification()) return;
        _updateNotificationOpen = true;
        bool viewUpdate = false;
        try
        {
            using var dialog = new HubUpdateAvailableDialog(release);
            _updateNotification.Acknowledge(release);
            _updateNotificationTimer?.Stop();
            viewUpdate = dialog.ShowDialog(this) == DialogResult.OK;
        }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing) AppendLog("[Updates] The update notice could not open. Use Update available to review the release. " + error.Message);
        }
        finally { _updateNotificationOpen = false; }
        // Opening this notice never downloads or applies an update.
        if (viewUpdate && !IsDisposed && !Disposing && !_closingInProgress) await HandleHubUpdateClickAsync();
    }
}
