using System.Diagnostics;
using System.Text.Json;

namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private readonly LinkLabel _updateLink = new()
    {
        Text = "Check updates", LinkColor = Accent, ActiveLinkColor = Color.White,
        VisitedLinkColor = Accent, DisabledLinkColor = DisabledText,
        AccessibleName = "Qpro app updates", TabStop = true,
    };
    private readonly CancellationTokenSource _updateLifetime = new();
    private CancellationToken _updateToken;
    private bool _updateLifetimeReleased;
    private HubUpdateCheck? _updateCheck;
    private bool _updateChecking;
    private bool _updateAutomatically = true;
    private bool _updateIncludePrereleases;
    private static string UpdateStorage => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "updates");
    private static string UpdatePreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "config", "updates.json");
    private static Version AppVersion => typeof(HubForm).Assembly.GetName().Version is { } value
        ? new Version(value.Major, value.Minor, value.Build) : new Version(3, 0, 0);

    private void WireUpdates()
    {
        // Async continuations may run after the window is disposed. Retain the
        // token so they never access a disposed CancellationTokenSource.
        _updateToken = _updateLifetime.Token;
        Disposed += (_, _) => EndUpdateLifetime(releaseSource: true);
        _updateLink.LinkClicked += async (_, _) => await HandleHubUpdateClickAsync();
        WireUpdateNotifications();
        if (_previewOnly) return;
        try
        {
            if (File.Exists(UpdatePreferencePath) && new FileInfo(UpdatePreferencePath).Length <= 4096)
            {
                var preference = JsonSerializer.Deserialize<UpdatePreference>(File.ReadAllText(UpdatePreferencePath));
                _updateAutomatically = preference?.CheckAutomatically ?? true;
                _updateIncludePrereleases = preference?.IncludePrereleases ?? false;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { AppendLog("[Updates] Could not read the update preference: " + error.Message); }
        Shown += async (_, _) =>
        {
            try { await Task.Run(() => HubUpdateRunner.CleanupCompletedHelpers(UpdateStorage)); }
            catch (Exception error)
            { if (!IsDisposed) AppendLog("[Updates] Temporary update cleanup needs attention: " + error.Message); }
            if (!_updateAutomatically || IsDisposed) return;
            await CheckComponentUpdatesAsync();
            try
            {
                await Task.Delay(1000, _updateToken);
                if (!_updateAutomatically) return;
                await CheckHubUpdatesAsync(notifyAvailability: true);
            }
            catch (OperationCanceledException) { }
        };
        FormClosed += (_, _) => EndUpdateLifetime(releaseSource: false);
    }

    private void EndUpdateLifetime(bool releaseSource)
    {
        lock (_updateLifetime)
        {
            if (_updateLifetimeReleased) return;
            if (releaseSource) _updateLifetimeReleased = true;
            try { _updateLifetime.Cancel(); }
            finally { if (releaseSource) _updateLifetime.Dispose(); }
        }
    }

    private async Task CheckHubUpdatesAsync(bool notifyAvailability = false)
    {
        if (_previewOnly || _updateChecking || _updateDialogOpen || _updateNotificationOpen || IsDisposed || _closingInProgress) return;
        _updateChecking = true;
        _updateLink.Enabled = false;
        _updateLink.Text = "Checking…";
        try
        {
            using var client = new HubUpdateClient();
            var result = await client.CheckAsync(AppVersion, _updateToken, _updateIncludePrereleases);
            if (IsDisposed || Disposing) return;
            _updateCheck = result;
            AppendLog("[Updates] " + result.Message);
            if (notifyAvailability) QueueUpdateNotification(result);
            else _updateNotification.Observe(result, AppVersion, _updateIncludePrereleases);
            RefreshUpdateLink();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing)
            {
                _updateCheck = new(HubUpdateCheckState.Unavailable, "Updates could not be checked: " + error.Message);
                AppendLog("[Updates] " + _updateCheck.Message);
                if (notifyAvailability) QueueUpdateNotification(_updateCheck);
                else _updateNotification.Observe(_updateCheck, AppVersion, _updateIncludePrereleases);
                RefreshUpdateLink();
            }
        }
        finally
        {
            _updateChecking = false;
            if (!IsDisposed && !Disposing) _updateLink.Enabled = true;
        }
    }

    private async Task HandleHubUpdateClickAsync()
    {
        if (_previewOnly || _updateChecking || _closingInProgress || _updateDialogOpen || _updateNotificationOpen) return;
        var reviewCheck = UpdateCheckForReview();
        if (reviewCheck?.Release is null)
        {
            await CheckHubUpdatesAsync();
            reviewCheck = UpdateCheckForReview();
        }
        if (IsDisposed || Disposing || reviewCheck is null) return;
        // An update never interrupts a capture, download or tracking recovery.
        if (UtilityActionIsBusy()) return;
        var live = _starting || _stopping || LiveTrackingRunning;
        var packaged = HubUpdateRunner.IsPackagedInstall(_root);
        string? blocked = HubUpdateBlockReason(live, packaged, _root);
        using var dialog = new HubUpdateDialog(reviewCheck, AppVersion, _updateAutomatically, blocked,
            async (release, progress, cancellation) =>
            {
                using var client = new HubUpdateClient();
                return await client.StageAsync(release, _root, UpdateStorage, progress, cancellation);
            }, _updateIncludePrereleases, async (includePrereleases, cancellation) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _updateToken);
                using var client = new HubUpdateClient();
                var check = await client.CheckAsync(AppVersion, linked.Token, includePrereleases);
                if (!IsDisposed && !Disposing && !linked.IsCancellationRequested) AppendLog("[Updates] " + check.Message);
                return check;
            });
        if (reviewCheck.Release is { } reviewed) _updateNotification.Acknowledge(reviewed);
        _updateDialogOpen = true;
        DialogResult result;
        try { result = dialog.ShowDialog(this); }
        finally { _updateDialogOpen = false; }
        _updateAutomatically = dialog.CheckAutomatically;
        _updateIncludePrereleases = dialog.IncludePrereleases;
        _updateCheck = dialog.Check;
        if (_updateCheck is not null) _updateNotification.Observe(_updateCheck, AppVersion, _updateIncludePrereleases);
        else _updateNotification.RestrictChannel(_updateIncludePrereleases);
        if (_updateCheck?.Release is { } dialogRelease) _updateNotification.Acknowledge(dialogRelease);
        if (!IsDisposed && !Disposing)
        {
            RefreshUpdateLink();
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UpdatePreferencePath)!);
            var temporary = UpdatePreferencePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new UpdatePreference(_updateAutomatically, _updateIncludePrereleases)));
                File.Move(temporary, UpdatePreferencePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { AppendLog("[Updates] Could not save the update preference: " + error.Message); }
        if (dialog.OpenComponents)
        {
            if (dialog.StagedUpdate is { } discarded) await DiscardHubUpdateAsync(discarded);
            await ShowComponentUpdatesAsync();
            return;
        }
        if (dialog.StagedUpdate is not { } staged) return;
        if (result != DialogResult.OK || dialog.Release is not { } release)
        {
            await DiscardHubUpdateAsync(staged);
            return;
        }
        bool helperStarted = false;
        try
        {
            SaveLiveOptions();
            Enabled = false;
            _utilityActionRunning = true;
            _updateLink.Text = "Preparing restart…";
            await Task.Run(() => HubUpdateRunner.StartHelper(staged, _root, release.Version));
            helperStarted = true;
            AppendLog("[Updates] Verified update ready. Closing the Hub to replace app files in this folder; models and settings are kept.");
        }
        catch (Exception error)
        {
            await DiscardHubUpdateAsync(staged);
            MessageBox.Show(this, "The update could not start. Your current app is unchanged.\n\n" + error.Message,
                "Update needs attention", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _utilityActionRunning = false;
            if (!IsDisposed) { Enabled = true; _updateLink.Text = "Update available"; }
        }
        if (helperStarted) Close();
    }

    private async Task DiscardHubUpdateAsync(HubStagedUpdate staged)
    {
        try { await Task.Run(() => HubUpdatePackage.Discard(staged, UpdateStorage)); }
        catch (Exception error)
        { if (!IsDisposed) AppendLog("[Updates] Temporary update files could not be removed: " + error.Message); }
    }

    private static string? HubUpdateBlockReason(bool live, bool packaged, string runtimeRoot)
    {
        if (live) return "Stop tracking before installing an update. You can read the release notes now.";
        if (!packaged) return "This is a source or preview build. Download the release ZIP from GitHub to update.";
        try { HubUpdatePackage.ValidateInstallPath(runtimeRoot); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "This app folder is linked or cannot be checked for an in-place update. Download the release ZIP from GitHub, " +
                "or move the app to a regular local folder before updating. Your models and settings remain available.";
        }
        return null;
    }

    private sealed record UpdatePreference(bool CheckAutomatically, bool IncludePrereleases = false);
}
