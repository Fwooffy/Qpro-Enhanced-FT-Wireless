using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal sealed record HubActionFailure(string Title, string Detail, string NextStep)
{
    internal static HubActionFailure Explain(string action, string error, string output)
    {
        // An expected first-install package check must not become the diagnosis
        // of a later download failure. Final verification keeps its full errors.
        var evidence = error + "\n" + string.Join("\n", output.Split('\n').Where(line =>
            !line.StartsWith("WARNING: Existing ROCm packages need installation or repair:", StringComparison.Ordinal)));
        bool Has(string text) => evidence.Contains(text, StringComparison.OrdinalIgnoreCase);
        bool Matches(string pattern) => Regex.IsMatch(evidence, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (Has("unauthorized") || Has("debugging authorization"))
            return new("Headset permission is needed", "ADB has not been authorized by the headset.", "Put on the headset, allow USB debugging, then reconnect and retry.");
        if (Has("root access was not granted") || Has("Grant Magisk Superuser access") ||
            Has("could not verify Magisk root") || Has("Superuser permission denied"))
            return new("Headset root access is needed", "Qpro could not verify Magisk root permission.", "Open Magisk, allow Shell / ADB Shell Superuser access, and retry with the headset awake.");
        if (Has("Unsupported tracking-engine") || Has("validated eye profile") || Has("Controller ABI") || Has("HANDS_INCOMPATIBLE"))
            return new(action + " is not compatible with this version", "The exact runtime version or engine has not passed this feature's compatibility checks.", "Keep this feature off and copy diagnostics with the exact build. Other tracking features have their own checks.");
        // A repair instruction is not proof that the app is open. Module
        // preflight errors also say to close VRCFT before reinstalling.
        if (Has("packaged Qpro module is missing or unreadable"))
            return new("A required app file is missing", "The module supplied with this app could not be read.", "Extract the entire ready-to-run release ZIP into a new folder, keeping your models and captures, then retry.");
        if (Has("module was not found") || Has("module is not installed") || Has("module card needs repair") ||
            Has("selected module folder has no readable Qpro DLL") || Has("installed Qpro DLL differs") ||
            Has("module folder contains an unexpected DLL") || Has("only installed Qpro module") ||
            Has("alternate or legacy Qpro module slot") || Has("alternate or legacy module slot") ||
            Has("More than one Qpro DLL is installed") || Has("installed Qpro module uses"))
        {
            var source = Regex.Match(evidence, @"Qpro (Virtual Desktop|Steam Link)(?: VRCFaceTracking)? module", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var install = source.Success ? "Install " + source.Groups[1].Value + " module" : "install the module for your selected streaming app";
            return new("The Qpro module needs installation or repair", "The selected module is missing, mismatched or duplicated. Its folder and module card must match this app.",
                "Close VRCFaceTracking, use First-time setup to " + install + ", then reopen VRCFaceTracking. Keep the module recovery copies listed in Activity.");
        }
        if (Has("Close VRCFaceTracking and wait") || Has("VRCFaceTracking is still open") ||
            Has("VRCFaceTracking must be closed"))
            return new("VRCFaceTracking is still open", "Its loaded module cannot be replaced safely while the app or module helper is running.", "Close VRCFaceTracking and wait for its module helper to exit, then retry.");
        if (Has("saved-module recovery needs attention"))
            return new("Qpro removed; saved module needs attention", "The Qpro module was removed, but an official module backup could not be restored. Existing modules and recovery copies were preserved.", "Review the recovery path in Activity, then install or repair the official module through VRCFaceTracking before using that source.");
        if (Has("Controller removal failed"))
            return new("Controller removal needs attention", "Controller removal could not be completed. The Activity log states whether the original files were restored or kept in a recovery folder.", "Keep SteamVR closed. Review the recovery path in Activity and repair the controller add-on before using controller input.");
        if (Has("Close SteamVR before installing") || Has("SteamVR is still open") || Has("SteamVR must be closed"))
            return new("SteamVR is still open", "The controller add-on cannot be changed safely while SteamVR is running.", "Close SteamVR, wait for it to exit, then retry.");
        if (Has("WinError 206") || Has("filename or extension is too long") || Has("path-length failure"))
            return new("A Windows file path is too long", "Windows could not create a required library file at this location.", "Retry ROCm using a short Qpro-only storage folder. Existing captures and environments should be kept.");
        if (Has("DLL load failed") || Has("WinError 126") || Has("WinError 1114") || Has("Windows native dependency could not load"))
            return new("A required Windows library could not load", "The runtime import failed. This is not evidence that the headset or model is incompatible.", "Install or repair Microsoft's Visual C++ x64 runtime, then retry Install runtime. Copy diagnostics if it repeats.");
        var missingModule = Regex.Match(evidence, @"ModuleNotFoundError:\s*(?:No module named\s+)?['""]?(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (missingModule.Success)
        {
            var name = missingModule.Groups["name"].Value.Split('.')[0];
            if (name is "torch" or "torchgen" or "torchvision" or "torchaudio" or "cv2" or "numpy" or "frida")
            {
                var component = name == "frida" ? "Hand/controller components" :
                    action.Contains("ROCm", StringComparison.OrdinalIgnoreCase) || Has("AMD PyTorch") || Has("Selected AMD ROCm")
                        ? "AMD ROCm and PyTorch" : "PC runtime";
                return new("A runtime package needs installation or repair", "Python could not import the required package '" + name + "' from Qpro's selected environment.",
                    "Retry " + component + " setup in First-time setup. Keep your models and captures; copy diagnostics if the package still cannot be imported.");
            }
            return new("A Python module could not be imported", "Python could not find '" + name + "'. The complete Activity log identifies the script and environment that requested it.",
                "Extract the entire ready-to-run release ZIP into a new folder and retry Install runtime. Keep your models and captures, and copy diagnostics if it repeats.");
        }
        if (Has("script is missing") || Has("helper is missing") || Has("Missing Qpro script") || Has("Missing bundled"))
            return new("A required app file is missing", "Setup could not find a script or Python module required by this action.", "Extract the entire ready-to-run release ZIP into a new folder, keeping your models and captures, then retry.");
        if (Has("did not detect a supported discrete") || Has("No eligible discrete") || Has("target mismatch") || Has("discrete GPU check failed"))
            return new("The Radeon GPU was not verified", "The GPU check failed; the ROCm environment has not been marked ready.", "Update the AMD driver, confirm the discrete card's model, then retry. Copy diagnostics if it still fails; CPU tracking remains available.");
        if (Has("package/import verification failed before GPU detection") || Has("package dependency verification failed before GPU detection") ||
            Matches(@"No matching distribution found|Could not find a version that satisfies the requirement"))
            return new("The runtime packages could not be verified", "The package download, installation or import stopped before GPU compatibility could be checked.",
                "Check the package, index and download error in Activity, then retry the same setup action. This result does not establish that your GPU is unsupported.");
        if (Matches(@"(?:device|headset|Quest).*(?:offline|not found|unavailable)|no devices/emulators found"))
            return new("The headset connection is unavailable", "ADB could not reach the selected headset. This does not mean its firmware or tracking model is incompatible.",
                "Keep the headset awake. For USB, use Reconnect USB in First-time setup; for wireless, connect using the headset's current IP and port. Retry after it shows connected.");
        return new(action + " did not complete",
            "The action stopped before it could confirm success. The original error is in the Activity log below.",
            "Check the red error lines below. Copy diagnostics if you need help before retrying.");
    }
}

// A successful process exit is still unexpected if the user did not press
// Stop. Keep it distinct from an installation failure or normal cleanup.
internal sealed record HubTrackingExitFeedback(string Title, string Detail, string NextStep, bool IsError)
{
    internal static async Task<HubTrackingExitFeedback?> WaitForStartupAsync(
        Task<HubTrackingExitFeedback?> exitResult, CancellationToken cancellation)
    {
        try { return await exitResult.WaitAsync(TimeSpan.FromSeconds(6), cancellation); }
        catch (TimeoutException)
        {
            return new("Tracking startup could not be verified",
                "The tracking worker exited, but its final diagnostic output did not arrive within six seconds.",
                "Keep the Activity log and review the last worker error before retrying tracking.", true);
        }
    }

    internal static HubTrackingExitFeedback? Complete(string label, int exitCode, bool stopRequested,
        bool outputDrained, string output)
    {
        if (stopRequested) return null;
        if (exitCode == 0)
            return new(label + " stopped", "The tracking worker exited before Stop tracking was requested.",
                "Review the last Activity lines for the stop reason. Stop any other Qpro features before restarting tracking.", false);
        var failure = HubActionFailure.Explain(label, label + " exited with code " + exitCode + ".", output);
        return new(failure.Title, failure.Detail + (outputDrained ? string.Empty :
            " Some worker output did not finish; the diagnosis may be incomplete."), failure.NextStep, true);
    }
}

internal sealed class HubTrackingStartupException(HubTrackingExitFeedback feedback) : Exception(feedback.Detail)
{
    internal HubTrackingExitFeedback Feedback { get; } = feedback;
}
