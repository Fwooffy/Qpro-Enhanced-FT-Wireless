namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private HubComponentPlan? _componentPlan;
    private Action<string>? _componentUpdateOutput;

    private HubComponentPlan InspectComponentUpdates() => HubComponentUpdates.Inspect(_root,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCFaceTracking", "CustomLibs"),
        _environment.SteamLinkSelected,
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QPRO_PYTHON")),
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QPRO_ROCM_HOME")));

    private async Task CheckComponentUpdatesAsync()
    {
        if (_previewOnly || IsDisposed || _closingInProgress) return;
        try
        {
            var plan = await Task.Run(InspectComponentUpdates);
            if (IsDisposed || Disposing) return;
            _componentPlan = plan;
            _updateLink.Text = _updateCheck?.Release is not null ? "Update available" : plan.Updates.Count > 0 ? "Component updates" : "Check updates";
            if (plan.Updates.Count > 0)
            {
                AppendLog($"[Updates] {plan.Updates.Count} installed component(s) have an update or need verification for this release. Open Updates, then Components. Nothing has been installed.");
            }
        }
        catch (Exception error) { if (!IsDisposed) AppendLog("[Updates] Component status could not be checked: " + error.Message); }
    }

    private async Task ShowComponentUpdatesAsync()
    {
        if (_previewOnly || UtilityActionIsBusy()) return;
        try
        {
            var plan = await Task.Run(InspectComponentUpdates);
            if (IsDisposed || Disposing || UtilityActionIsBusy()) return;
            using var dialog = new HubComponentUpdateDialog(plan, ApplyComponentUpdateAsync);
            dialog.ShowDialog(this);
            await CheckComponentUpdatesAsync();
        }
        catch (Exception error) { ShowWorkflowFailure("Component updates", error); }
    }

    private async Task<string?> ApplyComponentUpdateAsync(HubComponentUpdate item, Action<string> progress)
    {
        if (_previewOnly) return "Preview mode does not install components.";
        if (_closingInProgress || UtilityActionIsBusy()) return "Finish the current Qpro action first.";
        if (LiveTrackingRunning) return "Stop live tracking before updating components.";
        if (item.Kind == HubComponentKind.Module && VrcftModuleProcessRunning())
            return "Close VRCFaceTracking and wait for its ModuleProcess helper to exit, then retry.";
        // The plan is only a preview. Re-read environment ownership and recipes
        // immediately before launching a helper; the user may have changed them.
        var current = InspectComponentUpdates().Updates.FirstOrDefault(candidate => candidate.Kind == item.Kind);
        if (current is null)
        {
            bool alreadyCurrent = item.Kind == HubComponentKind.Module
                ? _environment.SteamLinkSelected == item.SteamLink && CurrentBridgeInstalled()
                : HubComponentUpdates.RuntimeUpdateVerified(item, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            return alreadyCurrent ? null : "The component is no longer available for this update. Reopen Component updates to check its status.";
        }
        if (current.Recipe != item.Recipe || current.SteamLink != item.SteamLink ||
            current.EnvironmentRoot != item.EnvironmentRoot)
            return "The installed component changed. Reopen Component updates to review the new plan.";
        var args = new List<string>();
        string script;
        if (item.Kind == HubComponentKind.Module)
        {
            script = "install-vrcft-eye-bridge.ps1";
            args.AddRange(["-TrackingSource", item.SteamLink ? "SteamLink" : "VirtualDesktop"]);
        }
        else if (item.Kind == HubComponentKind.Runtime)
        { script = "setup-runtime.ps1"; args.Add("-Update"); }
        else
        {
            script = "Install-QproRocm.ps1";
            if (!AmdInstallEligible) return "No eligible discrete AMD GPU is detected. Use First-time setup to check compatibility.";
            bool legacy = item.Kind == HubComponentKind.LegacyRocm;
            if (!HubRocmOsPolicy.CanInstall(Environment.OSVersion.Version.Build, _experimentalWindows10Rocm.Checked, legacy))
                return "This Windows version requires the compatible ROCm option in First-time setup.";
            args.AddRange(["-Update", "-RocmStorageRoot", Path.GetDirectoryName(item.EnvironmentRoot!)!]);
            if (legacy) args.Add("-UseLegacyRocm");
            args.AddRange(HubRocmOsPolicy.InstallArguments(Environment.OSVersion.Version.Build, _experimentalWindows10Rocm.Checked));
        }
        string label = "Update " + item.Name;
        if (!TryBeginSetupProgress(label)) return "Finish the current Qpro action first.";
        bool succeeded = false;
        string FailedVerification(string message)
        {
            AppendLog("[Updates] " + item.Name + " verification failed: " + message);
            SetActionFeedback(item.Name + " needs attention", message, "Open Component updates again after checking the detailed log.", true);
            return message;
        }
        try
        {
            _componentUpdateOutput = progress;
            if (!await RunUtilityAsync(label, script, args.ToArray())) return "The helper did not confirm success. See Activity for the exact error.";
            if (item.Kind == HubComponentKind.Module)
            {
                _environment.ReloadTrackingSource();
                SyncTrackingSourceControls(); UpdateTrackingSourceNotes();
                if (_environment.SteamLinkSelected != item.SteamLink || !CurrentBridgeInstalled())
                    return FailedVerification("The installed module did not match the selected update. Retry with VRCFaceTracking closed.");
            }
            else if (!HubComponentUpdates.RuntimeUpdateVerified(item, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
                return FailedVerification("The helper exited, but the release recipe and readiness receipt could not be verified. Check Activity and retry.");
            succeeded = true;
            AppendLog("[Updates] " + item.Name + " updated and verified.");
            return null;
        }
        catch (Exception error) { return FailedVerification(error.Message); }
        finally { _componentUpdateOutput = null; FinishSetupProgress(succeeded, label); }
    }
}
