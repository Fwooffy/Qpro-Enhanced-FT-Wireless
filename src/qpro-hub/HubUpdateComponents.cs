namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private HubComponentPlan? _componentPlan;
    private Action<string>? _componentUpdateOutput;

    private HubComponentPlan InspectComponentUpdates() => HubComponentUpdates.Inspect(_root,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
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
            RefreshUpdateLink();
            if (plan.Updates.Count > 0)
            {
                AppendLog($"[Updates] {plan.Updates.Count} installed component(s) have an update or need verification for this release. Open Updates, then Components. Nothing has been installed.");
            }
        }
        catch (Exception error) { if (!IsDisposed) AppendLog("[Updates] Component status could not be checked: " + error.Message); }
    }

    private async Task<bool> ShowComponentUpdatesAsync(bool offerAppUpdates = false)
    {
        if (_previewOnly || _updateDialogOpen || HubUpdateReviewIsBusy()) return false;
        _updateDialogOpen = true;
        try
        {
            var plan = await Task.Run(InspectComponentUpdates);
            if (IsDisposed || Disposing || HubUpdateReviewIsBusy()) return false;
            using var dialog = new HubComponentUpdateDialog(plan, ApplyComponentUpdateAsync, offerAppUpdates,
                _starting || _stopping || LiveTrackingRunning);
            bool appUpdates = dialog.ShowDialog(this) == DialogResult.Retry;
            await CheckComponentUpdatesAsync();
            return appUpdates;
        }
        catch (Exception error) { if (!IsDisposed && !Disposing) ShowWorkflowFailure("Component updates", error); return false; }
        finally { _updateDialogOpen = false; }
    }

    private async Task<string?> ApplyComponentUpdateAsync(HubComponentUpdate item, Action<string> progress)
    {
        if (_previewOnly) return "Preview mode does not install components.";
        if (_closingInProgress || UtilityActionIsBusy()) return "Finish the current Qpro action first.";
        if (LiveTrackingRunning) return "Stop live tracking before updating components.";
        // The plan is only a preview. Re-read environment ownership and recipes
        // immediately before launching a helper; the user may have changed them.
        var current = InspectComponentUpdates().Updates.FirstOrDefault(candidate => candidate.Kind == item.Kind);
        if (current is null)
        {
            bool alreadyCurrent = HubComponentUpdates.RuntimeUpdateVerified(item, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            return alreadyCurrent ? null : "The component is no longer available for this update. Reopen Component updates to check its status.";
        }
        if (current.Recipe != item.Recipe || current.EnvironmentRoot != item.EnvironmentRoot)
            return "The installed component changed. Reopen Component updates to review the new plan.";
        var args = new List<string>();
        string script;
        if (item.Kind == HubComponentKind.Runtime)
        { script = "setup-runtime.ps1"; args.Add("-Update"); }
        else
        {
            script = "Install-QproRocm.ps1";
            if (!AmdInstallEligible) return "No eligible discrete AMD GPU is detected. Use First-time setup to check compatibility.";
            bool legacy = item.Kind == HubComponentKind.LegacyRocm;
            if (!HubRocmOsPolicy.CanInstall(Environment.OSVersion.Version.Build, _experimentalWindows10Rocm.Checked, legacy))
                return "This Windows version requires the compatible ROCm option in First-time setup.";
            args.AddRange(["-Update", "-RocmStorageRoot", Path.GetDirectoryName(item.EnvironmentRoot)!]);
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
            if (!HubComponentUpdates.RuntimeUpdateVerified(item, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
                return FailedVerification("The helper exited, but the release recipe and readiness receipt could not be verified. Check Activity and retry.");
            succeeded = true;
            AppendLog("[Updates] " + item.Name + " updated and verified.");
            return null;
        }
        catch (Exception error) { return FailedVerification(error.Message); }
        finally { _componentUpdateOutput = null; FinishSetupProgress(succeeded, label); }
    }
}
