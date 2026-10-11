namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private readonly Label _healthModule = Info("Module output: unverified");
    private readonly Label _healthModuleDetail = Info("Module input and output are checked while VRCFaceTracking is running.");
    private HubModuleHeartbeatObservation _moduleHeartbeat = HubModuleHeartbeatObservation.Missing("No module observation yet.");
    private bool _heartbeatHostRunning;
    private bool _heartbeatModuleCurrent;
    private bool _heartbeatRefreshBusy;
    private string? _heartbeatSource;
    private readonly HubModuleHeartbeatHistory _heartbeatHistory = new();

    private async Task RefreshModuleHeartbeatAsync(bool hostRunning, HubModuleInspection module)
    {
        if (_previewOnly || _closingInProgress || IsDisposed || Disposing || _heartbeatRefreshBusy) return;
        var source = _environment.TrackingSourceArgument;
        _heartbeatHostRunning = hostRunning;
        _heartbeatModuleCurrent = module.IsCurrent;
        if (_heartbeatSource != source)
        {
            _moduleHeartbeat = HubModuleHeartbeatObservation.Missing("Waiting for the selected source's module observation.");
            _heartbeatSource = source;
            _heartbeatHistory.Reset();
        }
        _heartbeatRefreshBusy = true;
        try
        {
            var steamLink = source == "SteamLink";
            var observation = hostRunning && module.IsCurrent
                ? await Task.Run(() => HubModuleHeartbeat.Read(HubModuleHeartbeat.PathFor(steamLink), steamLink,
                    Path.Combine(module.ExpectedDirectory, steamLink ? "000-Qpro.SteamLink.dll" : "000-Qpro.VirtualDesktop.dll")))
                : HubModuleHeartbeatObservation.Missing("The selected module is not running or not current.");
            if (_closingInProgress || IsDisposed || Disposing || _environment.TrackingSourceArgument != source) return;
            _moduleHeartbeat = _heartbeatHistory.Accept(observation);
            RefreshModuleHeartbeatDecoration();
        }
        finally { _heartbeatRefreshBusy = false; }
    }

    private HubModuleHeartbeatView ModuleHeartbeatView()
    {
        if (_heartbeatSource != _environment.TrackingSourceArgument)
            return new("unverified", "Module output: source changed · awaiting load", "Restart VRCFaceTracking after changing the streaming source.");
        return HubModuleHeartbeat.Describe(_moduleHeartbeat, Environment.TickCount64, _heartbeatHostRunning, _heartbeatModuleCurrent);
    }

    private void RefreshModuleHeartbeatDecoration()
    {
        if (IsDisposed || Disposing || _interactiveResize || WindowState == FormWindowState.Minimized || _trackingHealthCard?.Visible != true) return;
        var view = ModuleHeartbeatView();
        var color = view.State == "receiving" ? Good : view.State is "closed" or "loaded" or "observed" ? Muted : Warning;
        if (_healthModule.Text != view.Summary) _healthModule.Text = view.Summary;
        if (_healthModule.ForeColor != color) _healthModule.ForeColor = color;
        if (_healthModule.AccessibleDescription != view.Detail) _healthModule.AccessibleDescription = view.Detail;
        if (_healthModuleDetail.Visible && _healthModuleDetail.Text != view.Detail) _healthModuleDetail.Text = view.Detail;
    }
}
