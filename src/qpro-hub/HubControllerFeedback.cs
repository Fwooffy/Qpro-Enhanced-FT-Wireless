using System.Text.Json;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal enum ControllerFeedbackState { Checked, Waiting, Restored, NeedsAttention }

// Worker cleanup can arrive before the supervisor's final restoration report.
// Only that explicit final evidence makes a stopped/restored result successful.
internal sealed record HubControllerFeedback(
    ControllerFeedbackState State, string Title, string Detail, string NextStep, bool IsError)
{
    internal const string CheckPrefix = "HANDS_CHECK ";
    internal const string CleanupPrefix = "CONTROLLER_CLEANUP ";

    internal static bool TryParseCheckLine(string line, out HubControllerFeedback? feedback)
    {
        feedback = null;
        if (!line.StartsWith(CheckPrefix, StringComparison.Ordinal) || line.Length > 65536) return false;
        try
        {
            using var document = JsonDocument.Parse(line[CheckPrefix.Length..], new JsonDocumentOptions { MaxDepth = 8 });
            var result = document.RootElement;
            ReportJson.RequireKeys(result, "compatible", "androidVersion", "pcDriverSha256", "fridaVersion", "settings",
                "settingSource", "experimental", "runtimeValidated", "problems", "warnings", "componentsReady", "componentProblems");
            var compatible = ReportJson.Boolean(result.GetProperty("compatible"));
            var components = ReportJson.Boolean(result.GetProperty("componentsReady"));
            _ = ReportJson.Boolean(result.GetProperty("experimental"));
            _ = ReportJson.Boolean(result.GetProperty("runtimeValidated"));
            var version = ReportJson.NullableText(result.GetProperty("androidVersion"), 128);
            var hash = ReportJson.NullableText(result.GetProperty("pcDriverSha256"), 64);
            var fridaVersion = ReportJson.NullableText(result.GetProperty("fridaVersion"), 64);
            if (hash is not null && !Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$")) return false;
            if (ReportJson.Text(result.GetProperty("settingSource"), 64) != "current-user") return false;
            var settings = result.GetProperty("settings");
            ReportJson.RequireKeys(settings, "hand_tracking_enabled", "multimodal_hands_and_controllers_enabled", "simultaneous_hands_and_controllers_mode");
            foreach (var property in settings.EnumerateObject()) _ = ReportJson.NullableText(property.Value, 64);
            var problems = ReportJson.TextList(result.GetProperty("problems"), 32, 2048);
            var warnings = ReportJson.TextList(result.GetProperty("warnings"), 32, 2048);
            var componentProblems = ReportJson.TextList(result.GetProperty("componentProblems"), 32, 2048);
            if (compatible && problems.Length != 0 || components && componentProblems.Length != 0) return false;
            if (compatible && (version is null || hash is null ||
                settings.GetProperty("simultaneous_hands_and_controllers_mode").GetString() != "1" ||
                settings.EnumerateObject().Where(property => property.Name != "simultaneous_hands_and_controllers_mode")
                    .Any(property => property.Value.GetString()?.Trim().ToLowerInvariant() is not ("true" or "false" or "1" or "0")))) return false;
            if (components && fridaVersion is null) return false;
            bool blocked = !compatible || !components;
            var reasons = problems.Concat(componentProblems).Distinct().ToArray();
            var detail = blocked
                ? reasons.Length == 0 ? "The required hand/controller checks did not pass." : string.Join("\n", reasons)
                : $"Virtual Desktop headset version: {version ?? "not reported"}. Version and component checks passed; live optical input has not been verified." +
                    (warnings.Length == 0 ? string.Empty : "\n" + string.Join("\n", warnings));
            var next = !components ? "Close SteamVR and install the hand/controller components, reopen SteamVR through Virtual Desktop, then check again."
                : !compatible ? "Follow the reported compatibility detail and check again. Qpro will not use an unvalidated version or ABI."
                : "Start tracking with Hands + controllers selected, then wait for valid live finger input. A preliminary inactive setting is a warning, not proof that the menu switch is off.";
            feedback = new(ControllerFeedbackState.Checked, blocked ? "Hand/controller compatibility needs attention" : "Hand/controller prechecks passed",
                detail, next, blocked);
            return true;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { return false; }
    }

    internal static HubControllerFeedback ParseCleanupLine(string line)
    {
        static HubControllerFeedback Attention(string detail) => new(ControllerFeedbackState.NeedsAttention,
            "Controller restoration is unconfirmed", detail,
            "Keep the Hub open and check Activity before restarting. Copy the complete result if restoration cannot be confirmed.", true);
        if (!line.StartsWith(CleanupPrefix, StringComparison.Ordinal) || line.Length > 65536)
            return Attention("The controller cleanup report was missing or too large.");
        try
        {
            using var document = JsonDocument.Parse(line[CleanupPrefix.Length..], new JsonDocumentOptions { MaxDepth = 8 });
            var result = document.RootElement;
            if (result.ValueKind != JsonValueKind.Object) throw new FormatException("Object required.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in result.EnumerateObject())
                if (property.Name is not ("confirmed" or "restoration" or "reader" or "inputs" or "problems" or "workerPid" or "message") ||
                    !names.Add(property.Name)) throw new FormatException("Unknown or duplicate cleanup field.");
            bool? confirmed = result.TryGetProperty("confirmed", out var confirmation) ? ReportJson.Boolean(confirmation) : null;
            var restoration = result.TryGetProperty("restoration", out var restorationValue) ? ReportJson.Text(restorationValue, 64) : null;
            var reader = result.TryGetProperty("reader", out var readerValue) ? ReportJson.Text(readerValue, 64) : null;
            var inputs = result.TryGetProperty("inputs", out var inputValue) ? ReportJson.Text(inputValue, 64) : null;
            var message = result.TryGetProperty("message", out var messageValue) ? ReportJson.Text(messageValue, 4096, multiline: true) : null;
            if (result.TryGetProperty("workerPid", out var pid) && (!pid.TryGetInt32(out var workerPid) || workerPid <= 0))
                throw new FormatException("Invalid worker identifier.");
            var problems = result.TryGetProperty("problems", out var problemList) ? ReportJson.TextList(problemList, 32, 4096) : [];
            if (restoration is not (null or "confirmed" or "unconfirmed" or "still-running" or "stop-file-failed") ||
                reader is not (null or "stopped" or "unconfirmed") || inputs is not (null or "disabled"))
                throw new FormatException("Unknown cleanup state.");
            if (confirmed == false || restoration is "unconfirmed" or "stop-file-failed" || reader == "unconfirmed" || problems.Length > 0)
                return Attention(problems.Length > 0 ? string.Join("\n", problems) : message ?? "A controller worker could not confirm its restoration.");
            if (confirmed == true && restoration == "confirmed" && reader == "stopped" && result.TryGetProperty("problems", out _))
                return new(ControllerFeedbackState.Restored, "Controller input stopped and restored",
                    "The supervisor confirmed that its workers stopped and their recorded changes were restored.",
                    "Reopen SteamVR after uninstalling the controller add-on to reload the normal controller profile.", false);
            if (restoration == "still-running" || confirmed is null && reader == "stopped" && inputs == "disabled")
                return new(ControllerFeedbackState.Waiting, "Waiting for controller restoration",
                    "A worker has reported progress; final restoration is not confirmed yet.", "Keep the Hub open until the final cleanup result arrives.", false);
            return Attention("The controller cleanup report did not include complete final restoration evidence.");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        { return Attention("The controller cleanup report was malformed. Check the detailed Activity output."); }
    }
}

// Stdout and stderr callbacks can run concurrently. Keep their evidence separate
// from UI dispatch so a successful exit cannot race an incomplete report.
internal sealed class HubControllerUtilityResult
{
    private readonly object _sync = new();
    private int _checkCount;
    private HubControllerFeedback? _check;
    private HubControllerFeedback? _cleanup;
    private HubControllerFeedback? _failure;

    internal void Observe(string line)
    {
        lock (_sync)
        {
            if (line.StartsWith(HubControllerFeedback.CheckPrefix, StringComparison.Ordinal))
            {
                _checkCount++;
                if (!HubControllerFeedback.TryParseCheckLine(line, out var result))
                    _failure ??= Incomplete("The compatibility report was incomplete or malformed.");
                else
                {
                    _check = result;
                    if (result!.IsError) _failure ??= result;
                }
                if (_checkCount > 1)
                    _failure ??= Incomplete("The helper emitted more than one compatibility report. Its result is ambiguous.");
            }
            else if (line.StartsWith(HubControllerFeedback.CleanupPrefix, StringComparison.Ordinal))
            {
                _cleanup = HubControllerFeedback.ParseCleanupLine(line);
                if (_cleanup.IsError) _failure ??= _cleanup;
            }
        }
    }

    internal HubControllerFeedback? Complete(bool requireCheck, int exitCode)
    {
        lock (_sync)
        {
            if (_failure is not null) return _failure;
            if (_cleanup?.State == ControllerFeedbackState.Waiting)
                return new(ControllerFeedbackState.NeedsAttention, "Controller restoration is unconfirmed",
                    "The helper exited while its last cleanup report was still waiting for restoration.",
                    "Keep the Hub open and copy the complete Activity log before restarting controller tracking.", true);
            // A valid check does not turn a failed helper into a successful one.
            if (exitCode != 0) return null;
            if (requireCheck && (_checkCount != 1 || _check is null))
                return Incomplete("The helper did not emit one complete hand/controller compatibility report. Its exit code alone cannot verify support.");
            return _cleanup ?? (requireCheck ? _check : null);
        }
    }

    private static HubControllerFeedback Incomplete(string detail) => new(ControllerFeedbackState.NeedsAttention,
        "Hand/controller compatibility could not be verified", detail,
        "Check Activity and copy the complete report before trying an unverified runtime.", true);
}
