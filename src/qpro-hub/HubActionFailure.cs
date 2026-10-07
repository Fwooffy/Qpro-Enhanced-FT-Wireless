namespace QproFaceTracking.Hub;

internal sealed record HubActionFailure(string Title, string Detail, string NextStep)
{
    internal static HubActionFailure Explain(string action, string error, string output)
    {
        var evidence = error + "\n" + output;
        bool Has(string text) => evidence.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Has("unauthorized") || Has("debugging authorization"))
            return new("Headset permission is needed", "ADB has not been authorized by the headset.", "Put on the headset, allow USB debugging, then reconnect and retry.");
        if (Has("root access was not granted") || Has("Grant Magisk Superuser access") ||
            Has("could not verify Magisk root") || Has("Superuser permission denied"))
            return new("Headset root access is needed", "Qpro could not verify Magisk root permission.", "Open Magisk, allow Shell / ADB Shell Superuser access, and retry with the headset awake.");
        if (Has("Unsupported tracking-engine") || Has("validated eye profile") || Has("Controller ABI") || Has("HANDS_INCOMPATIBLE"))
            return new(action + " is not compatible with this version", "The exact runtime version or engine has not passed this feature's compatibility checks.", "Keep this feature off and copy diagnostics with the exact build. Other tracking features have their own checks.");
        if (Has("Close VRCFaceTracking"))
            return new("VRCFaceTracking is still open", "Its loaded module cannot be replaced safely while the app or module helper is running.", "Close VRCFaceTracking and wait for its module helper to exit, then retry.");
        if (Has("saved-module recovery needs attention"))
            return new("Qpro removed; saved module needs attention", "The Qpro module was removed, but an official module backup could not be restored. Existing modules and recovery copies were preserved.", "Review the recovery path in Activity, then install or repair the official module through VRCFaceTracking before using that source.");
        if (Has("Controller removal failed"))
            return new("Controller removal needs attention", "Controller removal could not be completed. The Activity log states whether the original files were restored or kept in a recovery folder.", "Keep SteamVR closed. Review the recovery path in Activity and repair the controller add-on before using controller input.");
        if (Has("Close SteamVR"))
            return new("SteamVR is still open", "The controller add-on cannot be changed safely while SteamVR is running.", "Close SteamVR, wait for it to exit, then retry.");
        if (Has("WinError 206") || Has("filename or extension is too long") || Has("path-length failure"))
            return new("A Windows file path is too long", "Windows could not create a required library file at this location.", "Retry ROCm using a short Qpro-only storage folder. Existing captures and environments should be kept.");
        if (Has("DLL load failed") || Has("WinError 126") || Has("WinError 1114"))
            return new("A required Windows library could not load", "The runtime import failed. This is not evidence that the headset or model is incompatible.", "Install or repair Microsoft's Visual C++ x64 runtime, then retry Install runtime. Copy diagnostics if it repeats.");
        if (Has("ModuleNotFoundError") || Has("script is missing") || Has("helper is missing"))
            return new("A required app file is missing", "Setup could not find a script or Python module required by this action.", "Extract the entire ready-to-run release ZIP into a new folder, keeping your models and captures, then retry.");
        if (Has("did not detect a supported discrete") || Has("No eligible discrete") || Has("target mismatch"))
            return new("The Radeon GPU was not verified", "The GPU check failed; the ROCm environment has not been marked ready.", "Update the AMD driver, confirm the discrete card's model, then retry. Copy diagnostics if it still fails; CPU tracking remains available.");
        return new(action + " did not complete",
            "The action stopped before it could confirm success. The original error is in the Activity log below.",
            "Check the red error lines below. Copy diagnostics if you need help before retrying.");
    }
}
