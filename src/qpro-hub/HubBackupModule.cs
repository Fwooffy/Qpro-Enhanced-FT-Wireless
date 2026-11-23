namespace QproFaceTracking.Hub;

internal sealed partial class HubForm
{
    private const string BackupPackageMissing = "This copy of Companion does not include the backup module. Open the full package with VRCFaceTracking backup support, or select Direct VRChat.";

    private bool BackupModuleHostRunning() => !_previewOnly && VrcftModuleProcessRunning();

    // Called on a user check or before starting extras. Comparing assemblies and reading
    // the host heartbeat on each screen refresh would add work to ordinary tracking.
    private Task<string?> BackupModuleProblemAsync(bool checkSelectedFeatures = false, bool captureLabels = false)
    {
        if (_previewOnly) return Task.FromResult<string?>(null);
        var steam = _environment.SteamLinkSelected;
        var requiresEyes = checkSelectedFeatures && (_pupil.Checked || _gaze.Checked);
        var requiresFace = captureLabels || (checkSelectedFeatures && (_tongue.Checked || _individualCheekPuff.Checked || _individualCheekSuck.Checked));
        return Task.Run(() => InspectBackupModule(steam, requiresEyes, requiresFace));
    }

    private string? InspectBackupModule(bool steam, bool requiresEyes, bool requiresFace)
    {
        if (!HubModuleInstallation.PackageIncludesBackup(_root)) return BackupPackageMissing;
        var customLibs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCFaceTracking", "CustomLibs");
        var install = HubModuleInstallation.Inspect(customLibs, HubModuleInstallation.PackagedDll(_root), steam);
        if (!install.IsCurrent) return install.Detail;
        if (!BackupModuleHostRunning()) return "Open VRCFaceTracking and start the Qpro module for " + (steam ? "Steam Link." : "Virtual Desktop.");
        var dll = Path.Combine(install.ExpectedDirectory, steam ? "000-Qpro.SteamLink.dll" : "000-Qpro.VirtualDesktop.dll");
        var observation = HubModuleHeartbeat.Read(HubModuleHeartbeat.PathFor(steam), steam, dll);
        return HubModuleHeartbeat.BackupInputProblem(observation, Environment.TickCount64, requiresEyes, requiresFace);
    }

    private async Task InstallBackupModuleAsync()
    {
        if (!SetupCommandsAvailable || !BackupSelected || _faceRouteChanging || UtilityActionIsBusy()) return;
        if (!_previewOnly && !HubModuleInstallation.PackageIncludesBackup(_root))
        {
            SetActionFeedback("Backup module unavailable", BackupPackageMissing, "See First-time setup for output options.", true);
            return;
        }
        if (BackupModuleHostRunning())
        {
            SetActionFeedback("Close VRCFaceTracking before installing", "VRCFaceTracking or its module helper is open.",
                "Exit VRCFaceTracking, wait for its helper to close, then select Install Qpro backup module again.", true);
            return;
        }
        var source = _environment.TrackingSourceArgument;
        var label = _environment.SteamLinkSelected ? "Steam Link" : "Virtual Desktop";
        await RunSetupStepAsync("Install backup module", "install-vrcft-eye-bridge.ps1",
            $"The Qpro {label} backup module is installed.", "Open VRCFaceTracking, start its tracking and check the avatar.",
            "-TrackingSource", source);
    }
}
