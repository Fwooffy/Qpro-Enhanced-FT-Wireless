namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private Task? _steamVrStartupChange;

    private void ConfigureSteamVrStartup()
    {
        if (_previewOnly) return;
        if (Environment.ProcessPath is not { } exe || !HubUpdateRunner.IsPackagedInstall(_root))
        {
            _launchWithSteamVr.Enabled = false;
            _steamVrStartupStatus.Text = "Open Companion from its extracted app folder to set up SteamVR launch.";
            return;
        }
        BindSteamVrStartup(() => HubSteamVrLaunch.GetStatus(exe), enabled => HubSteamVrLaunch.SetEnabled(exe, enabled));
    }

    internal void BindSteamVrStartup(Func<SteamVrLaunchStatus> read, Func<bool, SteamVrLaunchStatus> write)
    {
        bool busy = false, lastChecked = false;
        async Task ApplyAsync(bool? requested)
        {
            if (busy || _closingInProgress || IsDisposed || Disposing) return;
            busy = true;
            _launchWithSteamVr.Enabled = false;
            if (requested.HasValue) _steamVrStartupStatus.Text = "Updating SteamVR startup settings…";
            try
            {
                var result = await Task.Run(() => requested.HasValue ? write(requested.Value) : read());
                if (IsDisposed || Disposing) return;
                lastChecked = result.Enabled && result.ThisCopy;
                _launchWithSteamVr.Checked = lastChecked;
                _steamVrStartupStatus.Text = result.Text;
                if (requested.HasValue)
                {
                    bool applied = requested.Value == lastChecked;
                    _steamVrStartupStatus.ForeColor = applied ? Good : Warning;
                    AppendLog((applied ? "" : "WARNING: ") + "[SteamVR startup] " + result.Text);
                }
                else _steamVrStartupStatus.ForeColor = Muted;
            }
            catch (Exception error)
            {
                if (IsDisposed || Disposing) return;
                _launchWithSteamVr.Checked = lastChecked;
                _steamVrStartupStatus.Text = error.Message;
                _steamVrStartupStatus.ForeColor = Warning;
                if (requested.HasValue) AppendLog("WARNING: [SteamVR startup] " + error.Message);
            }
            finally
            {
                busy = false;
                if (!IsDisposed && !Disposing)
                {
                    UpdateToggleStyle(_launchWithSteamVr);
                    _launchWithSteamVr.Enabled = !_closingInProgress;
                }
            }
        }
        _launchWithSteamVr.Enabled = false;
        Shown += async (_, _) => await ApplyAsync(null);
        // SteamVR also exposes the preference. Re-read it when returning from that window.
        Activated += async (_, _) => { if (_launchWithSteamVr.Visible) await ApplyAsync(null); };
        _launchWithSteamVr.Click += async (_, _) =>
        {
            _steamVrStartupChange = ApplyAsync(_launchWithSteamVr.Checked);
            await _steamVrStartupChange;
        };
    }
}
