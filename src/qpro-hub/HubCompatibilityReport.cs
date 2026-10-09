using System.Text.Json;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

// This report explains a read-only check. It never authorizes a native patch:
// preparation and startup still validate the exact engine and model themselves.
internal sealed record HubCompatibilityReport(
    string Model, string ProductDevice, string BuildIncremental, string BuildDisplayId, bool FirmwareApproved,
    bool EngineSupported, string? EngineProfile, string? EngineProfileValidation,
    string? EngineCompatibilityReason, bool GazePreflightPassed,
    string? GazeEnvironmentError, string? ModelPath, bool? ModelPathMounted,
    string? ModelDiscoveryError, bool HasActiveMagiskGaze)
{
    internal const string Format = "qpro-gaze-compatibility-diagnostic-v1";
    internal string Build => BuildIncremental;
    internal string FirmwareLabel => BuildDisplayId.Length == 0 ? Model : $"{Model} · {BuildDisplayId}";
    internal bool CanPrepareGaze => !HasActiveMagiskGaze && EngineSupported && GazePreflightPassed &&
        ModelPath is not null && ModelPathMounted == false && ModelDiscoveryError is null;
    internal bool NeedsAttention => !HasActiveMagiskGaze && (!CanPrepareGaze || EngineProfileValidation != "live-reference");
    internal string EngineValidationLabel => !EngineSupported ? "No validated engine profile" :
        EngineProfileValidation == "live-reference" ? "Live reference profile" : "Firmware analysis only";
    internal string Summary => HasActiveMagiskGaze
        ? "An active Magisk gaze module was detected. The Hub's temporary gaze method is not needed."
        : !EngineSupported
        ? "The Hub's independent gaze method is unavailable for this tracking engine. Other features have separate checks."
        : !GazePreflightPassed ? "The tracking engine is recognized, but the current gaze setup needs attention."
        : ModelDiscoveryError is not null || ModelPath is null ? "The engine is recognized, but its eye model could not be selected."
        : ModelPathMounted != false ? "An eye-model overlay is present; gaze preparation is unavailable."
        : EngineProfileValidation == "firmware-analysis"
            ? "The engine passed firmware analysis. Live behavior is not yet verified; the eye patch is not prepared."
            : "The engine matches a live reference profile. The eye patch still needs preparation and a live input check.";
    internal string NextStep => HasActiveMagiskGaze
        ? "Leave Independent Eye Gaze off in the Hub. Skip Check gaze setup and Prepare gaze; choose your other features on Live tracking. Confirm your Magisk module's eye tracking in VRCFaceTracking's preview. To switch to the Hub method, disable the Magisk gaze module and reboot first."
        : !EngineSupported
        ? "Leave Independent Eye Gaze off. Copy this report with the exact firmware build. Check a Magisk gaze module's support before using it."
        : !GazePreflightPassed
            ? "Use one gaze method at a time. Leave Hub gaze off while a Magisk gaze module is active; disable that module and reboot before using the Hub method. See the detailed result."
        : ModelDiscoveryError is not null || ModelPath is null || ModelPathMounted != false
            ? "Keep Independent Eye Gaze off. Check the model-discovery detail and copy this report; do not supply or replace engine files."
            : "Press Prepare gaze if you want the Hub method, then enable Independent Eye Gaze and Start tracking. This check changed no headset tracking.";

    internal static bool TryParse(string json, out HubCompatibilityReport? report)
    {
        report = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 262144) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            ReportJson.RequireKeys(root, "format", "firmware", "firmwareApproved", "engine", "engineSupported",
                "engineProfileValidation", "engineCompatibilityReason", "modelPath", "modelPathMounted",
                "modelDiscoveryError", "gazeEnvironment", "gazeEnvironmentError", "gazePreflightPassed",
                "modelPatchValidated", "headsetTrackingChanged");
            if (ReportJson.Text(root.GetProperty("format"), 64) != Format ||
                ReportJson.Boolean(root.GetProperty("modelPatchValidated")) ||
                ReportJson.Boolean(root.GetProperty("headsetTrackingChanged"))) return false;

            var firmware = root.GetProperty("firmware");
            ReportJson.RequireKeys(firmware, "model", "productDevice", "buildIncremental", "buildDisplayId", "buildFingerprint");
            var model = ReportJson.Text(firmware.GetProperty("model"), 128);
            var product = ReportJson.Text(firmware.GetProperty("productDevice"), 128);
            var build = ReportJson.Text(firmware.GetProperty("buildIncremental"), 128);
            var display = ReportJson.Text(firmware.GetProperty("buildDisplayId"), 256, allowEmpty: true);
            _ = ReportJson.Text(firmware.GetProperty("buildFingerprint"), 1024);
            if (!model.Equals("Quest Pro", StringComparison.OrdinalIgnoreCase) &&
                !product.Equals("seacliff", StringComparison.OrdinalIgnoreCase)) return false;
            var firmwareApproved = ReportJson.Boolean(root.GetProperty("firmwareApproved"));
            var supported = ReportJson.Boolean(root.GetProperty("engineSupported"));
            var engine = root.GetProperty("engine");
            ReportJson.RequireKeys(engine, supported ? ["path", "size", "sha256", "profile"] : ["path", "size", "sha256"]);
            if (ReportJson.Text(engine.GetProperty("path"), 256) != "/odm/lib64/libtrackingengines.so" ||
                !engine.GetProperty("size").TryGetInt64(out var size) || size < 1 || size > 1073741824 ||
                !Regex.IsMatch(ReportJson.Text(engine.GetProperty("sha256"), 64), "^[0-9a-fA-F]{64}$")) return false;
            var profile = supported ? ReportJson.Text(engine.GetProperty("profile"), 128) : null;
            var validation = ReportJson.NullableText(root.GetProperty("engineProfileValidation"), 64);
            var reason = ReportJson.NullableText(root.GetProperty("engineCompatibilityReason"), 16384, multiline: true);
            if (supported ? validation is not ("live-reference" or "firmware-analysis") || reason is not null
                : validation is not null || reason is null) return false;

            var path = ReportJson.NullableText(root.GetProperty("modelPath"), 1024);
            if (path is not null && (!Regex.IsMatch(path, "^/odm/etc/eyetracking/runtime/models/[A-Za-z0-9_./-]+/bolt\\.ptl$") ||
                path.Split('/').Any(part => part is "." or ".."))) return false;
            var mountedValue = root.GetProperty("modelPathMounted");
            bool? mounted = mountedValue.ValueKind == JsonValueKind.Null ? null : ReportJson.Boolean(mountedValue);
            var modelError = ReportJson.NullableText(root.GetProperty("modelDiscoveryError"), 16384, multiline: true);
            if (path is null ? mounted is not null || modelError is null
                : modelError is null ? mounted is null : mounted is not null) return false;

            var environment = root.GetProperty("gazeEnvironment");
            var environmentError = ReportJson.NullableText(root.GetProperty("gazeEnvironmentError"), 16384, multiline: true);
            var preflight = ReportJson.Boolean(root.GetProperty("gazePreflightPassed"));
            bool activeMagiskGaze = false;
            if (environment.ValueKind == JsonValueKind.Null)
            {
                if (preflight || environmentError is null) return false;
            }
            else
            {
                var stockSelection = ValidateEnvironment(environment, out activeMagiskGaze);
                if (preflight != (environmentError is null) || preflight && !stockSelection) return false;
            }
            report = new(model, product, build, display, firmwareApproved,
                supported, profile, validation, reason, preflight, environmentError, path, mounted, modelError, activeMagiskGaze);
            return true;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or
            FormatException or ArgumentException or OverflowException) { return false; }
    }

    private static bool ValidateEnvironment(JsonElement environment, out bool activeGazeModule)
    {
        ReportJson.RequireKeys(environment, "scanComplete", "modules", "relevantMounts", "transparentOverlayMounts",
            "overlayfsOdmUpperState", "overlayfsOdmUpperInspectedPath", "experimentalModelProperty");
        if (!ReportJson.Boolean(environment.GetProperty("scanComplete"))) throw new FormatException("Incomplete scan.");
        var modules = ReportJson.Array(environment.GetProperty("modules"), 128);
        activeGazeModule = false;
        foreach (var module in modules.EnumerateArray())
        {
            ReportJson.RequireKeys(module, "directory", "id", "name", "enabled", "pendingRemoval", "gazeRelevant", "relevantFiles");
            _ = ReportJson.Text(module.GetProperty("directory"), 128);
            _ = ReportJson.Text(module.GetProperty("id"), 128);
            _ = ReportJson.Text(module.GetProperty("name"), 256, allowEmpty: true);
            foreach (var key in new[] { "enabled", "pendingRemoval", "gazeRelevant" }) _ = ReportJson.Boolean(module.GetProperty(key));
            activeGazeModule |= module.GetProperty("enabled").GetBoolean() && module.GetProperty("gazeRelevant").GetBoolean();
            _ = ReportJson.TextList(module.GetProperty("relevantFiles"), 5, 128);
        }
        foreach (var key in new[] { "relevantMounts", "transparentOverlayMounts" })
            foreach (var mount in ReportJson.Array(environment.GetProperty(key), 256).EnumerateArray())
            {
                ReportJson.RequireKeys(mount, "source", "target", "filesystem", "options");
                foreach (var field in new[] { "source", "target", "filesystem", "options" })
                    _ = ReportJson.Text(mount.GetProperty(field), 4096);
            }
        if (ReportJson.Text(environment.GetProperty("overlayfsOdmUpperState"), 32) is not ("empty" or "notEmpty" or "absent"))
            throw new FormatException("Unknown overlay state.");
        _ = ReportJson.Text(environment.GetProperty("overlayfsOdmUpperInspectedPath"), 1024);
        var selection = ReportJson.Text(environment.GetProperty("experimentalModelProperty"), 32, allowEmpty: true).ToLowerInvariant();
        if (selection is not ("" or "0" or "1" or "false" or "true")) throw new FormatException("Unknown gaze selection.");
        return !activeGazeModule && environment.GetProperty("relevantMounts").GetArrayLength() == 0 &&
            selection is "" or "0" or "false";
    }
}

internal static class ReportJson
{
    internal static void RequireKeys(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Object required.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!keys.Contains(property.Name, StringComparer.Ordinal) || !found.Add(property.Name))
                throw new FormatException("Unexpected or duplicate field.");
        if (found.Count != keys.Length) throw new FormatException("Incomplete report.");
    }
    internal static bool Boolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw new FormatException("Boolean required.");
    internal static string Text(JsonElement value, int limit, bool allowEmpty = false, bool multiline = false)
    {
        if (value.ValueKind != JsonValueKind.String) throw new FormatException("Text required.");
        var text = value.GetString()!;
        if ((!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Length > limit ||
            text.Any(character => char.IsControl(character) && !(multiline && character is '\r' or '\n' or '\t')))
            throw new FormatException("Invalid text.");
        return text;
    }
    internal static string? NullableText(JsonElement value, int limit, bool multiline = false) =>
        value.ValueKind == JsonValueKind.Null ? null : Text(value, limit, multiline: multiline);
    internal static JsonElement Array(JsonElement value, int limit) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= limit ? value : throw new FormatException("Invalid list.");
    internal static string[] TextList(JsonElement value, int count, int length) =>
        Array(value, count).EnumerateArray().Select(item => Text(item, length, multiline: true)).ToArray();
}
