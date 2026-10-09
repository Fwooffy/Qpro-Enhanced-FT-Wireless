using System.Text.Json;

namespace QproFaceTracking.Hub;

// A completed script report describes headset selection, not avatar behavior
// or proof that every factory setting and PC tracking module is original.
internal sealed record HubGazeInspection(string Firmware, string ExperimentalSelection,
    bool QproSessionRecorded, IReadOnlyList<string> MagiskGazeModules, IReadOnlyList<string> UnverifiedMounts)
{
    internal const string Prefix = "QPRO_GAZE_SETUP ";
    internal const string RecoveryHelp =
        "Recover Qpro gaze: Restores the previous eye-model state saved for a recorded Qpro session.\n\n" +
        "Reset legacy gaze: Chooses normal, nonexperimental gaze selection for an older session without a record, after confirmation.\n\n" +
        "Neither button disables a Magisk module or uninstalls the PC module.";

    internal static HubGazeInspection? ParseLine(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal) || line.Length > 65536) return null;
        try
        {
            using var document = JsonDocument.Parse(line[Prefix.Length..], new JsonDocumentOptions { MaxDepth = 8 });
            var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object) return null;
            string[] fields = ["schema", "firmware", "experimentalSelection", "qproSessionRecorded", "magiskGazeModules", "unverifiedMounts"];
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in data.EnumerateObject())
                if (!(fields.Contains(property.Name, StringComparer.Ordinal) || property.Name == "socialFiltering") ||
                    !found.Add(property.Name)) return null;
            if (fields.Any(field => !found.Contains(field))) return null;
            if (data.TryGetProperty("socialFiltering", out var filtering)) _ = ReadText(filtering);
            if (data.GetProperty("schema").GetInt32() != 1) return null;
            var firmware = ReadText(data.GetProperty("firmware"));
            var selection = ReadText(data.GetProperty("experimentalSelection"));
            var recorded = data.GetProperty("qproSessionRecorded").GetBoolean();
            var modules = ReadList(data.GetProperty("magiskGazeModules"));
            var mounts = ReadList(data.GetProperty("unverifiedMounts"));
            return new(firmware, selection, recorded, modules, mounts);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string ReadText(JsonElement value)
    {
        var text = value.GetString() ?? throw new FormatException("Missing gaze report text.");
        if (text.Length > 512 || text.Any(char.IsControl)) throw new FormatException("Invalid gaze report text.");
        return text;
    }

    private static IReadOnlyList<string> ReadList(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 256)
            throw new FormatException("Invalid gaze report list.");
        return value.EnumerateArray().Select(ReadText).ToArray();
    }

    private bool ExperimentalEnabled => ExperimentalSelection is "true" or "1";
    private bool NormalSelection => ExperimentalSelection is "" or "false" or "0";
    internal bool HasActiveMagiskGaze => MagiskGazeModules.Count > 0;
    // A Magisk method is expected to differ from the stock model. Its presence
    // is not a failed Hub setup; a remaining Qpro recovery record still is.
    internal bool NeedsAttention => QproSessionRecorded ||
        (!HasActiveMagiskGaze && (UnverifiedMounts.Count > 0 || !NormalSelection)) ||
        (!NormalSelection && !ExperimentalEnabled);

    internal string PopupText()
    {
        string method, next;
        if (HasActiveMagiskGaze)
        {
            method = "Magisk independent gaze";
            var names = string.Join(", ", MagiskGazeModules.Take(3));
            if (MagiskGazeModules.Count > 3) names += $" (+{MagiskGazeModules.Count - 3} more)";
            method += "\nModule: " + names;
            next = "A Magisk gaze module is active. Skip Check gaze setup and Prepare gaze. Leave Independent Eye Gaze unchecked in the Hub. " +
                "You can still use Qpro tongue, camera cheek, pupil and eyebrow features. " +
                "Confirm the module's eye tracking in VRCFaceTracking's preview. " +
                "To use the Hub method instead, disable that gaze module in Magisk, reboot, then run Check gaze setup again.";
            if (QproSessionRecorded) next += " A Qpro record also remains; stop its owning Hub and use Recover Qpro gaze.";
        }
        else if (QproSessionRecorded)
        {
            method = "Qpro gaze session recorded";
            next = "Stop tracking in the Hub that started this session. If the record remains, press Recover Qpro gaze, " +
                "then run Check gaze setup again. Recovery verifies ownership before restoring anything.";
        }
        else if (UnverifiedMounts.Count > 0)
        {
            method = "Another model or tracking-engine overlay; method unverified";
            next = "Leave Independent Eye Gaze unchecked. Check Activity to identify the owning gaze method, " +
                "disable it through its own module and reboot before trying the Hub method.";
        }
        else if (ExperimentalEnabled)
        {
            method = "Experimental gaze selection enabled; no Qpro recovery record";
            next = "If an older Qpro session left this enabled and you want normal gaze selection, use Reset legacy gaze " +
                "and read its confirmation. If you do not know which method enabled it, keep the Hub gaze option off and check Activity.";
        }
        else if (NormalSelection)
        {
            method = "Normal headset gaze selection";
            next = "For the Hub's temporary independent gaze, press Prepare gaze, then enable Independent Eye Gaze in Live tracking " +
                "and press Start tracking. For tongue or pupil tracking only, leave Independent Eye Gaze unchecked.";
        }
        else
        {
            method = "Unknown headset gaze selection";
            next = "Keep Independent Eye Gaze unchecked and share the Activity result with the headset's exact firmware build before changing gaze.";
        }
        return $"Detected headset gaze: {method}\nFirmware build: {Firmware}\n\nNext step:\n{next}\n\n{RecoveryHelp}\n\n" +
            "This check changes no headset setting and does not verify convergence or every factory setting.";
    }

    internal static string FailedPopupText => "Detected headset gaze: Could not verify\n\n" +
        "This optional check is only needed to investigate gaze. If you already use a Magisk gaze module, leave Independent Eye Gaze off in the Hub and skip Check gaze setup and Prepare gaze.\n\n" +
        "To investigate the Hub method:\nCheck Activity for the exact error. Keep the Quest awake, connect USB or wireless ADB, " +
        "allow Shell / ADB Shell in Magisk, then run Check gaze setup again. If files are missing, extract the complete ZIP again.\n\n" + RecoveryHelp;
}
