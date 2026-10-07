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
            using var document = JsonDocument.Parse(line[Prefix.Length..]);
            var data = document.RootElement;
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
    internal bool NeedsAttention => QproSessionRecorded || UnverifiedMounts.Count > 0 || !NormalSelection;

    internal string PopupText()
    {
        string method, next;
        if (MagiskGazeModules.Count > 0)
        {
            method = "Magisk independent gaze";
            var names = string.Join(", ", MagiskGazeModules.Take(3));
            if (MagiskGazeModules.Count > 3) names += $" (+{MagiskGazeModules.Count - 3} more)";
            method += "\nModule: " + names;
            next = "The Magisk module already provides independent gaze. Skip Prepare gaze. Leave Independent Eye Gaze unchecked in the Hub. " +
                "You can still use Qpro tongue, camera cheek, pupil and eyebrow features. " +
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
        "Next step:\nCheck Activity for the exact error. Keep the Quest awake, connect USB or wireless ADB, " +
        "allow Shell / ADB Shell in Magisk, then run Check gaze setup again. If files are missing, extract the complete ZIP again.\n\n" + RecoveryHelp;
}
