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
    private HubUpdateCheck? _updateCheck;
    private bool _updateChecking;
    private bool _updateAutomatically = true;
    private static string UpdateStorage => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "updates");
    private static string UpdatePreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking", "config", "updates.json");
    private static Version AppVersion => typeof(HubForm).Assembly.GetName().Version is { } value
        ? new Version(value.Major, value.Minor, value.Build) : new Version(2, 1, 2);

    private void WireUpdates()
    {
        _updateLink.LinkClicked += async (_, _) => await HandleHubUpdateClickAsync();
        if (_previewOnly) return;
        try
        {
            if (File.Exists(UpdatePreferencePath) && new FileInfo(UpdatePreferencePath).Length <= 4096)
                _updateAutomatically = JsonSerializer.Deserialize<UpdatePreference>(File.ReadAllText(UpdatePreferencePath))?.CheckAutomatically ?? true;
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
                await Task.Delay(1000, _updateLifetime.Token);
                await CheckHubUpdatesAsync();
            }
            catch (OperationCanceledException) { }
        };
        FormClosed += (_, _) => _updateLifetime.Cancel();
    }

    private async Task CheckHubUpdatesAsync()
    {
        if (_previewOnly || _updateChecking || IsDisposed || _closingInProgress) return;
        _updateChecking = true;
        _updateLink.Enabled = false;
        _updateLink.Text = "Checking…";
        try
        {
            using var client = new HubUpdateClient();
            var result = await client.CheckAsync(AppVersion, _updateLifetime.Token);
            if (IsDisposed || Disposing) return;
            _updateCheck = result;
            _updateLink.Text = result.Release is not null ? "Update available" : _componentPlan?.Updates.Count > 0 ? "Component updates" : "Check updates";
            _updateLink.AccessibleDescription = result.Message;
            AppendLog("[Updates] " + result.Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing)
            {
                _updateCheck = new(HubUpdateCheckState.Unavailable, "Updates could not be checked: " + error.Message);
                _updateLink.Text = _componentPlan?.Updates.Count > 0 ? "Component updates" : "Check updates";
                AppendLog("[Updates] " + _updateCheck.Message);
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
        if (_previewOnly || _updateChecking || _closingInProgress) return;
        if (_updateCheck?.Release is null) await CheckHubUpdatesAsync();
        if (IsDisposed || Disposing || _updateCheck is null) return;
        // An update never interrupts a capture, download or tracking recovery.
        if (UtilityActionIsBusy()) return;
        var live = _starting || _stopping || LiveTrackingRunning;
        var packaged = HubUpdateRunner.IsPackagedInstall(_root);
        string? blocked = live ? "Stop tracking before installing an update. You can read the release notes now."
            : !packaged ? "This is a source or preview build. Download the release ZIP from GitHub to update."
            : null;
        using var dialog = new HubUpdateDialog(_updateCheck, AppVersion, _updateAutomatically, blocked,
            async (release, progress, cancellation) =>
            {
                using var client = new HubUpdateClient();
                return await client.StageAsync(release, _root, UpdateStorage, progress, cancellation);
            });
        var result = dialog.ShowDialog(this);
        _updateAutomatically = dialog.CheckAutomatically;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UpdatePreferencePath)!);
            var temporary = UpdatePreferencePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new UpdatePreference(_updateAutomatically)));
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

    private sealed record UpdatePreference(bool CheckAutomatically);
}
