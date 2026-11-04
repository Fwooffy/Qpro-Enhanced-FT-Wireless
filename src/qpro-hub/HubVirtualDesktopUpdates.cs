namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private readonly Label _streamerUpdaterTitle = Info("Windows updater not checked");
    private readonly Label _streamerUpdaterDetail = Info("Check the installed Windows Streamer before changing its updater.");
    private readonly DarkButton _checkStreamerUpdater = SecondaryButton("Check updater status");
    private readonly DarkButton _pauseStreamerUpdater = SecondaryButton("Pause PC Streamer updater");
    private readonly DarkButton _restoreStreamerUpdater = SecondaryButton("Restore PC Streamer updater");
    private readonly CancellationTokenSource _streamerUpdaterLifetime = new();
    private CancellationToken _streamerUpdaterToken;
    private bool _streamerUpdaterLifetimeReleased;
    private VirtualDesktopUpdaterStatus? _streamerUpdaterState;
    private bool _streamerUpdaterChecking;
    private bool _streamerUpdaterChanging;

    private Control BuildStreamerUpdaterControls()
    {
        var panel = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, BackColor = Inset, Padding = new Padding(12),
            Margin = new Padding(0, 4, 0, 8), AccessibleName = "Windows Streamer updates",
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        void FullRow(Control control, int row)
        {
            panel.Controls.Add(control, 0, row);
            panel.SetColumnSpan(control, 2);
        }
        FullRow(Info("Applies to Virtual Desktop Streamer on this PC. Quest app updates are managed separately."), 0);
        FullRow(Info("Pause blocks network access for the verified Windows Updater.exe. Restoring removes Companion's block. Windows asks for administrator approval when you change it."), 1);
        _streamerUpdaterTitle.Font = new Font(UiFontName, 10F, FontStyle.Bold);
        FullRow(_streamerUpdaterTitle, 2);
        FullRow(_streamerUpdaterDetail, 3);
        FullRow(_checkStreamerUpdater, 4);
        _pauseStreamerUpdater.Dock = _restoreStreamerUpdater.Dock = DockStyle.Fill;
        _pauseStreamerUpdater.Margin = new Padding(0, 4, 8, 4);
        _restoreStreamerUpdater.Margin = new Padding(0, 4, 0, 4);
        panel.Controls.Add(_pauseStreamerUpdater, 0, 5);
        panel.Controls.Add(_restoreStreamerUpdater, 1, 5);
        FullRow(Info("Pausing does not downgrade Streamer or add support for an unrecognized hand driver. A newer Quest app may still need a matching Windows Streamer."), 6);
        return panel;
    }

    private void InitializeStreamerUpdaterUi()
    {
        _streamerUpdaterToken = _streamerUpdaterLifetime.Token;
        Disposed += (_, _) =>
        {
            if (_streamerUpdaterLifetimeReleased) return;
            _streamerUpdaterLifetimeReleased = true;
            _streamerUpdaterLifetime.Cancel();
            _streamerUpdaterLifetime.Dispose();
        };
        _checkStreamerUpdater.Click += async (_, _) => await RefreshStreamerUpdaterAsync();
        _pauseStreamerUpdater.Click += async (_, _) => await ChangeStreamerUpdaterAsync(restore: false);
        _restoreStreamerUpdater.Click += async (_, _) => await ChangeStreamerUpdaterAsync(restore: true);
        UpdateStreamerUpdaterAvailability();
    }

    private void UpdateStreamerUpdaterAvailability()
    {
        bool idle = !_closingInProgress && !_starting && !_stopping && !_trackingCleanupPending &&
            !_utilityActionRunning && !_setupActionRunning && !_datasetOperationBusy &&
            !_gazeRecoveryRunning && !LiveTrackingRunning && !_streamerUpdaterChanging;
        _checkStreamerUpdater.Enabled = idle && !_streamerUpdaterChecking;
        // Restore stays available after changing the streaming source to Steam Link.
        _pauseStreamerUpdater.Enabled = idle && !_streamerUpdaterChecking && _streamerUpdaterState?.CanPause == true;
        _restoreStreamerUpdater.Enabled = idle && !_streamerUpdaterChecking && _streamerUpdaterState?.CanRestore == true;
    }

    private void PresentStreamerUpdater(VirtualDesktopUpdaterStatus status)
    {
        _streamerUpdaterState = status;
        _streamerUpdaterTitle.Text = status.Title;
        _streamerUpdaterTitle.ForeColor = status.IsPaused ? Warning : status.CanPause ? Good : Warning;
        _streamerUpdaterDetail.Text = status.Detail;
        UpdateStreamerUpdaterAvailability();
    }

    private async Task RefreshStreamerUpdaterAsync()
    {
        if (_previewOnly || IsDisposed || Disposing || _streamerUpdaterChecking || _streamerUpdaterChanging) return;
        _streamerUpdaterChecking = true;
        _streamerUpdaterTitle.Text = "Checking Windows Streamer updater…";
        _streamerUpdaterTitle.ForeColor = Muted;
        UpdateStreamerUpdaterAvailability();
        try
        {
            var status = await VirtualDesktopUpdaterPolicy.InspectAsync(_root, _streamerUpdaterToken);
            if (!IsDisposed && !Disposing) PresentStreamerUpdater(status);
        }
        catch (OperationCanceledException) when (_streamerUpdaterToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (IsDisposed || Disposing) return;
            _streamerUpdaterState = null;
            _streamerUpdaterTitle.Text = "Windows updater status needs attention";
            _streamerUpdaterTitle.ForeColor = Warning;
            _streamerUpdaterDetail.Text = error.Message;
            AppendLog("[Windows Streamer updater] Status could not be checked: " + error.Message);
        }
        finally
        {
            _streamerUpdaterChecking = false;
            if (!IsDisposed && !Disposing) UpdateStreamerUpdaterAvailability();
        }
    }

    private async Task ChangeStreamerUpdaterAsync(bool restore)
    {
        if (_previewOnly || IsDisposed || Disposing || _streamerUpdaterChecking || _streamerUpdaterChanging ||
            UtilityActionIsBusy()) return;
        if ((restore ? _streamerUpdaterState?.CanRestore : _streamerUpdaterState?.CanPause) != true) return;
        _streamerUpdaterChanging = _utilityActionRunning = true;
        string action = restore ? "Restore PC Streamer updater" : "Pause PC Streamer updater";
        try
        {
            UpdateControlState();
            _streamerUpdaterTitle.Text = action + "…";
            _streamerUpdaterTitle.ForeColor = Muted;
            AppendLog("[Windows Streamer updater] " + action + ". Windows may ask for administrator approval.");
            var status = restore
                ? await VirtualDesktopUpdaterPolicy.RestoreAsync(_root, _streamerUpdaterToken)
                : await VirtualDesktopUpdaterPolicy.PauseAsync(_root, _streamerUpdaterToken);
            if (IsDisposed || Disposing) return;
            PresentStreamerUpdater(status);
            bool completed = restore ? !status.CanRestore && !status.IsPaused : status.IsPaused;
            AppendLog("[Windows Streamer updater] " + status.Title + ": " + status.Detail);
            SetActionFeedback(status.Title, status.Detail,
                "Use First-time setup > Show optional tools and recovery > Show Windows Streamer updates to check or restore this PC setting.",
                severity: completed ? ActivitySeverity.Normal : ActivitySeverity.Warning);
        }
        catch (OperationCanceledException) when (_streamerUpdaterToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (IsDisposed || Disposing) return;
            _streamerUpdaterState = null;
            _streamerUpdaterTitle.Text = "Check Windows updater status before retrying";
            _streamerUpdaterTitle.ForeColor = Warning;
            _streamerUpdaterDetail.Text = error.Message;
            ShowWorkflowFailure(action, error);
        }
        finally
        {
            _streamerUpdaterChanging = _utilityActionRunning = false;
            if (!IsDisposed && !Disposing) UpdateControlState();
        }
    }
}
